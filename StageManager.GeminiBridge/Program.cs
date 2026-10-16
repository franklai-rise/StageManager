using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32;

const string origin = "chrome-extension://koplbglpgeojafoahfepmmamfidoeolo/";
const string host = "com.franklai.stage_manager.gemini";
var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai");
if (args is ["--register"])
{
    var manifestPath = Path.Combine(AppContext.BaseDirectory, host + ".json");
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { name = host, description = "Stage Manager Gemini quota bridge", path = Environment.ProcessPath, type = "stdio", allowed_origins = new[] { origin } }));
    foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge" })
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\" + browser + @"\NativeMessagingHosts\" + host);
        key.SetValue("", manifestPath);
    }
    Console.WriteLine("Registered for this Windows user: Chrome and Edge.");
    return;
}
if (args.Length == 0 || args[0] != origin) { Environment.ExitCode = 2; return; }
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    using var input = Console.OpenStandardInput();
    using var output = Console.OpenStandardOutput();
    var header = new byte[4];
    await input.ReadExactlyAsync(header, deadline.Token);
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length is < 2 or > 16384) throw new InvalidDataException();
    var bytes = new byte[length];
    await input.ReadExactlyAsync(bytes, deadline.Token);
    using var json = JsonDocument.Parse(bytes);
    var root = json.RootElement;
    bool enabled = false;
    var settingsPath = Path.Combine(directory, "settings.json");
    if (File.Exists(settingsPath))
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        bool Flag(string name, bool fallback = false) => settings.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.True } ? true :
            settings.RootElement.EnumerateObject().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? false : fallback;
        enabled = Flag("ShowAiUsageCard") && Flag("AiUsageEnabled") && Flag("AiUsageGeminiEnabled", true);
    }
    var op = root.GetProperty("op").GetString();
    if (op == "snapshot" && enabled)
    {
        // Deserialize into a strict allow-list and reserialize: never persist arbitrary fields/tokens.
        var record = JsonSerializer.Deserialize<GeminiQuotaRecord>(root.GetProperty("data"));
        if (record is null || !GeminiQuotaRecord.IsValid(record, DateTimeOffset.UtcNow)) throw new InvalidDataException();
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "gemini-browser-quota.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(record)); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    else if (op != "status" && op != "snapshot") throw new InvalidDataException();
    var reply = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, enabled });
    BinaryPrimitives.WriteInt32LittleEndian(header, reply.Length);
    await output.WriteAsync(header, deadline.Token); await output.WriteAsync(reply, deadline.Token); await output.FlushAsync(deadline.Token);
}
catch { Environment.ExitCode = 1; } // No payloads or authentication information in logs/stdout.
