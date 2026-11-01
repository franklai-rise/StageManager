using System.Text.Json;
using StageManager.Card3DPrototype.Calendar;
using System.IO;

internal static class CodexCalendarTests
{
    public static void Run()
    {
        using var json = JsonDocument.Parse("""{"id":"event","subject":"Sample","start":{"dateTime":"2026-10-25T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-26T00:00:00","timeZone":"UTC"},"is_all_day":true,"web_link":"https://outlook.live.com/calendar/","bodyPreview":"plain"}""");
        var item = CodexCalendarProvider.ParseEvent(json.RootElement, "calendar")!;
        Check(item.IsAllDay && item.StartDate == new DateOnly(2026, 10, 25) && item.EndDate == new DateOnly(2026, 10, 26), "Connector all-day dates shifted.");
        Check(item.Summary == "plain" && item.WebLink.StartsWith("https://outlook.live.com/"), "Connector details missing.");
        using var cancelled = JsonDocument.Parse("""{"is_cancelled":true}""");
        Check(CodexCalendarProvider.ParseEvent(cancelled.RootElement, "calendar") is null, "Cancelled connector event displayed.");
        var folder = Path.Combine(Path.GetTempPath(), "Stage-CodexCalendar-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new CodexCalendarProvider("nonexistent", folder);
            bool missing = false;
            try { provider.GetCalendarsAsync(default).GetAwaiter().GetResult(); }
            catch (CalendarProviderException e) { missing = e.Code == "CodexRefreshRequired"; }
            Check(missing && !Directory.Exists(folder), "Opening offline mode created a process or fake calendar.");
            using var service = new CalendarService(_ => new CodexCalendarProvider("nonexistent", folder));
            service.Configure(true, "codex:nonexistent", []);
            Check(service.UsesCodex && !service.PeriodicRefreshEnabled, "Codex mode enabled a periodic query.");
            service.RefreshAsync().GetAwaiter().GetResult();
            Check(service.Current.Status != CalendarStatus.Connected, "Unconnected Codex is reported as connected.");
            Check(!Directory.Exists(folder), "Offline read wrote unencrypted cache.");
            service.Configure(false, "codex:nonexistent", []);
            Check(service.Current.Events.Count == 0, "Disabling retained calendar in memory.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    public static async Task LiveProbe()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        using var service = new CalendarService();
        service.Configure(true, "codex:", []);
        var month = await service.GetMonthAsync(CalendarDates.Today, true, deadline.Token);
        Console.WriteLine($"Calendar live result: {month.Status}; code={month.ErrorCode}; events={month.Events.Count}; today={service.Current.Events.Count}; updated={month.LastSuccess:O}");
        Check(month.Status == CalendarStatus.Connected && month.LastSuccess is not null, "Live refresh did not finish.");
        using var offline = new CalendarService();
        offline.Configure(true, "codex:nonexistent", []);
        var restored = await offline.GetMonthAsync(CalendarDates.Today, token: deadline.Token);
        Check(restored.Status == CalendarStatus.Connected && restored.Events.Count == month.Events.Count && restored.LastSuccess == month.LastSuccess, "Encrypted cache failed offline reload.");
        Console.WriteLine("DPAPI cache and offline reload verified; no model turn requested.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
