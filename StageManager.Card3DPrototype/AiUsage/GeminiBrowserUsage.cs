using System.Text.Json;

namespace StageManager.Card3DPrototype.AiUsage;

internal static class GeminiBrowserUsage
{
    internal static AiUsageSnapshot Read(string directory)
    {
        var path = Path.Combine(directory, "gemini-browser-quota.json");
        try
        {
            if (!File.Exists(path)) return AiUsageService.Empty("gemini", "Gemini browser connection required");
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException();
            var r = JsonSerializer.Deserialize<GeminiQuotaRecord>(File.ReadAllText(path));
            if (r is null || !GeminiQuotaRecord.IsValid(r, r.UpdatedUtc) || r.UpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(1)) throw new InvalidDataException();
            if (r.Status != "Connected") return AiUsageService.Empty("gemini", r.Status == "LoginRequired" ? "Gemini browser connection required" : "Gemini browser unavailable") with { ContinuityKey = "gemini:" + r.Session };
            var windows = r.Meters.OrderBy(m => m.Kind).Select(m => new AiQuotaWindow(m.Kind.ToString(), m.Kind == 1 ? "Current" : "Weekly", 100 - m.UsedPercent, m.Kind == 1 ? 300 : 10080, m.ResetUtc)).ToArray();
            var primary = windows[0];
            return new("gemini", "Gemini", "gemini", $"{primary.RemainingPercent:0}%", DateTimeOffset.UtcNow - r.UpdatedUtc > TimeSpan.FromMinutes(10) ? "Gemini browser stale" : "Connected", r.UpdatedUtc,
                windows.Select(w => new AiUsageDetail(w.Label, $"{w.RemainingPercent:0}% remaining")).ToArray(), primary.RemainingPercent, "percent",
                "gemini:" + r.Session + "|" + primary.ResetsAt?.ToUnixTimeSeconds(), windows);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return AiUsageService.Empty("gemini", "Gemini browser unavailable"); }
    }
}
