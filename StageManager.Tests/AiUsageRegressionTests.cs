using System.Text;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Settings;

internal static class AiUsageRegressionTests
{
	public static void RunAll() => RunAsync().GetAwaiter().GetResult();
	private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
	internal static AiUsageSnapshot CodexFixture(string reset = "1800000000", decimal used = 37) =>
		AiUsageParsers.Codex(JsonSerializer.SerializeToElement(new
		{
			rateLimitsByLimitId = new { codex = new { primary = new { usedPercent = used, windowDurationMins = 300, resetsAt = long.Parse(reset) },
				secondary = new { usedPercent = 78, windowDurationMins = 10080, resetsAt = 1800500000L } } }
		}), "codex:test", DateTimeOffset.UtcNow);

	private static async Task RunAsync()
	{
		var gemini = AiUsageService.Empty("gemini", "Gemini browser connection required");
		var stamp = DateTimeOffset.UtcNow;
		using var agFixture = JsonDocument.Parse("""{"response":{"groups":[{"buckets":[{"bucketId":"gemini-5h","remainingFraction":0,"resetTime":"2026-10-17T05:00:38Z"},{"bucketId":"gemini-weekly","remainingFraction":1},{"bucketId":"3p-5h","remaining":{"remainingFraction":0.5}},{"bucketId":"3p-weekly"},{"bucketId":"unknown","remainingFraction":1}]}]}}""");
		var ag = AntigravityUsageClient.Parse(agFixture.RootElement, stamp, "test");
		Check(ag.HasData && ag.Windows?.Count == 3 && ag.TrendValue == 0, "Antigravity quota parsing failed or fabricated missing quota.");
		Check(ag.Windows!.Single(w => w.LimitId == "3p-5h").RemainingPercent == 50, "Nested Antigravity fraction failed.");
		using var agEmpty = JsonDocument.Parse("""{"response":{"groups":[{"buckets":[{"bucketId":"gemini-5h"},{"bucketId":"3p-5h","remainingFraction":1.1}]}]}}""");
		Check(!AntigravityUsageClient.Parse(agEmpty.RootElement, stamp, "test").HasData, "Unavailable Antigravity quotas look full.");
		Check(AiUsagePresentation.Height(new[] { ag }) == 180 && AiUsagePresentation.Height(Array.Empty<AiUsageSnapshot>()) == 120, "Antigravity row toggle failed.");
		var record = new GeminiQuotaRecord("Connected", stamp, new string('a', 32), new[] { new GeminiQuotaMeter(1, 0, stamp.AddHours(1)), new GeminiQuotaMeter(2, 100, stamp.AddDays(1)) });
		Check(GeminiQuotaRecord.IsValid(record, stamp), "Valid Gemini quota rejected.");
		Check(!GeminiQuotaRecord.IsValid(record with { Meters = new[] { new GeminiQuotaMeter(1, -1, null) } }, stamp), "Negative Gemini usage accepted.");
		Check(!GeminiQuotaRecord.IsValid(record with { Status = "LoginRequired" }, stamp), "Unauthenticated quota accepted.");
		Check(!GeminiQuotaRecord.IsValid(record with { UpdatedUtc = stamp.AddHours(1) }, stamp), "Future Gemini data accepted.");
		Check(gemini.Name == "Gemini" && gemini.NeedsConnection && !gemini.HasData && gemini.TrendValue is null, "Gemini placeholder must not fabricate usage.");
		foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
		{
			var size = new System.Drawing.Size((int)(116 * dpi), (int)(AiUsagePresentation.CardHeight * dpi));
			var cells = Enumerable.Range(0, 4).Select(i => AiUsagePresentation.CellBounds(i, size)).ToArray();
			Check(cells[0].Bottom == cells[2].Top && cells[0].Right == cells[1].Left && cells[3].Bottom == size.Height, "Four-provider grid overlaps or exceeds card bounds.");
		}
		using (var icons = new AiProviderIcons())
		using (var bitmap = new System.Drawing.Bitmap(48, 48))
		using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
		{
			graphics.Clear(System.Drawing.Color.Transparent);
			icons.Draw(graphics, "codex", new System.Drawing.RectangleF(0, 0, 48, 48));
			var dark = 0; var light = 0;
			for (var y = 0; y < 48; y++) for (var x = 0; x < 48; x++)
			{
				var pixel = bitmap.GetPixel(x, y);
				if (pixel.A == 0) continue;
				Check(pixel.R == pixel.G && pixel.G == pixel.B, "Codex logo must be monochrome.");
				if (pixel.R < 20) dark++; if (pixel.R > 235) light++;
			}
			Check(dark > 100 && light > 100, "Codex logo lost its black background or white mark.");
		}
		var quota = CodexFixture();
		using (var preview = new System.Drawing.Bitmap(232, 240))
		using (var graphics = System.Drawing.Graphics.FromImage(preview))
		using (var icons = new AiProviderIcons())
		{
			graphics.Clear(System.Drawing.Color.FromArgb(22, 27, 36));
			AiUsagePresentation.PaintCard(graphics, preview.Size, 2f,
				new[] { quota, AiUsageService.Empty("deepseek", "Waiting for API key"), gemini, AiUsageService.Empty("kimi", "Waiting for API key") }, UiLanguage.SimplifiedChinese, icons);
			preview.Save(Path.Combine(AppContext.BaseDirectory, "ai-grid-preview.png"));
		}
		Check(quota.TrendValue == 63 && quota.Windows?.Count == 2 && quota.Windows[1].RemainingPercent == 22, "Quota parser lost windows or inverted usage.");
		foreach (var (minutes, label) in new[] { (15, "15m"), (90, "90m"), (1440, "1d"), (10080, "7d") })
			Check(AiUsagePresentation.Duration(minutes) == label, "Incorrect duration label.");
		using var nullable = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":null,"resetsAt":null},"secondary":{"usedPercent":101,"windowDurationMins":"bad","resetsAt":9999999999999}}}""");
		var parsed = AiUsageParsers.Codex(nullable.RootElement, "scope", DateTimeOffset.UtcNow);
		Check(parsed.Windows![0].DurationMinutes is null && parsed.Windows[0].RemainingPercent == 100 && parsed.Windows[1].RemainingPercent == 0 && parsed.Windows[1].ResetsAt is null, "Nullable or out-of-range fields failed.");
		using var balance = JsonDocument.Parse("""{"balance_infos":[{"currency":"USD","total_balance":"2.50"},{"currency":"CNY","total_balance":"0","granted_balance":"0","topped_up_balance":"0"}]}""");
		var deep = AiUsageParsers.DeepSeek(balance.RootElement, "test", DateTimeOffset.UtcNow);
		Check(deep.HasData && deep.TrendValue == 0 && deep.TrendUnit == "CNY" && deep.Details.Count == 4, "Zero/multi-currency balance incorrect.");
		using var kimi = JsonDocument.Parse("""{"code":0,"data":{"available_balance":0,"voucher_balance":0,"cash_balance":0}}""");
		Check(AiUsageParsers.Kimi(kimi.RootElement, true, "test", DateTimeOffset.UtcNow).TrendUnit == "USD", "Kimi international currency missing.");
		Check(AiUsageService.Empty("deepseek", "Waiting for API key") is { HasData: false, UpdatedUtc: null }, "Missing key looks like valid data.");
		var controller = new AiUsagePopupController(); var t = DateTime.UtcNow;
		Check(controller.Update(true, false, false, t) == AiPopupAction.None, "Popup opens on first contact.");
		Check(controller.Update(true, false, false, t.AddMilliseconds(299)) == AiPopupAction.None, "Hover delay shortened.");
		Check(controller.Update(true, false, false, t.AddMilliseconds(300)) == AiPopupAction.Open, "Hover did not open.");
		Check(controller.Update(false, false, true, t.AddMilliseconds(310)) == AiPopupAction.None, "Gap immediately closed popup.");
		Check(controller.Update(false, true, true, t.AddMilliseconds(400)) == AiPopupAction.KeepOpen, "Pointer could not enter popup.");
		controller.Dismiss();
		Check(controller.Update(true, false, false, t.AddSeconds(1)) == AiPopupAction.None, "Explicit close immediately reopened popup.");
		controller.Update(false, false, false, t.AddSeconds(2));
		controller.Update(true, false, false, t.AddSeconds(3));
		Check(controller.Update(true, false, false, t.AddSeconds(3.35)) == AiPopupAction.Open, "Popup cannot re-arm.");
		var temp = Path.Combine(AppContext.BaseDirectory, "ai-test-data", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temp);
		try
		{
			var geminiPath = Path.Combine(temp, "gemini-browser-quota.json");
			File.WriteAllText(geminiPath, JsonSerializer.Serialize(record));
			var browserQuota = GeminiBrowserUsage.Read(temp);
			Check(browserQuota.Windows?.Count == 2 && browserQuota.TrendValue == 100 && browserQuota.Windows[1].RemainingPercent == 0, "Gemini browser cache lost quota windows.");
			File.WriteAllText(geminiPath, JsonSerializer.Serialize(record with { UpdatedUtc = stamp.AddMinutes(-20) }));
			Check(GeminiBrowserUsage.Read(temp).Status == "Gemini browser stale", "Old Gemini cache marked current.");
			File.WriteAllText(geminiPath, JsonSerializer.Serialize(record with { Status = "LoginRequired", Meters = Array.Empty<GeminiQuotaMeter>() }));
			Check(!GeminiBrowserUsage.Read(temp).HasData, "Signed-out Gemini cache retained quota.");
			await ServiceTests(temp);
			var store = new AiUsageCredentialStore(Path.Combine(temp, "vault"));
			store.Save("synthetic-deepseek-key", "synthetic-kimi-key");
			Check(store.Get("deepseek") == "synthetic-deepseek-key", "DPAPI roundtrip failed.");
			var path = Path.Combine(temp, "vault", "ai-usage-credentials.dpapi");
			Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic"), "Credentials stored in clear text.");
			store.Save("", "synthetic-kimi-key");
			Check(store.Get("deepseek") is null && store.Get("kimi") == "synthetic-kimi-key", "Clear affected another provider.");
			File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
			var failed = false;
			try { store.Save("", ""); } catch (System.Security.Cryptography.CryptographicException) { failed = true; }
			Check(failed && File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Corrupt credential file was overwritten.");
			var text = """{"id":"server-request","method":"ignored"}""" + "\n" + """{"id":3,"result":{}}""" + "\n";
			using var input = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
			var lines = new CodexUsageClient.BoundedLineReader(input);
			Check((await lines.ReadAsync(default))!.Contains("server-request") && (await lines.ReadAsync(default))!.Contains("\"id\":3"), "Buffered protocol lines lost.");
			using var oversized = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1))));
			var rejected = false;
			try { await new CodexUsageClient.BoundedLineReader(oversized).ReadAsync(default); } catch (InvalidDataException) { rejected = true; }
			Check(rejected, "Unbounded protocol response accepted.");
		}
		finally { Directory.Delete(temp, true); } // Owned per-test directory, never application/user state.
		Console.WriteLine("PASS AI usage: parsers, popup timing, scheduling, cancellation, history, DPAPI, protocol bounds");
	}
	private static async Task ServiceTests(string root)
	{
		var now = DateTimeOffset.UtcNow;
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		var provider = new FakeProvider(async token => { Interlocked.Increment(ref calls); started.TrySetResult(); await release.Task.WaitAsync(token); return CodexFixture(); });
		using var service = new AiUsageService(new[] { provider }, Path.Combine(root, "service"), () => now);
		var config = new AppSettings { ShowAiUsageCard = true, AiUsageEnabled = true, AiUsageDeepSeekEnabled = false };
		var jobs = Enumerable.Range(0, 100).Select(_ => service.RefreshAsync(config, false)).ToArray();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Check(calls == 1 && jobs.All(j => ReferenceEquals(j, jobs[0])), "Concurrent refreshes did not coalesce.");
		release.SetResult(); await Task.WhenAll(jobs);
		Check(service.Current.Count == 1 && service.Current[0].HasData, "Data not published.");
		await service.RefreshAsync(config, false); Check(calls == 1, "Automatic freshness ignored.");
		now += TimeSpan.FromSeconds(11);
		await service.RefreshAsync(config, true); await service.RefreshAsync(config, true);
		Check(calls == 2, "Manual cooldown failed.");
		now += TimeSpan.FromMinutes(6);
		provider.Fetch = _ => Task.FromException<AiUsageSnapshot>(new HttpRequestException());
		await service.RefreshAsync(config, false);
		Check(service.Current[0].HasData && service.Current[0].Status != "Connected", "Network error erased last successful data.");
		var currentAt = service.Current[0].UpdatedUtc;
		Check(currentAt == service.Current[0].UpdatedUtc, "Stale timestamp overwritten.");
		provider.Fetch = _ => Task.FromResult(CodexFixture(reset: "1800000100", used: 35));
		now += TimeSpan.FromMinutes(6); await service.RefreshAsync(config, false);
		Check(service.GetHistory("codex").Points.Last().BreakBefore, "Small quota reset was joined.");
		for (var i = 0; i < 300; i++) { now += TimeSpan.FromMinutes(5); await service.RefreshAsync(config, false); }
		Check(service.GetHistory("codex").Points.Count <= 288, "History cap failed.");

		var events = 0; var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		provider.Fetch = async token => { blocked.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return CodexFixture(); };
		now += TimeSpan.FromMinutes(6); service.Updated += (_, _) => events++;
		var active = service.RefreshAsync(config, false); await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
		service.Dispose(); var atDispose = events;
		try { await active.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
		Check(events == atDispose, "Disposed service published data.");
		// Corrupt/null/over-capacity history must not prevent initial refresh.
		var historyRoot = Path.Combine(root, "corrupt"); Directory.CreateDirectory(historyRoot);
		File.WriteAllText(Path.Combine(historyRoot, "ai-usage-history.json"), "[null,{}]");
		using var corrupt = new AiUsageService(new[] { new FakeProvider(_ => Task.FromResult(CodexFixture())) }, historyRoot);
		await corrupt.RefreshAsync(config, false);
		Check(corrupt.Current[0].HasData, "Bad history broke provider query.");
	}
	private sealed class FakeProvider(Func<CancellationToken, Task<AiUsageSnapshot>> fetch) : IAiUsageProvider
	{
		public Func<CancellationToken, Task<AiUsageSnapshot>> Fetch = fetch;
		public string ProviderId => "codex";
		public bool IsEnabled(AppSettings settings) => settings.AiUsageCodexEnabled;
		public Task<AiUsageSnapshot> RefreshAsync(AppSettings settings, CancellationToken token) => Fetch(token);
	}
}

