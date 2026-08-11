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
		RunTest("Collapsed cards retain subtle hover feedback without expanding", CollapsedHoverFeedback);
		RunTest("Expanded multi-window cards form a full vertical child list", SubtleHoverProjection);
		RunTest("Capture fallback produces a valid transparent card", CaptureFallback);
		RunTest("Prototype stage slots do not jump after activation", PrototypeStageSlotsStayStable);
		RunTest("Prototype card click toggles only the selected foreground window", PrototypeClickToggle);
		RunTest("Multi-window child selection stays expanded until the primary card is clicked", MultiWindowCardClicking);
		RunTest("Expanded application groups keep every real window available", ExpandedApplicationGroupPaging);
		RunTest("Application group cards render a white logo surface", ApplicationGroupCardRendering);
		RunTest("Window cards request only one initial capture", InitialCapturePolicy);
		RunTest("Tray-hidden windows leave the sidebar while taskbar-minimized windows remain", ManagedWindowVisibility);
		RunTest("Idle auto-hide waits one minute and wakes at the left edge", IdleAutoHideBehavior);
		RunTest("Full-screen or maximized sidebar reveals at the edge and hides after pointer leave", LargeWindowTransientSidebar);
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
			Directory.CreateDirectory(directory);
			File.WriteAllText(path, """
				{
				  "SchemaVersion": 3,
				  "IgnoredProcesses": ["explorer", "yuanbao"]
				}
				""");
			var service = new SettingsService(path);
			Assert(service.Current.SchemaVersion == 4, "Settings schema was not upgraded for Explorer folder support.");
			Assert(!service.Current.IgnoredProcesses.Contains("explorer", StringComparer.OrdinalIgnoreCase),
				"The legacy default Explorer ignore entry was not migrated.");
			Assert(service.Current.IgnoredProcesses.Contains("yuanbao", StringComparer.OrdinalIgnoreCase),
				"An unrelated ignored process was lost during migration.");
			var migratedJson = File.ReadAllText(path);
			Assert(migratedJson.Contains("\"SchemaVersion\": 4", StringComparison.Ordinal),
				"The migrated schema was not written back to disk.");
			Assert(!migratedJson.Contains("explorer", StringComparison.OrdinalIgnoreCase),
				"The legacy Explorer ignore entry remained in the persisted settings.");
			Assert(!service.Current.AutoHideSidebar, "Sidebar auto-hide should be disabled by default.");
			Assert(service.Current.IdleAutoHideEnabled && service.Current.IdleAutoHideSeconds == 60,
				"3D sidebar idle auto-hide should default to one minute.");
			Assert(service.Current.UsePerspectiveCards, "macOS-style cards should be enabled by default.");
			Assert(Math.Abs(service.Current.CardScale - 0.60) < 0.001, "Default card scale should be 60%.");
			var settings = service.CloneCurrent();
			settings.CardScale = 99;
			settings.SidebarOpacity = 0;
			settings.IdleAutoHideSeconds = 1;
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
			Assert(service.Current.IdleAutoHideSeconds == 15, "Idle auto-hide minimum was not clamped.");
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
		var firstCenterX = 0f;
		var lastCenterX = 0f;
		var stride = Card3DGeometry.CalculateExpandedListStride(cardSize.Y, 1f);
		var childIndent = 18f;
		Assert(stride > cardSize.Y + 10f, "Expanded child cards are still vertically stacked instead of separated.");
		for (var index = 0; index < 6; index++)
		{
			var transform = Card3DGeometry.CreateExpandedListTransform(index, 2, 1f, stride, childIndent);
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
			var center = polygon.Average(point => point.Y);
			Assert(center > priorCenter, $"Expanded child window {index} did not preserve vertical order.");
			if (index == 0)
			{
				firstCenter = center;
				firstCenterX = polygon.Average(point => point.X);
			}
			if (index == 5)
			{
				lastCenter = center;
				lastCenterX = polygon.Average(point => point.X);
			}
			priorCenter = center;
		}
		Assert(lastCenter - firstCenter >= cardSize.Y * 5f, "Expanded child cards still overlap vertically.");
		Assert(lastCenter - firstCenter < 520f, "Expanded child list uses excessive vertical spacing.");
		Assert(Math.Abs(lastCenterX - firstCenterX) >= 12f && Math.Abs(lastCenterX - firstCenterX) < 30f,
			"Expanded child cards did not keep the small connector-line indent.");
		var hoveredTransform = Card3DGeometry.CreateExpandedListTransform(2, 2, 1f, stride, childIndent);
		Assert(hoveredTransform.Scale.X <= 1.025f, "Hovered card scales too aggressively.");
		Assert(hoveredTransform.Offset.Z >= 20f, "Hovered card does not rise clearly above the stack.");
	}

	private static void CollapsedHoverFeedback()
	{
		var normal = Card3DGeometry.CreateCollapsedStackTransform(0, false, 1f);
		var hovered = Card3DGeometry.CreateCollapsedStackTransform(0, true, 1f);
		Assert(hovered.Offset.Z > normal.Offset.Z, "Collapsed card does not move forward on hover.");
		Assert(hovered.Offset.X - normal.Offset.X < 2f, "Collapsed hover moves too far sideways.");
		Assert(hovered.Scale.X > normal.Scale.X && hovered.Scale.X <= 1.015f,
			"Collapsed hover scaling is missing or too aggressive.");
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

	private static void MultiWindowCardClicking()
	{
		Assert(MultiWindowCardInteraction.Decide(1, false, true) == MultiWindowCardClickAction.SelectWindow,
			"A single-window card did not remain a direct selection.");
		Assert(MultiWindowCardInteraction.Decide(4, false, true) == MultiWindowCardClickAction.Expand,
			"A collapsed multi-window card did not require an explicit first click to expand.");
		Assert(MultiWindowCardInteraction.Decide(4, true, false) == MultiWindowCardClickAction.SelectWindow,
			"Selecting an expanded child card did not preserve the expanded list.");
		Assert(MultiWindowCardInteraction.Decide(4, true, true) == MultiWindowCardClickAction.Collapse,
			"Clicking the expanded primary card did not collapse the child list.");
	}

	private static void ExpandedApplicationGroupPaging()
	{
		var windows = Enumerable.Range(101, 8).Select(value => new IntPtr(value)).ToArray();
		var firstPage = MultiWindowCardInteraction.CreateExpandedChildPage(windows, 0, 5);
		Assert(firstPage.VisibleChildren.SequenceEqual(windows.Take(5)) && firstPage.PageCount == 2,
			"The first group page did not contain the first five real windows.");

		var secondPage = MultiWindowCardInteraction.CreateExpandedChildPage(windows, 1, 5);
		Assert(secondPage.VisibleChildren.SequenceEqual(windows.Skip(5)),
			"The second group page did not contain every remaining real window.");
		Assert(firstPage.VisibleChildren.Concat(secondPage.VisibleChildren).SequenceEqual(windows),
			"The synthetic application card displaced a real window from the expanded group.");
	}

	private static void ApplicationGroupCardRendering()
	{
		using var capture = new WindowFrameCapture();
		var frame = capture.CaptureApplicationCard(new FakeWindow(0, "Example", "missing.exe"), 254, 158);
		Assert(!frame.IsPlaceholder, "The synthetic application card was marked as a failed window capture.");
		Assert(frame.Pixels[3] == 0, "The rounded application card corner is not transparent.");
		var backgroundIndex = (8 * frame.Width + frame.Width / 2) * 4;
		Assert(frame.Pixels[backgroundIndex] > 245 && frame.Pixels[backgroundIndex + 1] > 245 &&
			frame.Pixels[backgroundIndex + 2] > 245 && frame.Pixels[backgroundIndex + 3] == 255,
			"The synthetic application card does not have a white background.");
	}

	private static void InitialCapturePolicy()
	{
		Assert(WindowCapturePolicy.NeedsInitialCapture(DateTime.MinValue),
			"A new window card did not request its initial snapshot.");
		Assert(!WindowCapturePolicy.NeedsInitialCapture(DateTime.UtcNow),
			"A captured window card still requested a periodic refresh.");
	}

	private static void ManagedWindowVisibility()
	{
		Assert(ManagedWindowPresence.ShouldDisplay(true, false), "A visible background window was removed from the sidebar.");
		Assert(ManagedWindowPresence.ShouldDisplay(false, true), "A taskbar-minimized window was removed from the sidebar.");
		Assert(!ManagedWindowPresence.ShouldDisplay(false, false), "A tray-hidden background window remained in the sidebar.");
	}

	private static void IdleAutoHideBehavior()
	{
		var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
		Assert(!SidebarIdleBehavior.ShouldHide(true, 60, now.AddSeconds(-59), now), "Sidebar hid before one idle minute elapsed.");
		Assert(SidebarIdleBehavior.ShouldHide(true, 60, now.AddSeconds(-60), now), "Sidebar did not hide after one idle minute.");
		Assert(!SidebarIdleBehavior.ShouldHide(false, 60, now.AddHours(-1), now), "Disabled idle auto-hide still hid the sidebar.");
		var screen = new Rectangle(0, 0, 1920, 1040);
		Assert(SidebarIdleBehavior.IsNearLeftEdge(new Point(7, 500), screen, 8), "Left-edge activation zone rejected a nearby pointer.");
		Assert(!SidebarIdleBehavior.IsNearLeftEdge(new Point(20, 500), screen, 8), "Left-edge activation zone is wider than requested.");
	}

	private static void LargeWindowTransientSidebar()
	{
		var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
		Assert(TransientSidebarBehavior.Decide(true, false, true, false, DateTime.MinValue, now) == TransientSidebarAction.Reveal,
			"A hidden sidebar did not reveal at the left edge for a full-screen or maximized window.");
		Assert(TransientSidebarBehavior.Decide(true, true, false, true, now, now.AddSeconds(1)) == TransientSidebarAction.None,
			"A transient sidebar hid while the pointer was still over it.");
		Assert(TransientSidebarBehavior.Decide(true, true, false, false, now, now.AddMilliseconds(200)) == TransientSidebarAction.None,
			"A transient sidebar ignored the pointer-leave grace period.");
		Assert(TransientSidebarBehavior.Decide(true, true, false, false, now, now.AddMilliseconds(400)) == TransientSidebarAction.Hide,
			"A transient sidebar did not hide after the pointer left.");
		Assert(TransientSidebarBehavior.Decide(false, false, true, false, DateTime.MinValue, now) == TransientSidebarAction.None,
			"Large-window behavior changed the normal sidebar edge policy.");
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
