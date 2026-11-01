using System.IO;
using StageManager.Card3DPrototype.Calendar;

internal static class CalendarCompletionTests
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "StageManager-Completion-" + Guid.NewGuid().ToString("N"), "completion.json");
        var now = DateTimeOffset.Now;
        var item = new CalendarEvent("secret-event-id", "secret-calendar-id", "PRIVATE TITLE", now, now.AddHours(1), false,
            DateOnly.FromDateTime(now.LocalDateTime), DateOnly.FromDateTime(now.LocalDateTime), "PRIVATE LOCATION", "PRIVATE ORGANIZER", "PRIVATE BODY", "https://outlook.live.com/calendar");
        try
        {
            var store = new CalendarCompletionStore(path);
            Check(!store.IsCompleted(item), "New event is completed by default.");
            Check(store.Set(item, true), "Completion save failed.");
            Check(new CalendarCompletionStore(path).IsCompleted(item), "Completion did not survive restart.");
            var text = File.ReadAllText(path);
            Check(!text.Contains("PRIVATE") && !text.Contains("secret-") && !text.Contains("outlook"), "Event contents leaked into local checklist.");
            Check(store.IsCompleted(item with { Subject = "Renamed" }), "Renaming lost local completion.");
            Check(!store.IsCompleted(item with { Start = now.AddDays(7) }), "Recurring occurrence reused completion.");
            Check(!store.IsCompleted(item with { CalendarId = "other-calendar" }), "Calendar identity was ignored.");
            Check(store.Set(item, false) && !new CalendarCompletionStore(path).IsCompleted(item), "Uncheck did not persist.");
            File.WriteAllText(path, "broken JSON");
            Check(new CalendarCompletionStore(path).Keys.Count == 0, "Malformed local data not handled.");
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
