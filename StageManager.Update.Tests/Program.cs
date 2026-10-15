using System.Diagnostics;
using StageManager.Card3DPrototype.Updates;

if (args.Length == 2 && args[0] is "--apply-update" or "--recover-update")
    return UpdateTransaction.Apply(args[1], args[0] == "--recover-update");
if (args is ["--updated", var journal])
{
    File.WriteAllText(UpdateTransaction.SettingsPath, "new settings");
    var scenario = Environment.GetEnvironmentVariable("STAGE_UPDATE_TEST_SCENARIO");
    if (scenario is "crash" or "no-settings") return 7;
    if (scenario == "timeout") { Thread.Sleep(45000); return 8; }
    UpdateTransaction.SignalReady(journal); Thread.Sleep(1000); return 0;
}
if (args is ["--old"])
{
    var tx = UpdateTransaction.Create(Environment.ProcessPath!, "", "4.6.1");
    File.Copy(Environment.ProcessPath!, tx.Payload);
    // Different bytes, same runnable fixture/version. No user's executable or settings.
    using (var append = new FileStream(tx.Payload, FileMode.Append)) append.WriteByte(0);
    tx.NewHash = UpdateTransaction.Hash(tx.Payload); tx.Save();
    await tx.ArmAsync(default); return 0;
}
if (args is not ["--scenarios"]) { UpdateTransaction.RecoverInterrupted(); return 0; }
foreach (var scenario in new[] { "success", "crash", "timeout", "no-settings", "interrupted" })
{
    var root = Path.Combine(Path.GetTempPath(), "StageManager-updater-fixture-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("STAGE_UPDATE_TEST_ROOT", root);
    Environment.SetEnvironmentVariable("STAGE_UPDATE_TEST_SCENARIO", scenario);
    if (scenario != "no-settings") File.WriteAllText(UpdateTransaction.SettingsPath, "old settings");
    var target = Path.Combine(root, "app.exe"); File.Copy(Environment.ProcessPath!, target);
    Console.WriteLine("Testing updater process scenario: " + scenario);
    if (scenario == "interrupted")
    {
        var pending = UpdateTransaction.Create(target, "", "4.6.1");
        File.Copy(target, pending.Helper); File.Copy(target, pending.Payload);
        using (var append = new FileStream(pending.Payload, FileMode.Append)) append.WriteByte(0);
        pending.NewHash = UpdateTransaction.Hash(pending.Payload);
        pending.HadSettings = true; File.Copy(UpdateTransaction.SettingsPath, pending.SettingsBackup);
        File.Copy(pending.Payload, pending.Candidate); File.Replace(pending.Candidate, target, pending.Backup);
        File.WriteAllText(UpdateTransaction.SettingsPath, "interrupted settings");
        pending.State = "Installed"; pending.Save();
    }
    using var old = scenario == "interrupted" ? UpdateTransaction.Start(target) : UpdateTransaction.Start(target, "--old");
    var clock = Stopwatch.StartNew(); UpdateTransaction? result = null;
    while (clock.Elapsed < TimeSpan.FromSeconds(55))
    {
        if (Directory.Exists(UpdateTransaction.Root))
            foreach (var file in Directory.EnumerateFiles(UpdateTransaction.Root, "transaction.json", SearchOption.AllDirectories))
            { try { result = UpdateTransaction.Load(file); } catch (IOException) { } }
        if (result?.State is "Committed" or "RolledBack" or "Failed") break;
        await Task.Delay(100);
    }
    var expected = scenario == "success" ? "Committed" : "RolledBack";
    if (result?.State != expected) throw new Exception($"{scenario}: expected {expected}, got {result?.State}; retained {root}");
    if (UpdateTransaction.Hash(target) != (scenario == "success" ? result.NewHash : result.OldHash)) throw new Exception("Incorrect installed bytes");
    if (scenario == "no-settings")
    { if (File.Exists(UpdateTransaction.SettingsPath)) throw new Exception("Absent settings not restored"); }
    else if (File.ReadAllText(UpdateTransaction.SettingsPath) != (scenario == "success" ? "new settings" : "old settings")) throw new Exception("Settings rollback failed");
    Console.WriteLine($"PASS {scenario}: {result.State}; EXE hash and settings verified");
    // Retain only on failure; all paths were created by this scenario.
    await Task.Delay(1500); Directory.Delete(root, true);
}
return 0;
