using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using StageManager.Card3DPrototype.NotificationArea;
using StageManager.Card3DPrototype.QuickLaunch;
using StageManager.Native;
using StageManager.Services;
using StageManager.Settings;

internal static class MaintenanceRegressionTests
{
	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	public static void DesktopRegistryFailuresRecover()
	{
		foreach (var error in new Exception[] { new IOException(), new UnauthorizedAccessException(), new System.Security.SecurityException() })
		{
			var unavailable = true;
			var visible = false;
			var toggles = 0;
			var service = new DesktopIconVisibilityService(() => unavailable ? throw error : visible, () => { toggles++; return true; });
			Check(!service.Refresh(), "A failed read changed the last known state.");
			unavailable = false;
			Check(service.Refresh() && !service.IconsVisible, "State did not recover after a failed startup read.");
			unavailable = true;
			Check(!service.Refresh() && !service.IconsVisible, "A refresh failure overwrote an observed hidden state.");
			unavailable = false;
			visible = true;
			Check(service.Refresh() && service.IconsVisible && toggles == 0, "Refreshing altered the desktop or failed to recover.");
		}
	}

	public static void BrowserRegistryFailuresUseFallback()
	{
		foreach (var app in new[] { QuickLaunchApp.Chrome, QuickLaunchApp.Edge })
		foreach (var error in new Exception[] { new IOException(), new UnauthorizedAccessException(), new System.Security.SecurityException() })
		{
			var reads = 0;
			var fallback = QuickLaunchAppResolver.ResolveExecutable(app, _ => { reads++; throw error; }, _ => true);
			Check(reads == 2 && fallback?.EndsWith(QuickLaunchAppResolver.CommandName(app)) == true,
				"A registry failure prevented checking the normal installation paths.");
			reads = 0;
			var registryFallback = QuickLaunchAppResolver.ResolveExecutable(app,
				_ => ++reads == 1 ? throw error : "valid-browser.exe", path => path == "valid-browser.exe");
			Check(registryFallback == "valid-browser.exe" && reads == 2, "Registry search order changed after a failed candidate.");
		}
	}

	public static void AppliedSettingsOwnTheirSnapshot()
	{
		var directory = Directory.CreateTempSubdirectory("stage-settings-maintenance-").FullName;
		var path = Path.Combine(directory, "settings.json");
		try
		{
			var service = new SettingsService(path);
			var draft = service.CloneCurrent();
			draft.CardScale = 0.72;
			draft.IgnoredProcesses = ["test-app"];
			var events = 0;
			service.SettingsChanged += (_, _) => events++;
			service.Apply(draft);
			var persisted = File.ReadAllText(path);
			draft.CardScale = 1.1;
			draft.IgnoredProcesses.Add("unsaved-edit");
			Check(service.Current.CardScale == 0.72 && service.Current.IgnoredProcesses.SequenceEqual(new[] { "test-app" }) && events == 1,
				"Editing the old settings draft silently mutated the applied snapshot.");
			Check(File.ReadAllText(path) == persisted, "Editing a draft modified persisted settings.");
		}
		finally
		{
			File.Delete(path);
			Directory.Delete(directory);
		}
	}

	public static void RegistrationCannotCommitAfterDispose() => RegistrationRace("dispose");
	public static void CancelledRegistrationCannotCommit() => RegistrationRace("cancel");
	public static void RegistrationStillPublishesNormally() => RegistrationRace("normal");
	public static void HostedApplicationIdentityIsPreserved() => RegistrationRace("hosted");

	private static void RegistrationRace(string action)
	{
		using var window = new HiddenTestWindow();
		using var classifier = new PausingClassifier(action == "hosted");
		using var manager = new WindowsManager(classifier, new VirtualDesktopService());
		// Never Start(): no global WinEvent hooks and no user-window enumeration.
		typeof(WindowsManager).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, true);
		typeof(WindowsManager).GetField("_currentProcessId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, -1);
		var created = 0;
		manager.WindowCreated += (_, _) => Interlocked.Increment(ref created);
		var registration = (Task)typeof(WindowsManager).GetMethod("ScheduleRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(manager, [window.Handle])!;
		try
		{
			Check(classifier.Entered.Wait(TimeSpan.FromSeconds(5)), "Window classification never reached the barrier.");
			if (action == "dispose")
				Check(Task.Run(manager.Dispose).Wait(TimeSpan.FromSeconds(3)), "Disposal blocked on classification.");
			else if (action == "cancel")
				typeof(WindowsManager).GetMethod("CancelRegistration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [window.Handle]);
		}
		finally
		{
			classifier.Release.Set();
			Check(registration.Wait(TimeSpan.FromSeconds(5)), "The registration task did not finish.");
		}
		var expected = action is "normal" or "hosted" ? 1 : 0;
		Check(manager.Windows.Count() == expected && created == expected,
			"Window registration published stale state or lost a valid window.");
		if (action == "hosted") Check(manager.Windows.Single().ProcessId == int.MaxValue,
			"Registering a hosted window overwrote its resolved application identity.");
	}

	public static void NotificationWorkersStopOnDispose() => NotificationWorkerLifetime(dispose: true);
	public static void NotificationWorkersAreBoundedByTimeout() => NotificationWorkerLifetime(dispose: false);

	private static void NotificationWorkerLifetime(bool dispose)
	{
		Process? worker = null;
		var starts = 0;
		using var client = new NotificationAreaClient(_ =>
		{
			Interlocked.Increment(ref starts);
			worker = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
			{
				ArgumentList = { "--maintenance-worker-probe" }, UseShellExecute = false,
				CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
			});
			return worker;
		});
		try
		{
			var first = client.CaptureAsync();
			Check(worker is not null, "Worker probe did not start.");
			var workerId = worker!.Id;
			var queued = dispose ? client.CaptureAsync() : Task.FromResult<IReadOnlyList<NotificationIconSnapshot>>([]);
			if (dispose) client.Dispose();
			Check(Task.WhenAll(first, queued).Wait(TimeSpan.FromSeconds(8)), "A cancelled/expired worker did not complete.");
			Check(client.ShutdownAsync().Wait(TimeSpan.FromSeconds(3)), "Client shutdown did not join worker cleanup.");
			Check(first.Result.Count == 0 && queued.Result.Count == 0 && starts == 1,
				"A queued worker started after disposal or returned a stale snapshot.");
			var alive = false;
			try { using var probe = Process.GetProcessById(workerId); alive = !probe.HasExited; }
			catch (ArgumentException) { }
			Check(!alive, "The operation finished before its worker actually exited.");
		}
		finally
		{
			// Own probe only; never touch the real notification area or user apps.
			try { if (worker is not null && !worker.HasExited) { worker.Kill(true); worker.WaitForExit(3000); } }
			catch (InvalidOperationException) { }
			worker?.Dispose();
		}
	}

	private sealed class PausingClassifier(bool hosted = false) : IWindowClassifier, IDisposable
	{
		public ManualResetEventSlim Entered { get; } = new();
		public ManualResetEventSlim Release { get; } = new();
		public bool IsCandidate(WindowsWindow window, out string reason)
		{
			reason = "test-owned window";
			if (hosted) typeof(WindowsWindow).GetField("_processId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, int.MaxValue);
			Entered.Set();
			Check(Release.Wait(TimeSpan.FromSeconds(8)), "Classification barrier timed out.");
			return true;
		}
		public void Dispose() { Entered.Dispose(); Release.Dispose(); }
	}

	private sealed class HiddenTestWindow : IDisposable
	{
		private readonly Thread _thread;
		public IntPtr Handle { get; }
		public HiddenTestWindow()
		{
			var ready = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
			_thread = new Thread(() =>
			{
				var window = new MessageWindow();
				try
				{
					window.CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
					ready.SetResult(window.Handle);
					Application.Run();
				}
				catch (Exception error) { ready.TrySetException(error); }
				finally { window.DestroyHandle(); }
			}) { IsBackground = true };
			_thread.SetApartmentState(ApartmentState.STA);
			_thread.Start();
			Handle = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
		}
		public void Dispose()
		{
			PostMessage(Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
			Check(_thread.Join(TimeSpan.FromSeconds(5)), "The test window did not close.");
		}
		private sealed class MessageWindow : NativeWindow
		{
			protected override void WndProc(ref Message message)
			{
				if (message.Msg == 0x0010) { Application.ExitThread(); return; }
				base.WndProc(ref message);
			}
		}
		[DllImport("user32.dll")]
		private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
	}
}
