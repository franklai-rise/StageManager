using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace StageManager.Card3DPrototype.Updates;

internal sealed class UpdateTransaction
{
    public string Id { get; set; } = "";
    public string Target { get; set; } = "";
    public string OldHash { get; set; } = "";
    public string NewHash { get; set; } = "";
    public string Version { get; set; } = "";
    public int OldPid { get; set; }
    public long OldStarted { get; set; }
    public bool HadSettings { get; set; }
    public string State { get; set; } = "Prepared";
    private static string DataRoot =>
#if UPDATE_TEST_HOST
        Environment.GetEnvironmentVariable("STAGE_UPDATE_TEST_ROOT") ?? throw new InvalidOperationException("Missing isolated test root");
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai");
#endif
    public static string Root => Path.Combine(DataRoot, "Updates");
    public static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    [System.Text.Json.Serialization.JsonIgnore] public string DirectoryPath => Path.Combine(Root, Id);
    [System.Text.Json.Serialization.JsonIgnore] public string Journal => Path.Combine(DirectoryPath, "transaction.json");
    [System.Text.Json.Serialization.JsonIgnore] public string Payload => Path.Combine(DirectoryPath, "download.exe");
    [System.Text.Json.Serialization.JsonIgnore] public string Helper => Path.Combine(DirectoryPath, "update-helper.exe");
    [System.Text.Json.Serialization.JsonIgnore] public string Backup => Target + ".rollback-" + Id;
    [System.Text.Json.Serialization.JsonIgnore] public string Candidate => Target + ".new-" + Id;
    [System.Text.Json.Serialization.JsonIgnore] public string SettingsBackup => Path.Combine(DirectoryPath, "settings.backup.json");
    [System.Text.Json.Serialization.JsonIgnore] public string Ready => Path.Combine(DirectoryPath, "ready");
    [System.Text.Json.Serialization.JsonIgnore] public string Armed => Path.Combine(DirectoryPath, "armed");

    public static UpdateTransaction Create(string target, string hash, string version)
    {
        target = Path.GetFullPath(target); RejectReparse(target);
        // Verify write access without touching the installed binary.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".stage-update-probe-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        var t = new UpdateTransaction { Id = Guid.NewGuid().ToString("N"), Target = target, OldHash = Hash(target), NewHash = hash,
            Version = version, OldPid = Environment.ProcessId, OldStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks };
        RejectReparse(Root); Directory.CreateDirectory(Root);
        Directory.CreateDirectory(t.DirectoryPath);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(t.DirectoryPath).SetAccessControl(acl);
        return t;
    }
    public void Save()
    {
        var temp = Journal + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, Journal, true);
    }
    public static UpdateTransaction Load(string path)
    {
        path = Path.GetFullPath(path); RejectReparse(path);
        if (!path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path) != "transaction.json" || new FileInfo(path).Length > 32 * 1024)
            throw new InvalidDataException("Invalid update transaction.");
        var t = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        if (!Guid.TryParseExact(t.Id, "N", out _) || !string.Equals(path, t.Journal, StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(t.Target) || !t.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        RejectReparse(t.Target); RejectReparse(t.DirectoryPath);
        if (!System.Text.RegularExpressions.Regex.IsMatch(t.OldHash, "^[A-Fa-f0-9]{64}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(t.NewHash, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException();
        return t;
    }
    public static void RejectReparse(string path)
    {
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths cannot contain reparse points.");
    }
    public static string Hash(string file) { using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }
    public void ValidatePayload()
    {
        RejectReparse(Payload);
        if (!string.Equals(Hash(Payload), NewHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Download checksum failed.");
        var version = FileVersionInfo.GetVersionInfo(Payload);
        if ($"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}" != Version) throw new InvalidDataException("Executable version mismatch.");
    }
    public async Task ArmAsync(CancellationToken token)
    {
        await Task.Run(() =>
        {
            ValidatePayload();
            if (Hash(Target) != OldHash) throw new IOException("Installed executable changed.");
            File.Copy(Target, Helper, false); Save();
        }, token);
        using var helper = Start(Helper, "--apply-update", Journal);
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(Armed))
        {
            if (helper.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("Update helper could not start.");
            await Task.Delay(100, token);
        }
    }
    internal static Process Start(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Could not start application.");
    }
    public static int Apply(string journal, bool recovery)
    {
        UpdateTransaction? t = null; var authorized = false;
        try
        {
            t = Load(journal);
            if (!string.Equals(Environment.ProcessPath, t.Helper, StringComparison.OrdinalIgnoreCase) || Hash(t.Helper) != t.OldHash)
                throw new InvalidDataException("Helper identity mismatch.");
            authorized = true;
            using var transactionLock = new FileStream(Path.Combine(t.DirectoryPath, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (recovery)
            {
                // Recovery launcher exits immediately; give its image handle time to close.
                Thread.Sleep(1000); t.Rollback(); using var restored = Start(t.Target); return 0;
            }
            t.ValidatePayload();
            using var old = Process.GetProcessById(t.OldPid);
            if (old.StartTime.ToUniversalTime().Ticks != t.OldStarted ||
                !string.Equals(old.MainModule?.FileName, t.Target, StringComparison.OrdinalIgnoreCase) || Hash(t.Target) != t.OldHash)
                throw new InvalidDataException("Running process identity mismatch.");
            // All preparation happens before telling the application it is safe to exit.
            File.Copy(t.Payload, t.Candidate, false);
            if (Hash(t.Candidate) != t.NewHash) throw new InvalidDataException();
            t.HadSettings = File.Exists(SettingsPath);
            if (t.HadSettings) File.Copy(SettingsPath, t.SettingsBackup, true);
            t.State = "Waiting"; t.Save(); File.WriteAllText(t.Armed, t.Id);
            if (!old.WaitForExit(20000)) throw new IOException("Application is still running.");
            t.State = "Replacing"; t.Save();
            File.Replace(t.Candidate, t.Target, t.Backup);
            t.State = "Installed"; t.Save();
            using var next = Start(t.Target, "--updated", t.Journal);
            var clock = Stopwatch.StartNew();
            while (!next.HasExited && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (File.Exists(t.Ready) && File.ReadAllText(t.Ready) == t.Id)
                { t.State = "Committed"; t.Save(); return 0; }
                Thread.Sleep(100);
            }
            if (!next.HasExited) { next.Kill(); next.WaitForExit(5000); }
            t.Rollback(); using var fallback = Start(t.Target); return 1;
        }
        catch
        {
            if (t is not null && authorized)
            {
                try
                {
                    if (File.Exists(t.Backup)) { t.Rollback(); using var fallback = Start(t.Target); }
                    else { t.State = "Failed"; t.Save(); }
                }
                catch { /* Journal and backup stay available for startup recovery. */ }
            }
            return 1;
        }
    }
    private void Rollback()
    {
        if (!File.Exists(Backup) || Hash(Backup) != OldHash) throw new InvalidDataException("Rollback backup mismatch.");
        // Never replace an unrelated EXE placed here after the update.
        if (File.Exists(Target) && Hash(Target) is var current && current != NewHash && current != OldHash) throw new InvalidDataException();
        var restore = Candidate + ".restore"; File.Copy(Backup, restore, true); File.Move(restore, Target, true);
        if (HadSettings && File.Exists(SettingsBackup))
        { var temp = SettingsPath + ".update-restore"; File.Copy(SettingsBackup, temp, true); File.Move(temp, SettingsPath, true); }
        else if (!HadSettings && File.Exists(SettingsPath))
            File.Move(SettingsPath, Path.Combine(DirectoryPath, "failed-version-settings.json"), true);
        State = "RolledBack"; Save();
    }
    public static void SignalReady(string? journal)
    {
        if (journal is null) return;
        var t = Load(journal);
        if (t.State != "Installed" || !string.Equals(Environment.ProcessPath, t.Target, StringComparison.OrdinalIgnoreCase) || Hash(t.Target) != t.NewHash)
            throw new InvalidDataException("Ready signal identity mismatch.");
        File.WriteAllText(t.Ready, t.Id);
    }
    public static bool RecoverInterrupted()
    {
        if (!Directory.Exists(Root)) return false;
        foreach (var path in Directory.EnumerateFiles(Root, "transaction.json", SearchOption.AllDirectories))
        {
            UpdateTransaction t;
            try { t = Load(path); } catch { continue; }
            if (!string.Equals(t.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                t.State is not ("Replacing" or "Installed")) continue;
            // A live updater owns this transaction. Never race its replacement.
            try { using var check = new FileStream(Path.Combine(t.DirectoryPath, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return false; }
            if (t.State == "Replacing" && !File.Exists(t.Backup) && Hash(t.Target) == t.OldHash)
            { t.State = "RolledBack"; t.Save(); return false; }
            if (t.State == "Installed" && File.Exists(t.Ready) && File.ReadAllText(t.Ready) == t.Id && Hash(t.Target) == t.NewHash)
            { t.State = "Committed"; t.Save(); return false; }
            if (!File.Exists(t.Helper) || Hash(t.Helper) != t.OldHash) return false;
            using var helper = Start(t.Helper, "--recover-update", t.Journal); return true;
        }
        return false;
    }
}
