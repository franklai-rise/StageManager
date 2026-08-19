using Microsoft.VisualStudio.TestTools.UnitTesting;
using StageManager;
using StageManager.Native;
using StageManager.Native.Window;
using StageManager.Model;
using StageManager.Services;
using StageManager.Settings;
using StageManager.Threading;
using System.Diagnostics;
using System.IO;

[TestClass]
public sealed class StageSessionTests
{
	[TestMethod]
	public async Task CrossApplicationMoveAndUndoRestoreOriginalStages()
	{
		var wechat = new FakeWindow(31001, "WeChat", "wechat.exe");
		var codex = new FakeWindow(31002, "Codex", "codex.exe");
		var powerPoint = new FakeWindow(31003, "POWERPNT", "powerpnt.exe");
		using var catalog = new FakeWindowCatalog(wechat, codex, powerPoint);
		using var fixture = new SceneManagerFixture(catalog);
		await fixture.Manager.Start();

		var original = fixture.Manager.GetStages().ToDictionary(stage => stage.InitialAppKey, stage => stage.Id);
		var codexStage = fixture.Manager.FindStageForWindow(codex)!;
		await fixture.Manager.MoveWindow(wechat.Handle, codexStage);

		var combined = fixture.Manager.FindStageForWindow(wechat);
		Assert.IsNotNull(combined);
		Assert.AreEqual(codexStage.Id, combined.Id);
		Assert.AreEqual(2, combined.WindowCount);
		Assert.AreEqual(2, fixture.Manager.GetStages().Count);
		Assert.IsTrue(fixture.Manager.CanUndo);

		await fixture.Manager.UndoLastStageAdjustment();

		Assert.AreEqual(3, fixture.Manager.GetStages().Count);
		Assert.AreNotEqual(
			fixture.Manager.FindStageForWindow(wechat)!.Id,
			fixture.Manager.FindStageForWindow(codex)!.Id);
		Assert.AreEqual(original[Stage.GetAppKey(wechat)], fixture.Manager.FindStageForWindow(wechat)!.Id);
		Assert.AreEqual(original[Stage.GetAppKey(codex)], fixture.Manager.FindStageForWindow(codex)!.Id);
		Assert.IsFalse(fixture.Manager.CanUndo);
	}

	[TestMethod]
	public async Task MergeAndSpecificWindowExtractionAreReversible()
	{
		var first = new FakeWindow(32001, "Browser", "browser.exe");
		var second = new FakeWindow(32002, "Browser", "browser.exe");
		var notes = new FakeWindow(32003, "Notes", "notes.exe");
		using var catalog = new FakeWindowCatalog(first, second, notes);
		using var fixture = new SceneManagerFixture(catalog);
		await fixture.Manager.Start();

		var browserStage = fixture.Manager.FindStageForWindow(first)!;
		var notesStage = fixture.Manager.FindStageForWindow(notes)!;
		await fixture.Manager.MergeStages(notesStage, browserStage);
		Assert.AreEqual(3, browserStage.WindowCount);
		Assert.AreEqual(1, fixture.Manager.GetStages().Count);

		await fixture.Manager.ExtractWindow(second.Handle);
		Assert.AreEqual(2, fixture.Manager.GetStages().Count);
		Assert.AreEqual(1, fixture.Manager.FindStageForWindow(second)!.WindowCount);

		await fixture.Manager.UndoLastStageAdjustment();
		Assert.AreEqual(1, fixture.Manager.GetStages().Count);
		Assert.AreEqual(3, fixture.Manager.FindStageForWindow(second)!.WindowCount);
		await fixture.Manager.UndoLastStageAdjustment();
		Assert.AreEqual(2, fixture.Manager.GetStages().Count);
		Assert.AreNotEqual(
			fixture.Manager.FindStageForWindow(notes)!.Id,
			fixture.Manager.FindStageForWindow(first)!.Id);
	}

	[TestMethod]
	public async Task PinToAllStagesIsRuntimeOnlyAndToggleable()
	{
		var editor = new FakeWindow(33001, "Editor", "editor.exe");
		var browser = new FakeWindow(33002, "Browser", "browser.exe");
		using var catalog = new FakeWindowCatalog(editor, browser);
		using var fixture = new SceneManagerFixture(catalog);
		await fixture.Manager.Start();

		Assert.IsFalse(fixture.Manager.IsPinnedToAllStages(editor.Handle));
		await fixture.Manager.TogglePinToAllStages(editor.Handle);
		Assert.IsTrue(fixture.Manager.IsPinnedToAllStages(editor.Handle));
		await fixture.Manager.TogglePinToAllStages(editor.Handle);
		Assert.IsFalse(fixture.Manager.IsPinnedToAllStages(editor.Handle));
	}

	[TestMethod]
	public async Task RelativeNavigationUsesStableRingInsteadOfMutatingMruOrder()
	{
		var first = new FakeWindow(35001, "First", "first.exe");
		var second = new FakeWindow(35002, "Second", "second.exe");
		var third = new FakeWindow(35003, "Third", "third.exe");
		using var catalog = new FakeWindowCatalog(first, second, third);
		using var fixture = new SceneManagerFixture(catalog);
		await fixture.Manager.Start();
		var firstStage = fixture.Manager.FindStageForWindow(first)!;
		var secondStage = fixture.Manager.FindStageForWindow(second)!;
		var thirdStage = fixture.Manager.FindStageForWindow(third)!;

		await fixture.Manager.SwitchTo(firstStage);
		await fixture.Manager.SwitchRelative(1);
		Assert.AreEqual(secondStage.Id, fixture.Manager.GetCurrentStage()!.Id);
		await fixture.Manager.SwitchRelative(1);
		Assert.AreEqual(thirdStage.Id, fixture.Manager.GetCurrentStage()!.Id);
		await fixture.Manager.SwitchRelative(1);
		Assert.AreEqual(firstStage.Id, fixture.Manager.GetCurrentStage()!.Id);
	}

	[TestMethod]
	public async Task UndoRejectsAReusedWindowHandle()
	{
		var original = new FakeWindow(36001, "Original", "original.exe");
		var target = new FakeWindow(36002, "Target", "target.exe");
		using var catalog = new FakeWindowCatalog(original, target);
		using var fixture = new SceneManagerFixture(catalog);
		await fixture.Manager.Start();
		await fixture.Manager.MoveWindow(original.Handle, fixture.Manager.FindStageForWindow(target)!);

		var replacement = new FakeWindow(36001, "Replacement", "replacement.exe");
		catalog.ReplaceWindow(replacement);
		await fixture.Manager.UndoLastStageAdjustment();

		var replacementStage = fixture.Manager.FindStageForWindow(replacement);
		var targetStage = fixture.Manager.FindStageForWindow(target);
		Assert.IsNotNull(replacementStage);
		Assert.IsNotNull(targetStage);
		Assert.AreNotEqual(replacementStage.Id, targetStage.Id, "A reused HWND was restored into the prior window's stage.");
	}

	[TestMethod]
	public async Task WindowMovedBetweenVirtualDesktopsIsRehomed()
	{
		var desktopA = Guid.NewGuid();
		var desktopB = Guid.NewGuid();
		var editor = new FakeWindow(37001, "Editor", "editor.exe");
		using var catalog = new FakeWindowCatalog(editor);
		catalog.MoveToDesktop(editor, desktopA);
		catalog.SwitchDesktop(desktopA);
		using var fixture = new SceneManagerFixture(catalog);
		await fixture.Manager.Start();
		Assert.AreEqual(1, fixture.Manager.GetStages().Count);

		catalog.MoveToDesktop(editor, desktopB);
		catalog.SwitchDesktop(desktopB);
		Assert.AreEqual(1, fixture.Manager.GetStages().Count);
		Assert.AreEqual(editor.Handle, fixture.Manager.GetStages()[0].Windows[0].Handle);

		catalog.SwitchDesktop(desktopA);
		Assert.AreEqual(0, fixture.Manager.GetStages().Count, "The old desktop retained a ghost stage after the window moved.");
	}

	[TestMethod]
	public void StageTransferPreservesManagerMinimizedAndLayoutState()
	{
		var window = new FakeWindow(38001, "Transfer", "transfer.exe");
		var source = new Stage("source", window);
		var target = new Stage("target");
		source.CaptureLayouts(new DisplayTopologyService());
		source.MarkMinimizedByManager(window.Handle);

		var transfer = source.Detach(window);
		target.Attach(window, transfer);

		Assert.IsTrue(target.WasMinimizedByManager(window.Handle));
		Assert.IsTrue(target.TryGetLayout(window.Handle, out var layout));
		Assert.IsNotNull(layout);
	}

	[TestMethod]
	public async Task ExactWindowActivationDoesNotCycleADifferentOneAtATimeTarget()
	{
		var first = new FakeWindow(39001, "Browser", "browser.exe");
		var second = new FakeWindow(39002, "Browser", "browser.exe");
		using var catalog = new FakeWindowCatalog(first, second);
		using var fixture = new SceneManagerFixture(catalog, settings =>
		{
			settings.StageMode = StageMode.Focus;
			settings.AppWindowsMode = AppWindowsMode.OneAtATime;
		});
		await fixture.Manager.Start();
		var stage = fixture.Manager.FindStageForWindow(first)!;

		await fixture.Manager.ActivateWindowInStage(stage, second.Handle);

		Assert.AreEqual(0, first.FocusCount);
		Assert.AreEqual(1, second.FocusCount);
		Assert.AreEqual(stage.Id, fixture.Manager.GetCurrentStage()!.Id);
	}

	[TestMethod]
	public async Task FiveHundredWindowLifecyclesConvergeWithoutGhostStages()
	{
		using var catalog = new FakeWindowCatalog();
		using var fixture = new SceneManagerFixture(catalog);
		await fixture.Manager.Start();
		var stopwatch = Stopwatch.StartNew();
		for (var index = 0; index < 500; index++)
		{
			var window = new FakeWindow(40000 + index, $"Probe {index}", "probe.exe");
			catalog.AddWindow(window, firstCreate: false);
			catalog.RemoveWindow(window.Handle);
		}
		stopwatch.Stop();
		await Task.Yield();

		Assert.AreEqual(0, fixture.Manager.GetStages().Count, "Closed test windows left ghost cards or stages.");
		Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
			$"The stage session needed {stopwatch.Elapsed.TotalMilliseconds:N0} ms to converge.");
	}

	private sealed class SceneManagerFixture : IDisposable
	{
		private readonly string _directory;
		private readonly VirtualDesktopService _virtualDesktops;

		public SceneManagerFixture(IWindowCatalog catalog, Action<AppSettings>? configure = null)
		{
			_directory = Path.Combine(Path.GetTempPath(), "StageManagerTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_directory);
			var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
			if (configure is not null)
			{
				var draft = settings.CloneCurrent();
				configure(draft);
				settings.Apply(draft);
			}
			_virtualDesktops = new VirtualDesktopService();
			Manager = new SceneManager(
				catalog,
				settings,
				_virtualDesktops,
				new DisplayTopologyService(),
				new InlineDispatcher());
		}

		public SceneManager Manager { get; }

		public void Dispose()
		{
			Manager.Dispose();
			_virtualDesktops.Dispose();
			try
			{
				Directory.Delete(_directory, recursive: true);
			}
			catch
			{
			}
		}
	}

	private sealed class InlineDispatcher : IUiDispatcher
	{
		private readonly int _threadId = Environment.CurrentManagedThreadId;
		public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;
		public Task InvokeAsync(Func<Task> action) => action();
	}

	private sealed class FakeWindowCatalog : IWindowCatalog
	{
		private readonly Dictionary<IntPtr, IWindow> _windows;
		private readonly Dictionary<IntPtr, Guid> _desktops;
		private readonly Dictionary<IntPtr, long> _generations;
		private EventHandler? _desktopChanged;
		private WindowCreateDelegate? _windowCreated;
		private WindowDelegate? _windowDestroyed;
		private WindowUpdateDelegate? _windowUpdated;

		public FakeWindowCatalog(params IWindow[] windows)
		{
			_windows = windows.ToDictionary(window => window.Handle);
			_desktops = windows.ToDictionary(window => window.Handle, _ => Guid.Empty);
			_generations = windows.ToDictionary(window => window.Handle, _ => 1L);
		}

		public event WindowCreateDelegate? WindowCreated { add => _windowCreated += value; remove => _windowCreated -= value; }
		public event WindowDelegate? WindowDestroyed { add => _windowDestroyed += value; remove => _windowDestroyed -= value; }
		public event WindowUpdateDelegate? WindowUpdated { add => _windowUpdated += value; remove => _windowUpdated -= value; }
		public event EventHandler? DesktopChanged { add => _desktopChanged += value; remove => _desktopChanged -= value; }
		public IEnumerable<IWindow> Windows => _windows.Values;
		public Task Start() => Task.CompletedTask;
		public void Stop() { }
		public void ReevaluateWindows() { }
		public bool TryGetWindow(IntPtr handle, out IWindow? window) => _windows.TryGetValue(handle, out window);
		public bool TryGetWindowInstanceId(IntPtr handle, out WindowInstanceId instanceId)
		{
			if (_windows.TryGetValue(handle, out var window))
			{
				instanceId = new WindowInstanceId(handle, window.ProcessId, DateTimeOffset.UnixEpoch, _generations[handle]);
				return true;
			}
			instanceId = default;
			return false;
		}
		public Guid GetDesktopId(IWindow window) => _desktops.GetValueOrDefault(window.Handle);
		public bool IsWindowOnCurrentDesktop(IWindow window) => GetDesktopId(window) == CurrentDesktopId;
		public Guid GetCurrentDesktopId(IntPtr foregroundHandle) => CurrentDesktopId;
		public Guid CurrentDesktopId { get; private set; }

		public void MoveToDesktop(IWindow window, Guid desktopId)
		{
			_desktops[window.Handle] = desktopId;
		}

		public void SwitchDesktop(Guid desktopId)
		{
			CurrentDesktopId = desktopId;
			_desktopChanged?.Invoke(this, EventArgs.Empty);
		}

		public void ReplaceWindow(IWindow window)
		{
			_windows[window.Handle] = window;
			_generations[window.Handle] = _generations.GetValueOrDefault(window.Handle) + 1;
			_desktops.TryAdd(window.Handle, CurrentDesktopId);
		}

		public void AddWindow(IWindow window, bool firstCreate)
		{
			_windows.Add(window.Handle, window);
			_desktops[window.Handle] = CurrentDesktopId;
			_generations[window.Handle] = 1;
			_windowCreated?.Invoke(window, firstCreate);
		}

		public void RemoveWindow(IntPtr handle)
		{
			if (!_windows.Remove(handle, out var window))
				return;
			_desktops.Remove(handle);
			_generations.Remove(handle);
			_windowDestroyed?.Invoke(window);
		}
		public void Dispose() { }
	}
}
