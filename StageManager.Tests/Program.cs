using StageManager;
using StageManager.Card3DPrototype;
using StageManager.Model;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using System.Drawing;
using System.IO;
using System.Numerics;

return TestRunner.Run();

internal static class TestRunner
{
	private static int _failures;

	public static int Run()
	{
		RunTest("Stage groups multiple applications and rejects duplicate handles", StageGrouping);
		RunTest("Stage window cycling is deterministic", StageCycling);
		RunTest("Composite preview exposes three thumbnails and overflow count", CompositePreview);
		RunTest("Adaptive cards fit or scroll at 2, 6, 10, and 20 stages", AdaptiveCards);
		RunTest("Settings are normalized and persisted atomically", SettingsPersistence);
		RunTest("Default and custom hotkeys parse", HotkeyParsing);
		RunTest("3D projection recedes toward the left edge", PerspectiveProjection);
		RunTest("3D hover response stays subtle without reversing order", SubtleHoverProjection);
		RunTest("Capture fallback produces a valid transparent card", CaptureFallback);
		RunTest("Prototype stage slots do not jump after activation", PrototypeStageSlotsStayStable);
		RunTest("Prototype card click toggles only the selected foreground window", PrototypeClickToggle);
		Console.WriteLine(_failures == 0 ? "All Stage_Manager_Lai tests passed." : $"{_failures} test(s) failed.");
		return _failures == 0 ? 0 : 1;
	}

	private static void StageGrouping()
	{
		var wechat = new FakeWindow(1, "WeChat", "wechat.exe");
		var codex = new FakeWindow(2, "Codex", "Codex.exe");
		var stage = new Stage(Stage.GetAppKey(wechat), wechat);
		stage.Add(codex);
		stage.Add(codex);
		Assert(stage.WindowCount == 2, "Duplicate handles were added.");
		Assert(stage.ContainsApp(Stage.GetAppKey(wechat)), "WeChat app key was lost.");
		Assert(stage.ContainsApp(Stage.GetAppKey(codex)), "Codex app key was lost.");
		stage.Remove(wechat);
		Assert(stage.WindowCount == 1 && stage.Windows[0].Handle == codex.Handle, "Removing a window damaged the stage.");
	}

	private static void StageCycling()
	{
		var first = new FakeWindow(10, "A", "a.exe");
		var second = new FakeWindow(11, "B", "b.exe");
		var stage = new Stage("cycle", first, second);
		Assert(stage.GetNextWindow()?.Handle == first.Handle, "Cycle did not start with the first window.");
		Assert(stage.GetNextWindow()?.Handle == second.Handle, "Cycle did not advance.");
		Assert(stage.GetNextWindow()?.Handle == first.Handle, "Cycle did not wrap.");
	}

	private static void CompositePreview()
	{
		var windows = Enumerable.Range(1, 5).Select(index => (IWindow)new FakeWindow(index, $"App{index}", $"app{index}.exe")).ToArray();
		var model = SceneModel.FromStage(new Stage("preview", windows));
		Assert(model.PreviewWindows.Count == 3, "Preview count is not capped at three.");
		Assert(model.ExtraWindowCount == 2 && model.ExtraWindowLabel == "+2", "Overflow badge is incorrect.");
		model.SetPerspectiveIndex(0, true);
		Assert(model.PerspectiveVisibility == System.Windows.Visibility.Visible && model.PerspectiveCardMargin.Left > 0,
			"Perspective metadata was not enabled.");
		Assert(model.PerspectiveAngle < 0 && model.PerspectiveZIndex > 0, "Perspective depth metadata is invalid.");
		model.SetPerspectiveIndex(0, false);
		Assert(model.PerspectiveVisibility == System.Windows.Visibility.Collapsed && model.PerspectiveCardMargin.Left == 0,
			"Flat-card metadata was not restored.");
	}

	private static void AdaptiveCards()
	{
		foreach (var count in new[] { 2, 6, 10, 20 })
		{
			var layout = CardLayoutCalculator.Calculate(900, count, 1);
			Assert(layout.Scale >= 0.55 && layout.Scale <= 1, $"Scale is outside bounds for {count} stages.");
			Assert(layout.CardWidth > 0 && layout.CardHeight > 0 && layout.Gap > 0, $"Invalid geometry for {count} stages.");
			var stride = layout.CardHeight + layout.Gap;
			for (var index = 1; index < count; index++)
			{
				var previousBottom = (index - 1) * stride + layout.CardHeight;
				var nextTop = index * stride;
				Assert(previousBottom < nextTop, $"Card slots overlap for {count} stages at index {index}.");
			}
			if (!layout.RequiresScrolling)
				Assert(count * stride <= 900.5, $"Non-scrolling layout overflows for {count} stages.");
		}
		Assert(!CardLayoutCalculator.Calculate(900, 2, 1).RequiresScrolling, "Two stages should fit without scrolling.");
		Assert(CardLayoutCalculator.Calculate(900, 20, 1).RequiresScrolling, "Twenty stages should scroll after reaching minimum size.");
	}

	private static void SettingsPersistence()
	{
		var directory = Path.Combine(Path.GetTempPath(), "StageManagerTests", Guid.NewGuid().ToString("N"));
		var path = Path.Combine(directory, "settings.json");
		try
		{
			var service = new SettingsService(path);
			Assert(!service.Current.AutoHideSidebar, "Sidebar auto-hide should be disabled by default.");
			Assert(service.Current.UsePerspectiveCards, "macOS-style cards should be enabled by default.");
			Assert(Math.Abs(service.Current.CardScale - 0.60) < 0.001, "Default card scale should be 60%.");
			var settings = service.CloneCurrent();
			settings.CardScale = 99;
			settings.SidebarOpacity = 0;
			settings.StageMode = StageMode.Focus;
			settings.UsePerspectiveCards = false;
			settings.IgnoredProcesses = new List<string> { "yuanbao", "YuanBao", "  explorer  " };
			service.Apply(settings);
			Assert(service.Current.CardScale == 1.25, "Maximum card scale was not clamped.");
			settings = service.CloneCurrent();
			settings.CardScale = 0;
			service.Apply(settings);
			Assert(service.Current.CardScale == 0.55, "Minimum card scale was not clamped.");
			Assert(service.Current.SidebarOpacity == 0.65, "Opacity was not clamped.");
			Assert(service.Current.IgnoredProcesses.Count == 2, "Ignored process names were not normalized.");
			var reloaded = new SettingsService(path);
			Assert(reloaded.Current.StageMode == StageMode.Focus, "Enum setting did not persist.");
			Assert(!reloaded.Current.UsePerspectiveCards, "Perspective-card setting did not persist.");
			Assert(File.Exists(path) && !File.Exists(path + ".tmp"), "Atomic settings replacement left an invalid temporary file.");
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, true);
		}
	}

	private static void HotkeyParsing()
	{
		foreach (var gesture in new[] { "Win+Alt+S", "Win+Alt+[", "Win+Alt+]", "Ctrl+Shift+F12" })
			Assert(HotkeyManager.TryParse(gesture, out _, out _), $"Valid hotkey '{gesture}' was rejected.");
		Assert(!HotkeyManager.TryParse("S", out _, out _), "Modifier-free hotkey should be rejected.");
		Assert(!HotkeyManager.TryParse("Win+Magic+S", out _, out _), "Unknown modifier should be rejected.");
	}

	private static void PerspectiveProjection()
	{
		var cardSize = new Vector2(196 * 0.65f, 122 * 0.65f);
		var pivot = new Vector2(cardSize.X * 0.88f, cardSize.Y * 0.5f);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(12, 220, 0),
			1,
			Vector3.Zero,
			Vector3.One,
			-7.5f,
			cardSize,
			pivot,
			new Vector2(450, 450),
			1200);
		var leftHeight = Vector2.Distance(polygon[0], polygon[3]);
		var rightHeight = Vector2.Distance(polygon[1], polygon[2]);
		Assert(leftHeight < rightHeight, "The left edge did not recede in perspective.");
		var center = polygon.Aggregate(Vector2.Zero, (sum, point) => sum + point) / polygon.Length;
		Assert(Card3DGeometry.Contains(polygon, center), "Projected-card hit testing rejected its center.");
		Assert(!Card3DGeometry.Contains(polygon, new Vector2(-1000, -1000)), "Projected-card hit testing accepted an outside point.");
	}

	private static void SubtleHoverProjection()
	{
		var cardSize = new Vector2(196 * 0.65f, 122 * 0.65f);
		var pivot = new Vector2(cardSize.X * 0.88f, cardSize.Y * 0.5f);
		var priorCenter = float.NegativeInfinity;
		var firstCenter = 0f;
		var lastCenter = 0f;
		for (var index = 0; index < 6; index++)
		{
			var transform = Card3DGeometry.CreateSubtleHoverTransform(index, 6, index == 2, 1f);
			var polygon = Card3DGeometry.ProjectCard(
				new Vector3(12, 300, 8),
				1,
				transform.Offset,
				transform.Scale,
				transform.Angle,
				cardSize,
				pivot,
				new Vector2(450, 450),
				1200);
			var center = polygon.Average(point => point.X);
			Assert(center > priorCenter, $"Hover window {index} did not preserve stacking order.");
			if (index == 0)
				firstCenter = center;
			if (index == 5)
				lastCenter = center;
			priorCenter = center;
		}
		Assert(lastCenter - firstCenter < 30f, "Hover spread is still large enough to look like a flying fan.");
		var hoveredTransform = Card3DGeometry.CreateSubtleHoverTransform(2, 6, true, 1f);
		Assert(hoveredTransform.Scale.X <= 1.02f, "Hovered card scales too aggressively.");
	}

	private static void CaptureFallback()
	{
		using var capture = new WindowFrameCapture();
		var frame = capture.Capture(new FakeWindow(0, "Missing", "missing.exe"), 254, 158, "+2");
		Assert(frame.IsPlaceholder, "Invalid HWND did not use the placeholder renderer.");
		Assert(frame.Pixels.Length == frame.Width * frame.Height * 4, "Placeholder pixel buffer size is invalid.");
		Assert(frame.Pixels[3] == 0, "Rounded placeholder corner is not transparent.");
		var centerAlpha = frame.Pixels[((frame.Height / 2) * frame.Width + frame.Width / 2) * 4 + 3];
		Assert(centerAlpha < 16, "Placeholder still paints a dark card background.");
	}

	private static void PrototypeStageSlotsStayStable()
	{
		var slots = new StableStageOrder();
		var baseline = new[]
		{
			new OrderedStage("A", new DateTime(2026, 1, 3)),
			new OrderedStage("B", new DateTime(2026, 1, 2)),
			new OrderedStage("C", new DateTime(2026, 1, 1))
		};
		var first = slots.Apply(baseline, stage => stage.Key, stage => stage.Priority);
		Assert(string.Concat(first.Select(stage => stage.Key)) == "ABC", "Initial stage priority was not respected.");

		var afterActivation = new[]
		{
			new OrderedStage("C", new DateTime(2026, 1, 5)),
			new OrderedStage("B", new DateTime(2026, 1, 2)),
			new OrderedStage("A", new DateTime(2026, 1, 3))
		};
		var stable = slots.Apply(afterActivation, stage => stage.Key, stage => stage.Priority);
		Assert(string.Concat(stable.Select(stage => stage.Key)) == "ABC", "Activation reordered card slots under the pointer.");
	}

	private static void PrototypeClickToggle()
	{
		var selected = new IntPtr(101);
		Assert(WindowClickBehavior.Decide(selected, selected, false, true) == WindowClickAction.Minimize,
			"Clicking the selected foreground window should minimize it.");
		Assert(WindowClickBehavior.Decide(selected, new IntPtr(202), false, true) == WindowClickAction.Activate,
			"Clicking a background window should activate that exact window.");
		Assert(WindowClickBehavior.Decide(selected, selected, true, true) == WindowClickAction.Activate,
			"A minimized window should be restored instead of minimized again.");
		Assert(WindowClickBehavior.Decide(selected, IntPtr.Zero, false, false) == WindowClickAction.Ignore,
			"A destroyed window should not trigger another application.");
	}

	private static void RunTest(string name, Action test)
	{
		try
		{
			test();
			Console.WriteLine($"PASS: {name}");
		}
		catch (Exception ex)
		{
			_failures++;
			Console.WriteLine($"FAIL: {name}{Environment.NewLine}{ex}");
		}
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException(message);
	}
}

internal sealed record OrderedStage(string Key, DateTime Priority);

internal sealed class FakeWindow : IWindow
{
	public FakeWindow(long handle, string processName, string executable)
	{
		Handle = new IntPtr(handle);
		ProcessName = processName;
		ProcessFileName = executable;
		ProcessExecutable = executable;
		Title = processName;
	}

	public event IWindowDelegate? WindowClosed;
	public event IWindowDelegate? WindowUpdated;
	public event IWindowDelegate? WindowFocused;
	public IntPtr Handle { get; }
	public string Title { get; }
	public string Class => "TestWindow";
	public IWindowLocation Location => new WindowLocation(100, 100, 800, 600, WindowState.Normal);
	public Rectangle Offset => Rectangle.Empty;
	public int ProcessId => Handle.ToInt32();
	public string ProcessFileName { get; }
	public string ProcessName { get; }
	public string ProcessExecutable { get; }
	public string? AppUserModelId => null;
	public bool CanLayout => true;
	public bool IsFocused { get; private set; }
	public bool IsMinimized { get; private set; }
	public bool IsMaximized => false;
	public bool IsMouseMoving => false;
	public void Focus() { IsFocused = true; WindowFocused?.Invoke(this); }
	public void ShowNormal() { IsMinimized = false; WindowUpdated?.Invoke(this); }
	public void ShowMaximized() { IsMinimized = false; WindowUpdated?.Invoke(this); }
	public void ShowMinimized() { IsMinimized = true; WindowUpdated?.Invoke(this); }
	public void ShowInCurrentState() { IsMinimized = false; WindowUpdated?.Invoke(this); }
	public void BringToTop() => WindowUpdated?.Invoke(this);
	public void Close() => WindowClosed?.Invoke(this);
	public void NotifyUpdated() => WindowUpdated?.Invoke(this);
}
