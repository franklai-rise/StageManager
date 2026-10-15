using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using StageManager.Card3DPrototype.Updates;
using StageManager.Settings;

internal static class UpdateRegressionTests
{
    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    public static void RunAll() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "StageManager-update-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            var settings = Path.Combine(temp, "settings.json");
            var seen = Path.Combine(temp, "seen.json");
            Check(WhatsNewState.ShouldShow(seen, "4.6.0"), "First launch skipped introduction.");
            WhatsNewState.MarkShown(seen, "4.6.0");
            Check(!WhatsNewState.ShouldShow(seen, "4.6.0") && WhatsNewState.ShouldShow(seen, "4.6.1"), "Introduction version tracking failed.");
            Check(!new SettingsService(settings).Current.ShowAiUsageCard, "New install enabled AI.");
            File.WriteAllText(settings, JsonSerializer.Serialize(new AppSettings { SchemaVersion = 17, ShowAiUsageCard = true, AiUsageEnabled = true }));
            Check(!new SettingsService(settings).Current.AiUsageEnabled, "Old-version migration enabled AI.");
            Check(File.Exists(settings + ".pre-schema19.bak"), "Missing migration backup.");
            File.WriteAllText(settings, JsonSerializer.Serialize(new AppSettings { SchemaVersion = 18, ShowAiUsageCard = true, AiUsageEnabled = true, AiUsageKimiEnabled = true }));
            var trial = new SettingsService(settings).Current;
            Check(trial.ShowAiUsageCard && trial.AiUsageEnabled && trial.AiUsageKimiEnabled && trial.SchemaVersion == 19, "Trial choices were lost.");
            byte[] Release(bool draft = false, bool prerelease = false, string tag = "v4.7.0") => JsonSerializer.SerializeToUtf8Bytes(new
            {
                draft, prerelease, tag_name = tag, html_url = AppVersion.ReleasesUrl + "/tag/" + tag,
                body = "fixture notes", published_at = DateTimeOffset.UtcNow, assets = Array.Empty<object>()
            });
            Check(ReleaseUpdateService.Parse(Release()).Version > AppVersion.Current, "Version comparison wrong.");
            Check(ReleaseUpdateService.Parse(Release(tag: "v4.6.0")).Version == AppVersion.Current, "Same version differs.");
            Check(ReleaseUpdateService.Parse(Release(tag: "v4.4.16")).Version < AppVersion.Current, "Older release marked newer.");
            foreach (var bytes in new[] { Release(draft: true), Release(prerelease: true), Release(tag: "v4.7.0-beta"), Encoding.UTF8.GetBytes("{}") })
            { var rejected = false; try { ReleaseUpdateService.Parse(bytes); } catch { rejected = true; } Check(rejected, "Invalid release accepted."); }
            foreach (var uri in new[] { "http://github.com/x", "https://github.com.evil.test/x", "https://evil@github.com/x", "https://github.com:444/x" })
                Check(!ReleaseUpdateService.AllowedHost(new Uri(uri)), "Unsafe host allowed.");
            Check(ReleaseUpdateService.AllowedHost(new Uri("https://release-assets.githubusercontent.com/file")), "Official CDN rejected.");
            var hash = new string('a', 64); var name = "Stage_Manager_Lai_v4.7.0.exe";
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { tag = "v4.7.0", file = name, sha256 = hash });
            Check(ReleaseUpdateService.VerifyManifest("v4.7.0", name, Encoding.UTF8.GetBytes(hash + "  " + name), manifest) == hash, "Valid checksum rejected.");
            var bad = false; try { ReleaseUpdateService.VerifyManifest("v4.7.0", name, Encoding.UTF8.GetBytes(new string('b', 64) + "  " + name), manifest); } catch (InvalidDataException) { bad = true; }
            Check(bad, "Mismatch accepted.");
            using (var service = new ReleaseUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Release()) }))))
                Check((await service.CheckAsync(default)).Tag == "v4.7.0", "Manual check failed.");
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.NotFound })
            {
                using var service = new ReleaseUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
                var failed = false; try { await service.CheckAsync(default); } catch (HttpRequestException) { failed = true; }
                Check(failed, "Network failure looks up-to-date.");
            }
            using (var service = new ReleaseUpdateService(new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); })))
            {
                using var cancel = new CancellationTokenSource(50); var cancelled = false;
                try { await service.CheckAsync(cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancellation ignored.");
            }
            using (var service = new ReleaseUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.test/exe") } }))))
            { var rejected = false; try { await service.CheckAsync(default); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Unsafe redirect followed."); }
            var current = Path.Combine(temp, "current.exe"); var candidate = Path.Combine(temp, "candidate.exe"); var backup = Path.Combine(temp, "backup.exe");
            File.WriteAllText(current, "old"); File.WriteAllText(candidate, "new");
            File.Replace(candidate, current, backup);
            Check(File.ReadAllText(current) == "new" && File.ReadAllText(backup) == "old", "Atomic replacement lost backup.");
            File.Copy(backup, candidate); File.Move(candidate, current, true);
            Check(File.ReadAllText(current) == "old", "Rollback failed.");
        }
        finally { Directory.Delete(temp, true); } // Test-owned GUID directory only.
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
