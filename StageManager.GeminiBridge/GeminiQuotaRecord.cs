using System.Text.Json.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GeminiQuotaRecord(string Status, DateTimeOffset UpdatedUtc, string Session, GeminiQuotaMeter[] Meters)
{
    public static bool IsValid(GeminiQuotaRecord r, DateTimeOffset now) =>
        r.Status is "Connected" or "LoginRequired" or "Unavailable" &&
        r.UpdatedUtc <= now.AddMinutes(1) && r.UpdatedUtc >= now.AddMinutes(-10) &&
        r.Session is { Length: 32 } && r.Session.All(Uri.IsHexDigit) &&
        r.Meters is { Length: <= 8 } && (r.Status != "Connected" || r.Meters.Length > 0) &&
        (r.Status == "Connected" || r.Meters.Length == 0) &&
        r.Meters.All(m => m is not null && m.Kind is 1 or 2 && m.UsedPercent is >= 0 and <= 100 &&
            (m.ResetUtc is null || m.ResetUtc >= now.AddDays(-1) && m.ResetUtc <= now.AddDays(40))) &&
        r.Meters.Select(m => m.Kind).Distinct().Count() == r.Meters.Length;
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GeminiQuotaMeter(int Kind, decimal UsedPercent, DateTimeOffset? ResetUtc);
