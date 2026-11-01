using System.IO;
using System.Text.Json;
using StageManager.Settings;

internal static class CalendarSettingsTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StageManager-CalendarSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var clientId = Guid.NewGuid().ToString("D");
            var original = JsonSerializer.Serialize(new AppSettings
            {
                SchemaVersion = 21, StageMode = StageMode.Focus, ShowAiUsageCard = true, AiUsageEnabled = true,
                ShowCalendarCard = true, OutlookCalendarClientId = clientId, OutlookCalendarIds = new() { "saved" }
            });
            File.WriteAllText(path, original);
            var service = new SettingsService(path);
            Check(service.Current.SchemaVersion == 23 && !service.Current.ShowCalendarCard, "Old settings must migrate with calendar disabled.");
            Check(service.Current.StageMode == StageMode.Focus && service.Current.AiUsageEnabled && service.Current.ShowAiUsageCard, "Calendar migration changed unrelated choices.");
            Check(File.ReadAllText(path + ".pre-schema23.bak") == original, "Migration backup did not preserve original bytes.");
            var draft = service.CloneCurrent();
            draft.ShowCalendarCard = true;
            draft.LastSettingsPage = "calendar";
            draft.OutlookCalendarIds = new() { "saved", "saved", " second ", "" };
            draft.OutlookCalendarClientId = " " + clientId.ToUpperInvariant() + " ";
            service.Apply(draft);
            var reloaded = new SettingsService(path).Current;
            Check(reloaded.ShowCalendarCard && reloaded.LastSettingsPage == "calendar" && reloaded.OutlookCalendarClientId == clientId, "Schema 22 calendar settings did not round trip.");
            Check(reloaded.OutlookCalendarIds.SequenceEqual(new[] { "saved", "second" }), "Calendar IDs not normalized.");
            Check(File.ReadAllText(path + ".pre-schema23.bak") == original, "Original migration backup overwritten.");
            draft = service.CloneCurrent();
            draft.OutlookCalendarIds.Add("clone-only");
            Check(!service.Current.OutlookCalendarIds.Contains("clone-only"), "Draft calendar IDs alias live settings.");
            draft.OutlookCalendarClientId = "not-a-client-id";
            draft.OutlookCalendarIds = null!;
            service.Apply(draft);
            Check(service.Current.OutlookCalendarClientId == "" && service.Current.OutlookCalendarIds.Count == 0, "Malformed calendar configuration not normalized.");
            Check(!new AppSettings().ShowCalendarCard, "Calendar default must be off.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
