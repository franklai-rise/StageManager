using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Drawing;
using System.Reflection;
using System.Collections.Concurrent;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using Windows.UI.Composition;
using StageManager.Card3DPrototype.QuickLaunch;
using StageManager.Settings;

internal static class QuickLaunchRegressionTests
{
	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	public static void MigrationPreservesFlagsAndOtherSettings()
	{
		foreach (var chrome in new[] { false, true })
		foreach (var edge in new[] { false, true })
		WithSettings(path =>
		{
			var original = new AppSettings
			{
				SchemaVersion = 16, ShowChromeQuickLaunch = chrome, ShowEdgeQuickLaunch = edge,
				UiLanguage = UiLanguage.SimplifiedChinese, StageMode = StageMode.Focus, CardScale = .85,
				SidebarVerticalOffset = 130, ShowExplorerButton = false, AnimationsEnabled = false,
				StartWithWindows = false, HotkeysEnabled = false, IdleAutoHideSeconds = 45,
				PreviewRefreshMinutes = 7, IgnoredProcesses = new() { "notepad", "word" }
			};
			File.WriteAllText(path, JsonSerializer.Serialize(original));
			var service = new SettingsService(path);
			var apps = service.Current.QuickLaunchApps!;
			Check(service.Current.SchemaVersion == 20, "Schema was not migrated to 20.");
			Check(apps.Count == 2 && apps[0].Id == "browser:chrome" && apps[1].Id == "browser:edge", "Legacy order changed.");
			Check(apps[0].Enabled == chrome && apps[1].Enabled == edge, "Legacy flags changed.");
			var before = JsonSerializer.SerializeToNode(original)!.AsObject();
			var after = JsonSerializer.SerializeToNode(service.Current)!.AsObject();
			foreach (var key in new[] { "SchemaVersion", "QuickLaunchApps" }) { before.Remove(key); after.Remove(key); }
			Check(JsonNode.DeepEquals(before, after), "Migration modified unrelated settings.");
			var reloaded = new SettingsService(path);
			Check(reloaded.Current.SchemaVersion == 20 && reloaded.Current.QuickLaunchApps!.SequenceEqual(apps), "Migration was not persisted.");
		});
	}

	public static void OrderedListsCloneAndEmptyRoundTrip()
	{
		WithSettings(path =>
		{
			var service = new SettingsService(path);
			var draft = service.CloneCurrent();
			var first = QuickLaunchCatalog.FromFile(@"C:\Apps\First.exe");
			var second = QuickLaunchCatalog.FromFile(@"C:\Menu\Custom profile.lnk");
			second.Enabled = false;
			draft.QuickLaunchApps = new() { second, first };
			service.Apply(draft);
			first.DisplayName = "mutated outside settings";
			draft.QuickLaunchApps.Clear();
			var reload = new SettingsService(path);
			Check(reload.Current.QuickLaunchApps![0] == second && !reload.Current.QuickLaunchApps[0].Enabled, "Order or disabled state changed.");
			Check(reload.Current.QuickLaunchApps[1].DisplayName == "First", "Applied list shares draft objects.");
			var clone = reload.CloneCurrent();
			clone.QuickLaunchApps![0].Target = "changed";
			Check(reload.Current.QuickLaunchApps[0].Target != "changed", "Clone shares launcher objects.");
			draft = reload.CloneCurrent();
			draft.QuickLaunchApps = new();
			reload.Apply(draft);
			Check(new SettingsService(path).Current.QuickLaunchApps!.Count == 0, "An empty list restored legacy defaults.");
		});
	}

	public static void StableIdentityAndInvalidRows()
	{
		var original = QuickLaunchCatalog.FromFile(@"C:\Apps\Example.exe");
		var renamed = QuickLaunchCatalog.Create("Another display name", original.Kind, original.Target.ToUpperInvariant());
		Check(original.Id == renamed.Id, "Identity depends on name or path casing.");
		Check(original.Id != QuickLaunchCatalog.FromFile(@"C:\Other\Example.exe").Id, "Different app paths collided.");
		WithSettings(path =>
		{
			File.WriteAllText(path, """
			{"SchemaVersion":17,"QuickLaunchApps":[null,{"Id":"","DisplayName":"","Target":"C:\\Apps\\Missing.exe","Kind":"Executable"},{"Id":"x","Target":"","Kind":"Executable"}]}
			""");
			var service = new SettingsService(path);
			Check(service.Current.QuickLaunchApps!.Count == 1, "Invalid rows were not isolated.");
			var app = service.Current.QuickLaunchApps[0];
			Check(app.Id.Length > 0 && app.DisplayName.Length > 0, "Missing identity/name not repaired.");
			var draft = service.CloneCurrent();
			draft.QuickLaunchApps!.Add(app with { });
			service.Apply(draft);
			Check(service.Current.QuickLaunchApps.Count == 1, "Duplicate ID survived normalization.");
			Check(new SettingsService(path).Current.QuickLaunchApps![0].Id == app.Id, "Repaired ID is unstable.");
		});
	}

	public static void ShellLaunchPreservesShortcutAndDoesNotElevate()
	{
		foreach (var path in new[] { @"C:\Apps\App.exe", @"C:\Menu\Profile with arguments.lnk" })
		{
			var app = QuickLaunchCatalog.FromFile(path);
			var info = QuickLaunchCatalog.CreateStartInfo(app);
			Check(info.FileName == path && info.UseShellExecute, "Shortcut was dereferenced or shell execution disabled.");
			Check(info.Arguments == "" && info.WorkingDirectory == "", "Shell shortcut arguments/working directory were overridden.");
			Check(info.Verb == "", "Launcher requested elevation or another verb.");
		}
		var packaged = QuickLaunchCatalog.Create("Packaged", QuickLaunchTargetKind.AppsFolder, "Vendor.App_123!Main");
		var packagedInfo = QuickLaunchCatalog.CreateStartInfo(packaged);
		Check(packagedInfo.FileName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
			packagedInfo.Arguments == "\"shell:AppsFolder\\Vendor.App_123!Main\"" && packagedInfo.Verb == "", "AUMID shell activation changed.");
		Check(!QuickLaunchCatalog.Launch(packaged with { Target = "bad\"target" }), "Invalid AUMID was not rejected.");
		Check(!QuickLaunchCatalog.Launch(new() { Target = "https://example.com", Kind = QuickLaunchTargetKind.Executable }), "Non-EXE launch accepted.");
	}

	public static void IconFailureAndCancellation()
	{
		using var icon = QuickLaunchIcons.LoadAsync(QuickLaunchCatalog.FromFile(@"C:\Missing-StageManager-Test\Absent.exe"), CancellationToken.None).GetAwaiter().GetResult();
		Check(icon.Width > 0 && icon.Height > 0, "Missing icon has no fallback.");
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		try { QuickLaunchCatalog.DiscoverAsync(cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Discovery ignored cancellation."); }
		catch (OperationCanceledException) { }
	}

	public static void PickerDoesNotMutateInput()
	{
		var apps = QuickLaunchDefinition.LegacyDefaults(false, true);
		using var picker = new QuickLaunchSettingsForm(apps, UiLanguage.English);
		var output = picker.GetSelection();
		output[0].Enabled = true;
		output.Clear();
		Check(!apps[0].Enabled && picker.GetSelection().Count == 2 && !picker.GetSelection()[0].Enabled, "Picker exposes shared edit state.");
		Check(!picker.IsHandleCreated, "Constructing settings eagerly created its HWND.");
	}

	public static void InFlightAndQueuedCancellation()
	{
		using var started = new CountdownEvent(2);
		using var release = new ManualResetEventSlim();
		using var cancellation = new CancellationTokenSource();
		var workers = Enumerable.Range(0, 2).Select(_ => QuickLaunchCatalog.RunStaAsync(() =>
		{
			Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Shell work is not on STA.");
			started.Signal();
			Check(release.Wait(TimeSpan.FromSeconds(5)), "Test worker was not released.");
			return 42;
		}, cancellation.Token)).ToArray();
		try
		{
			Check(started.Wait(TimeSpan.FromSeconds(5)), "Background workers failed to start.");
			var queued = QuickLaunchCatalog.DiscoverAsync(cancellation.Token);
			cancellation.Cancel();
			try { queued.GetAwaiter().GetResult(); throw new InvalidOperationException("Queued discovery ignored cancellation."); }
			catch (OperationCanceledException) { }
		}
		finally { release.Set(); }
		foreach (var worker in workers)
		{
			try { worker.GetAwaiter().GetResult(); throw new InvalidOperationException("In-flight shell result published after cancellation."); }
			catch (OperationCanceledException) { }
		}
	}

	public static void PendingIconsAreDisposedOnClose()
	{
		var app = QuickLaunchCatalog.FromFile(@"C:\Apps\Test.exe");
		var context = new QueuedContext();
		var accepted = 0;
		var icon = new Bitmap(8, 8);
		using (var request = new QuickLaunchIconRequest(app, context, value => { accepted++; value.Dispose(); },
			(_, _) => Task.FromResult(icon)))
		{
			Check(context.Callbacks.Count == 1, "Icon delivery did not queue on the UI context.");
		}
		context.Drain();
		Check(accepted == 0, "Disposed icon request invoked its UI consumer.");
		CheckDisposed(icon);
		var late = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
		var lateIcon = new Bitmap(8, 8);
		var lateRequest = new QuickLaunchIconRequest(app, context, value => { accepted++; value.Dispose(); }, (_, _) => late.Task);
		lateRequest.Dispose();
		late.SetResult(lateIcon);
		Check(lateRequest.Completion.Wait(TimeSpan.FromSeconds(5)), "Late result cleanup did not complete.");
		// GDI+ properties must not be probed while another thread is disposing the
		// image. Observe completion first, then assert the actual disposal outcome.
		CheckDisposed(lateIcon);
		context.Drain();
		Check(accepted == 0, "Late result updated a closed UI.");
	}

	public static void TransientIconFailuresRetryAndDeliverOnlyRealIcon()
	{
		var app = QuickLaunchCatalog.FromFile(@"C:\Apps\Retry.exe");
		var context = new QueuedContext();
		var calls = 0;
		Bitmap? delivered = null;
		using var request = new QuickLaunchIconRequest(app, context, icon => delivered = icon,
			(_, _) => ++calls < 3 ? Task.FromException<Bitmap>(new InvalidOperationException("Transient test failure")) :
				Task.FromResult(new Bitmap(9, 7)), TimeSpan.FromMilliseconds(1));
		Check(request.Completion.Wait(TimeSpan.FromSeconds(5)), "Icon retries did not complete.");
		Check(calls == 3 && context.Callbacks.Count == 1, "Retries delivered a placeholder or duplicated the real icon.");
		context.Drain();
		Check(delivered is { Width: 9, Height: 7 }, "Recovered icon did not reach the UI.");
		Check(!request.CanRetry, "A successfully loaded icon requested another native read.");
		delivered!.Dispose();

		using var cancelled = new QuickLaunchIconRequest(app, context, icon => icon.Dispose(),
			(_, _) => { calls++; return Task.FromException<Bitmap>(new IOException("Unavailable")); }, TimeSpan.FromSeconds(1));
		cancelled.Dispose();
		Check(cancelled.Completion.Wait(TimeSpan.FromSeconds(2)), "Closing the card did not cancel its retry delay.");
		Check(calls == 4 && context.Callbacks.IsEmpty, "Closed card retried or published a stale icon.");
	}

	public static void LateIconCleanupIsDeterministic()
	{
		var app = QuickLaunchCatalog.FromFile(@"C:\Apps\Close.exe");
		for (var iteration = 0; iteration < 100; iteration++)
		{
			var completion = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
			var context = new QueuedContext();
			var accepted = false;
			var icon = new Bitmap(8, 8);
			var request = new QuickLaunchIconRequest(app, context, value => { accepted = true; value.Dispose(); }, (_, _) => completion.Task);
			request.Dispose();
			completion.SetResult(icon);
			Check(request.Completion.Wait(TimeSpan.FromSeconds(2)), "Disposed request retained its native result.");
			CheckDisposed(icon);
			context.Drain();
			Check(!accepted, "Late icon updated a disposed card.");
		}
	}

	public static void NativeIconLoadsOwnIndependentBitmaps()
	{
		// An SDK-generated test apphost need not embed any icon resources. Use the
		// Windows executable that exists on both local and hosted Windows runners.
		var target = QuickLaunchCatalog.FromFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
		for (var iteration = 0; iteration < 8; iteration++)
		{
			var tasks = Enumerable.Range(0, 4)
				.Select(_ => QuickLaunchIcons.LoadRequiredAsync(target, CancellationToken.None)).ToArray();
			var icons = Task.WhenAll(tasks).GetAwaiter().GetResult();
			try
			{
				Check(icons.All(icon => icon.Width > 0 && icon.Height > 0), "Native extraction returned an empty image.");
				Check(!ReferenceEquals(icons[0], icons[1]), "Native results share a mutable bitmap.");
				var previous = icons[1].GetPixel(0, 0);
				icons[0].SetPixel(0, 0, previous.ToArgb() == Color.Magenta.ToArgb() ? Color.Lime : Color.Magenta);
				icons[0].Dispose();
				Check(icons[1].GetPixel(0, 0) == previous, "Disposing or editing one icon corrupted another.");
			}
			finally { foreach (var icon in icons) icon.Dispose(); }
		}
	}

	public static void VerifyInstalledBrowserIcons(string outputDirectory)
	{
		Directory.CreateDirectory(outputDirectory);
		foreach (var browser in new[] { QuickLaunchApp.Chrome, QuickLaunchApp.Edge })
		{
			var executable = QuickLaunchAppResolver.ResolveExecutable(browser);
			Check(executable is not null, $"{browser} is not installed for this local probe.");
			var definition = QuickLaunchAppResolver.Definition(browser);
			for (var iteration = 0; iteration < 25; iteration++)
			{
				using var actual = QuickLaunchIcons.LoadRequiredAsync(definition, CancellationToken.None).GetAwaiter().GetResult();
				using var expectedIcon = Icon.ExtractAssociatedIcon(executable!);
				using var expected = expectedIcon!.ToBitmap();
				Check(actual.Size == expected.Size, $"{browser} returned a fallback-sized icon.");
				for (var x = 0; x < actual.Width; x++)
				for (var y = 0; y < actual.Height; y++)
					Check(actual.GetPixel(x, y) == expected.GetPixel(x, y), $"{browser} icon differs from its executable resource.");
				if (iteration == 24) actual.Save(Path.Combine(outputDirectory, browser + ".png"), System.Drawing.Imaging.ImageFormat.Png);
			}
			Console.WriteLine($"{browser}: 25 real native icon loads match the executable resource.");
		}
	}

	private static bool IsDisposed(Bitmap bitmap)
	{
		try { _ = bitmap.Width; return false; } catch (ArgumentException) { return true; }
	}

	public static void StalledLaunchIsBoundedAndBusyClicksNeverReplay()
	{
		using var entered = new CountdownEvent(2);
		using var release = new ManualResetEventSlim();
		var calls = 0;
		var nonStaCalls = 0;
		var callerThread = Environment.CurrentManagedThreadId;
		var callerThreadCalls = 0;
		var launcher = new QuickLaunchLauncher(dispatch: _ =>
		{
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) Interlocked.Increment(ref nonStaCalls);
			if (Environment.CurrentManagedThreadId == callerThread) Interlocked.Increment(ref callerThreadCalls);
			if (Interlocked.Increment(ref calls) <= 2) entered.Signal();
			release.Wait(TimeSpan.FromSeconds(10));
		}, timeout: TimeSpan.FromMilliseconds(250));
		var app = QuickLaunchCatalog.FromFile(@"C:\Apps\Fake.exe");
		try
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var first = launcher.LaunchAsync(app);
			var second = launcher.LaunchAsync(app);
			Check(entered.Wait(TimeSpan.FromSeconds(2)), "Launch workers did not enter the fake Shell.");
			Check(!first.IsCompleted && !second.IsCompleted, "Launch call blocked the caller until Shell returned.");
			var results = Task.WhenAll(first, second).GetAwaiter().GetResult();
			Check(results.All(result => result.Status == QuickLaunchStatus.TimedOut && result.ShellDispatched), "Stalled Shell must report timeout with an uncertain dispatched launch.");
			Check(clock.Elapsed < TimeSpan.FromSeconds(2), "Launch timeout did not bound caller latency.");
			Check(launcher.AvailableSlots == 0, "Timeout released a slot while its native worker was still running.");
			for (var index = 0; index < 100; index++)
				Check(launcher.LaunchAsync(app).GetAwaiter().GetResult().Status == QuickLaunchStatus.Busy, "Repeated clicks spawned/queued workers behind stalled launches.");
			Check(calls == 2 && nonStaCalls == 0 && callerThreadCalls == 0, "Shell launch was not isolated to bounded background STAs.");
		}
		finally { release.Set(); }
		Check(SpinWait.SpinUntil(() => launcher.AvailableSlots == 2, TimeSpan.FromSeconds(2)), "Exited launch workers did not release slots.");
		Check(calls == 2, "Rejected clicks launched later when a slot became available.");
		Check(launcher.LaunchAsync(app).GetAwaiter().GetResult().Status == QuickLaunchStatus.Succeeded && calls == 3, "Launch capacity did not recover after native work finished.");
	}

	public static void TimedOutOrCancelledPreparationNeverDispatches()
	{
		foreach (var cancel in new[] { false, true })
		{
			using var entered = new ManualResetEventSlim();
			using var release = new ManualResetEventSlim();
			using var cancellation = new CancellationTokenSource();
			var calls = 0;
			var launcher = new QuickLaunchLauncher(prepare: app =>
			{
				entered.Set();
				release.Wait(TimeSpan.FromSeconds(10));
				return QuickLaunchCatalog.CreateStartInfo(app);
			}, dispatch: _ => Interlocked.Increment(ref calls), timeout: TimeSpan.FromMilliseconds(250));
			try
			{
				var pending = launcher.LaunchAsync(QuickLaunchCatalog.FromFile(@"C:\Apps\Fake.exe"), cancellation.Token);
				Check(entered.Wait(TimeSpan.FromSeconds(2)), "Fake resolver never started.");
				if (cancel) cancellation.Cancel();
				var result = pending.GetAwaiter().GetResult();
				Check(result.Status == (cancel ? QuickLaunchStatus.Cancelled : QuickLaunchStatus.TimedOut) && !result.ShellDispatched,
					"Preparation timeout/cancellation should not claim dispatch.");
				Check(launcher.AvailableSlots == 1, "Hung preparation freed its reserved worker.");
			}
			finally { release.Set(); }
			Check(SpinWait.SpinUntil(() => launcher.AvailableSlots == 2, TimeSpan.FromSeconds(2)), "Fake resolver did not exit.");
			Check(calls == 0, "Shell was called after a cancelled/timed-out resolver returned.");
		}
	}

	public static void LaunchCancellationAndFailuresAreExplicit()
	{
		var calls = 0;
		var app = QuickLaunchCatalog.FromFile(@"C:\Apps\Fake.exe");
		var launcher = new QuickLaunchLauncher(dispatch: _ => { Interlocked.Increment(ref calls); throw new IOException("Fake Shell failure"); });
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var cancelled = launcher.LaunchAsync(app, cancellation.Token).GetAwaiter().GetResult();
		Check(cancelled.Status == QuickLaunchStatus.Cancelled && !cancelled.ShellDispatched && calls == 0, "Pre-cancelled launch reached Shell.");
		var invalid = launcher.LaunchAsync(app with { Target = "invalid" }).GetAwaiter().GetResult();
		Check(invalid.Status == QuickLaunchStatus.Failed && !invalid.ShellDispatched && calls == 0, "Invalid target reached Shell.");
		var failed = launcher.LaunchAsync(app).GetAwaiter().GetResult();
		Check(failed.Status == QuickLaunchStatus.Failed && failed.ShellDispatched && calls == 1, "Shell fault was not contained.");
		try { _ = new QuickLaunchLauncher(timeout: TimeSpan.FromSeconds(6)); throw new InvalidOperationException("Launch timeout exceeded five seconds."); }
		catch (ArgumentOutOfRangeException) { }
	}

	public static void StalledIconsDoNotStarveDiscoveryOrLaunch()
	{
		using var entered = new CountdownEvent(2);
		using var release = new ManualResetEventSlim();
		using var cancellation = new CancellationTokenSource();
		var icons = Enumerable.Range(0, 2).Select(_ => QuickLaunchCatalog.RunIconStaAsync(() =>
		{
			entered.Signal();
			release.Wait(TimeSpan.FromSeconds(10));
			return 1;
		}, cancellation.Token)).ToArray();
		try
		{
			Check(entered.Wait(TimeSpan.FromSeconds(2)), "Icon slots were not occupied.");
			var discovery = QuickLaunchCatalog.RunStaAsync(() => 42, CancellationToken.None);
			Check(discovery.Wait(TimeSpan.FromSeconds(2)) && discovery.Result == 42, "Stalled icons starved discovery.");
			var launcher = new QuickLaunchLauncher(dispatch: _ => { });
			var launch = launcher.LaunchAsync(QuickLaunchCatalog.FromFile(@"C:\Apps\Fake.exe"));
			Check(launch.Wait(TimeSpan.FromSeconds(2)) && launch.Result.Status == QuickLaunchStatus.Succeeded, "Stalled icons starved launch.");
			cancellation.Cancel();
			foreach (var icon in icons)
			{
				try { icon.GetAwaiter().GetResult(); throw new InvalidOperationException("Running icon cancellation was not delivered promptly."); }
				catch (OperationCanceledException) { }
			}
			using var queuedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
			var replacementRan = false;
			try
			{
				QuickLaunchCatalog.RunIconStaAsync(() => { replacementRan = true; return 2; }, queuedCancellation.Token).GetAwaiter().GetResult();
				throw new InvalidOperationException("Cancellation freed still-running icon slots.");
			}
			catch (OperationCanceledException) { }
			Check(!replacementRan, "A cancelled queued icon spawned another native worker.");
		}
		finally { release.Set(); }
		Check(QuickLaunchCatalog.RunIconStaAsync(() => 3, CancellationToken.None).Wait(TimeSpan.FromSeconds(2)), "Icon pool failed to recover after real worker exit.");
	}
	private static void CheckDisposed(Bitmap bitmap) => Check(IsDisposed(bitmap), "Queued bitmap was not disposed.");
	private sealed class QueuedContext : SynchronizationContext
	{
		public ConcurrentQueue<(SendOrPostCallback Callback, object? State)> Callbacks { get; } = new();
		public override void Post(SendOrPostCallback callback, object? state) => Callbacks.Enqueue((callback, state));
		public void Drain() { while (Callbacks.TryDequeue(out var next)) next.Callback(next.State); }
	}

	public static void RendererWorksWithoutOwnerHwnd()
	{
		using var dispatcher = new DispatcherQueueHelper();
		dispatcher.EnsureDispatcherQueue();
		using var compositor = new Compositor();
		using var root = compositor.CreateContainerVisual();
		using var owner = new Form();
		using var renderer = new CompositionStageRenderer(owner, compositor, root, .8, false, true);
		var first = QuickLaunchCatalog.FromFile(@"C:\Missing-StageManager-Test\First.exe");
		var second = QuickLaunchCatalog.FromFile(@"C:\Missing-StageManager-Test\Second.lnk");
		var hidden = first with { Id = "disabled", Enabled = false };
		renderer.Resize(700, 1400, 1);
		renderer.SetQuickLaunchApps(new[] { second, hidden, first });
		var hits = (List<CardHitTarget>)typeof(CompositionStageRenderer).GetField("_hitTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
		var launchers = hits.Where(hit => hit.QuickLaunchApp is not null).ToArray();
		Check(launchers.Length == 2 && launchers[0].QuickLaunchApp!.Id == second.Id && launchers[1].QuickLaunchApp!.Id == first.Id, "Renderer lost configured order or disabled filtering.");
		var explorer = hits.Single(hit => hit.IsExplorerButton);
		Check(launchers[0].Polygon.Min(point => point.Y) > explorer.Polygon.Max(point => point.Y), "Launcher is not below Explorer.");
		renderer.Activate(launchers[0]);
		Check(renderer.ConsumeQuickLaunchRequest()?.Id == second.Id && renderer.ConsumeQuickLaunchRequest() is null, "Launch request identity/consume semantics failed.");
		Check(!renderer.SetQuickLaunchApps(new[] { second, hidden, first }), "Unchanged launcher settings rebuilt cards.");
		renderer.SetQuickLaunchApps(Array.Empty<QuickLaunchDefinition>());
		Check(!hits.Any(hit => hit.QuickLaunchApp is not null), "Removed launchers retained hit targets.");
		Check(!owner.IsHandleCreated, "Launcher rendering or icon work created the owner HWND.");
	}

	private static void WithSettings(Action<string> test)
	{
		var directory = Path.Combine(Path.GetTempPath(), "StageManager-QuickLaunchTests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try { test(Path.Combine(directory, "settings.json")); }
		finally { Directory.Delete(directory, true); }
	}

#if QUICK_LAUNCH_STANDALONE
	[STAThread]
	public static int Main(string[] args)
	{
		foreach (var test in new Action[] { MigrationPreservesFlagsAndOtherSettings, OrderedListsCloneAndEmptyRoundTrip,
			StableIdentityAndInvalidRows, ShellLaunchPreservesShortcutAndDoesNotElevate, IconFailureAndCancellation,
			InFlightAndQueuedCancellation, PendingIconsAreDisposedOnClose, PickerDoesNotMutateInput, RendererWorksWithoutOwnerHwnd,
			StalledLaunchIsBoundedAndBusyClicksNeverReplay, TimedOutOrCancelledPreparationNeverDispatches,
			LaunchCancellationAndFailuresAreExplicit, StalledIconsDoNotStarveDiscoveryOrLaunch })
		{
			try { test(); Console.WriteLine("PASS " + test.Method.Name); }
			catch (Exception ex) { Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + ex); return 1; }
		}
		if (args.Contains("--discovery"))
		{
			using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			var apps = QuickLaunchCatalog.DiscoverAsync(cancellation.Token).GetAwaiter().GetResult();
			Console.WriteLine($"Discovery: {apps.Count} apps; {apps.Count(app => app.Kind == QuickLaunchTargetKind.Shortcut)} shortcuts; {apps.Count(app => app.Kind == QuickLaunchTargetKind.AppsFolder)} AUMIDs.");
		}
		return 0;
	}
#endif
}
