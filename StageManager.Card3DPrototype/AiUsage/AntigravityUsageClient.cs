using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace StageManager.Card3DPrototype.AiUsage;

internal static class AntigravityUsageClient
{
    public static async Task<AiUsageSnapshot> ReadAsync(CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        var token = timeout.Token;
        // Discover only this user's installed app, not arbitrary processes/ports. Tokens stay in memory.
        const string script = """
        $ErrorActionPreference='Stop'
        $expected=Join-Path $env:LOCALAPPDATA 'Programs\antigravity\resources\bin\language_server.exe'
        $items=@(Get-CimInstance Win32_Process -Filter "Name='language_server.exe'" | Where-Object { $_.ExecutablePath -eq $expected } | Select-Object -First 2 | ForEach-Object {
          $m=[regex]::Match($_.CommandLine,'--csrf_token(?:=|\s+)("[^"]+"|\S+)')
          if($m.Success) { $ports=@(Get-NetTCPConnection -OwningProcess $_.ProcessId -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique); @{Token=$m.Groups[1].Value.Trim('"');Ports=$ports;Scope=[string]$_.CreationDate.Ticks} }
        })
        ConvertTo-Json -InputObject $items -Compress -Depth 3
        """;
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.StartInfo.ArgumentList.Add("-NoProfile"); process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-EncodedCommand"); process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        process.Start();
        string discovery;
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, token);
            var errors = ReadBoundedAsync(process.StandardError, token);
            await process.WaitForExitAsync(token);
            discovery = await output; await errors;
            if (process.ExitCode != 0) return AiUsageService.Empty("antigravity", "Antigravity unavailable");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        using var servers = JsonDocument.Parse(discovery);
        if (servers.RootElement.GetArrayLength() == 0) return AiUsageService.Empty("antigravity", "Antigravity not running");
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.Host == "127.0.0.1" };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var server in servers.RootElement.EnumerateArray())
        foreach (var port in server.GetProperty("Ports").EnumerateArray().Take(4))
        {
            var number = port.GetInt32(); if (number is <= 0 or > 65535) continue;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{number}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary");
                request.Headers.Add("X-Codeium-Csrf-Token", server.GetProperty("Token").GetString());
                request.Headers.Add("Connect-Protocol-Version", "1"); request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream);
                using var json = JsonDocument.Parse(await ReadBoundedAsync(reader, token));
                var result = Parse(json.RootElement, DateTimeOffset.UtcNow, server.GetProperty("Scope").GetString() ?? "local");
                if (result.HasData) return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException) { }
        }
        return AiUsageService.Empty("antigravity", "Antigravity unavailable");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true) { var n = await reader.ReadAsync(buffer.AsMemory(), token); if (n == 0) break;
            if (result.Length + n > 131072) throw new InvalidDataException(); result.Append(buffer, 0, n); }
        return result.ToString();
    }

    internal static AiUsageSnapshot Parse(JsonElement root, DateTimeOffset now, string session)
    {
        var windows = new List<AiQuotaWindow>();
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return AiUsageService.Empty("antigravity", "Antigravity unavailable");
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("bucketId", out var idElement) || idElement.ValueKind != JsonValueKind.String) continue;
                var id = idElement.GetString()!;
                if (id is not ("gemini-5h" or "gemini-weekly" or "3p-5h" or "3p-weekly")) continue;
                if (!bucket.TryGetProperty("remainingFraction", out var fraction) && bucket.TryGetProperty("remaining", out var remaining) && remaining.ValueKind == JsonValueKind.Object)
                    remaining.TryGetProperty("remainingFraction", out fraction);
                if (fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDecimal(out var value) || value is < 0 or > 1) continue;
                DateTimeOffset? reset = bucket.TryGetProperty("resetTime", out var resetElement) && resetElement.ValueKind == JsonValueKind.String && resetElement.TryGetDateTimeOffset(out var at) ? at : null;
                var weekly = id.EndsWith("weekly");
                if (windows.Any(w => w.LimitId == id)) continue;
                windows.Add(new(id, (id.StartsWith("gemini") ? "Gemini" : "Claude / GPT") + (weekly ? " · 7d" : " · 5h"), value * 100, weekly ? 10080 : 300, reset));
            }
        }
        if (windows.Count == 0) return AiUsageService.Empty("antigravity", "Antigravity unavailable");
        var ordered = windows.OrderBy(w => w.RemainingPercent).ThenBy(w => w.LimitId, StringComparer.Ordinal).ToArray();
        var primary = ordered[0];
        return new("antigravity", "Antigravity", "antigravity", $"{primary.RemainingPercent:0}%", "Connected", now,
            Array.Empty<AiUsageDetail>(), primary.RemainingPercent, "percent",
            "antigravity:" + session + "|" + primary.LimitId + "|" + primary.ResetsAt?.ToUnixTimeSeconds(), ordered);
    }
}
