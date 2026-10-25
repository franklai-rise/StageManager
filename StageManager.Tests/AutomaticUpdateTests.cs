using System.IO;
using System.Text.Json;
using StageManager.Card3DPrototype.Updates;
using StageManager.Settings;

internal static class AutomaticUpdateTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StageManager-auto-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero); var requests = 0; var notices = 0;
            UpdateRelease Release(int patch = 1) => new(new Version(4, 7, patch), $"v4.7.{patch}", "notes", now, new Uri(AppVersion.ReleasesUrl + $"/tag/v4.7.{patch}"), Array.Empty<ReleaseAsset>());
            Task<UpdateRelease> Query(CancellationToken token) { requests++; return Task.FromResult(Release()); }
            var path = Path.Combine(directory, "state.json");
            using (var checker = new AutomaticUpdateChecker(path, () => now, Query, new Version(4, 7, 0)))
            {
                await checker.PollAsync(); Check(requests == 0, "Disabled check uses network.");
                checker.SetEnabled(true); now = now.AddSeconds(44); await checker.PollAsync(); Check(requests == 0, "Startup check ran early.");
                checker.SetEnabled(true); now = now.AddSeconds(1); await checker.PollAsync(); Check(requests == 1, "Unrelated setting reset startup countdown.");
                checker.TryNotify(false, _ => notices++); Check(notices == 0, "Fullscreen received update notification.");
                checker.TryNotify(true, _ => notices++); checker.TryNotify(true, _ => notices++); Check(notices == 1, "Notification duplication.");
                now = now.AddHours(23); await checker.PollAsync(); Check(requests == 1, "Repeated automatic request within one day.");
                now = now.AddHours(1); await checker.PollAsync(); checker.TryNotify(true, _ => notices++); Check(requests == 2 && notices == 1, "Daily cadence or notified-version dedup failed.");
            }
            using (var restarted = new AutomaticUpdateChecker(path, () => now, Query, new Version(4, 7, 0)))
            {
                restarted.SetEnabled(true); now = now.AddSeconds(45); await restarted.PollAsync(); Check(requests == 2, "Restart lost last check timestamp.");
                now = now.AddDays(1); await restarted.PollAsync(); restarted.TryNotify(true, _ => notices++); Check(notices == 1, "Restart lost notified version.");
            }
            var response = new TaskCompletionSource<UpdateRelease>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken activeToken = default;
            using (var race = new AutomaticUpdateChecker(Path.Combine(directory, "race.json"), () => now, token => { requests++; activeToken = token; return response.Task; }, new Version(4, 7, 0)))
            {
                race.SetEnabled(true); now = now.AddSeconds(45); var pending = race.PollAsync(); var before = requests; _ = race.PollAsync(); Check(requests == before, "Overlapping requests.");
                race.SetEnabled(false); Check(activeToken.IsCancellationRequested, "Disabling does not cancel network request.");
                race.SetEnabled(true); response.SetResult(Release(2)); await pending; race.TryNotify(true, _ => notices++); Check(notices == 1, "Stale completion survived toggle race.");
            }
            using (var failure = new AutomaticUpdateChecker(Path.Combine(directory, "offline.json"), () => now, _ => throw new IOException("offline")))
            { failure.SetEnabled(true); now = now.AddSeconds(45); await failure.PollAsync(); failure.TryNotify(true, _ => notices++); Check(notices == 1, "Failed check reported a release."); }
            var settingsPath = Path.Combine(directory, "settings.json");
            File.WriteAllText(settingsPath, "{\"SchemaVersion\":20,\"StageMode\":\"Focus\",\"SidebarEdge\":\"Top\",\"ShowAiUsageCard\":true,\"AiUsageEnabled\":true}");
            var settings = new SettingsService(settingsPath);
            Check(settings.Current.SchemaVersion == 21 && settings.Current.StageMode == StageMode.Focus && settings.Current.SidebarEdge == SidebarEdge.Top && settings.Current.AiUsageEnabled && settings.Current.AutomaticUpdateChecks, "Migration lost existing choices.");
            Check(File.Exists(settingsPath + ".pre-schema21.bak"), "No settings migration backup.");
            var draft = settings.CloneCurrent(); draft.AutomaticUpdateChecks = false; draft.ShowWhatsNewOnStartup = false; draft.SettingsTheme = SettingsTheme.Dark; draft.SettingsTextSize = 12; draft.LastSettingsPage = "preferences"; settings.Apply(draft);
            var loaded = new SettingsService(settingsPath).Current;
            Check(!loaded.AutomaticUpdateChecks && !loaded.ShowWhatsNewOnStartup && loaded.SettingsTextSize == 12 && loaded.SettingsTheme == SettingsTheme.Dark && loaded.LastSettingsPage == "preferences", "Preferences do not survive restart.");
        }
        finally { Directory.Delete(directory, true); } // Test-owned GUID directory only.
    }
}
