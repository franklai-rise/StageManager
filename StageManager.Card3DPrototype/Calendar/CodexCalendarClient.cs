using System.Diagnostics;
using System.Text.Json;
using StageManager.Card3DPrototype.AiUsage;

namespace StageManager.Card3DPrototype.Calendar;

// Public app-server protocol only. No model turn, credential extraction or private desktop socket.
internal sealed class CodexCalendarClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CodexUsageClient.BoundedLineReader _reader;
    private readonly Task _stderr;
    private string _thread = "";
    private int _id;
    private readonly Dictionary<string, (string Server, string Tool)> _tools = new();
    private CodexCalendarClient(Process process)
    {
        _process = process; _reader = new(process.StandardOutput, 16 * 1024 * 1024);
        _stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }
    internal static async Task<CodexCalendarClient> OpenAsync(string cliPath, CancellationToken token, Action<string>? inventory = null)
    {
        var info = CodexUsageClient.CreateStartInfo(cliPath) ?? throw new CalendarProviderException("CodexUnavailable");
        var process = new Process { StartInfo = info };
        if (!process.Start()) { process.Dispose(); throw new CalendarProviderException("CodexUnavailable"); }
        var client = new CodexCalendarClient(process);
        try
        {
            await client.RequestAsync("initialize", new { clientInfo = new { name = "stage_calendar", version = "4.8.0" } }, token);
            await client.SendAsync(new { method = "initialized", @params = new { } }, token);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "Calendar", "Bridge");
            Directory.CreateDirectory(folder);
            var configuration = await client.RequestAsync("config/read", new { includeLayers = false }, token);
            var isolated = new Dictionary<string, object>();
            if (configuration.GetProperty("config").TryGetProperty("mcp_servers", out var configured))
                foreach (var server in configured.EnumerateObject())
                    if (server.Name != "codex_apps") isolated["mcp_servers." + server.Name + ".enabled"] = false;
            var started = await client.RequestAsync("thread/start", new { cwd = folder, ephemeral = true, sandbox = "read-only", approvalPolicy = "never", config = isolated }, token);
            client._thread = started.GetProperty("thread").GetProperty("id").GetString() ?? throw new CalendarProviderException("CodexUnavailable");
            string? cursor = null;
            for (int page = 0; page < 20; page++)
            {
                var status = await client.RequestAsync("mcpServerStatus/list", new { threadId = client._thread, detail = "toolsAndAuthOnly", limit = 100, cursor }, token);
                foreach (var server in status.GetProperty("data").EnumerateArray())
                {
                    var name = server.GetProperty("name").GetString()!;
                    inventory?.Invoke("Server: " + name);
                    if (!server.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Object) continue;
                    foreach (var tool in tools.EnumerateObject())
                    {
                    if (tool.Name.Contains("outlook", StringComparison.OrdinalIgnoreCase)) inventory?.Invoke("Tool: " + tool.Name);
                    foreach (var operation in new[] { "list_calendars", "search_events", "fetch_event", "fetch_events_batch" })
                    {
                        if (name == "codex_apps" && tool.Name == "microsoft_outlook_calendar." + operation)
                            client._tools[operation] = (name, tool.Name);
                    }
                    }
                }
                cursor = status.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (cursor is null) break;
            }
            if (!client._tools.ContainsKey("list_calendars") || !client._tools.ContainsKey("search_events"))
                throw new CalendarProviderException("CodexCalendarUnavailable");
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }
    internal string[] Operations => _tools.Keys.ToArray();
    internal async Task<JsonElement> CallAsync(string operation, object arguments, CancellationToken token)
    {
        if (!_tools.TryGetValue(operation, out var tool)) throw new CalendarProviderException("CodexCalendarUnavailable");
        var response = await RequestAsync("mcpServer/tool/call", new { threadId = _thread, server = tool.Server, tool = tool.Tool, arguments }, token);
        if (response.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            throw new CalendarProviderException("CodexCalendarReadFailed");
        if (response.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object) return structured.Clone();
        if (response.TryGetProperty("content", out var content))
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    try { using var json = JsonDocument.Parse(text.GetString()!); return json.RootElement.Clone(); } catch (JsonException) { }
        throw new CalendarProviderException("InvalidResponse");
    }
    private async Task<JsonElement> RequestAsync(string method, object args, CancellationToken token)
    {
        var id = ++_id;
        await SendAsync(new { id, method, @params = args }, token);
        while (true)
        {
            var line = await _reader.ReadAsync(token) ?? throw new CalendarProviderException("CodexUnavailable");
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var responseId)) continue;
            if (root.TryGetProperty("method", out _))
            {
                // Do not silently approve prompts. A required interaction must be handled in Codex itself.
                await SendAsync(new { id = responseId.Clone(), error = new { code = -32601, message = "Interactive approval is not supported by this read-only client." } }, token);
                throw new CalendarProviderException("CodexApprovalRequired");
            }
            if (responseId.ValueKind != JsonValueKind.Number || responseId.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out _)) throw new CalendarProviderException("CodexProtocolUnavailable");
            return root.GetProperty("result").Clone();
        }
    }
    private async Task SendAsync(object value, CancellationToken token)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
        await _process.StandardInput.FlushAsync(token);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            try { _process.StandardInput.Close(); } catch (IOException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(400)); }
            catch (TimeoutException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            try { await _stderr.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception e) when (e is IOException or TimeoutException) { }
        }
        finally { _process.Dispose(); }
    }
}
