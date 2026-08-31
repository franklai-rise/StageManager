using StageManager;
using StageManager.Card3DPrototype;
using StageManager.Model;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Windows.Forms;

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
		RunTest("Capture fallback produces a light gray placeholder card", CaptureFallback);
		RunTest("Prototype stage slots do not jump after activation", PrototypeStageSlotsStayStable);
		RunTest("Prototype child-window slots do not jump after activation", PrototypeChildWindowSlotsStayStableAfterActivation);
		RunTest("Prototype card click toggles only the selected foreground window", PrototypeClickToggle);
		RunTest("Multi-window child selection stays expanded until the primary card is clicked", MultiWindowCardClicking);
		RunTest("Only collapsed multi-window primary cards arm hover expansion", MultiWindowHoverExpansion);
		RunTest("Expanded application groups keep every real window available", ExpandedApplicationGroupPaging);
		RunTest("Application group cards render a white logo surface", ApplicationGroupCardRendering);
		RunTest("Window cards respect the configured preview schedule", InitialCapturePolicy);
		RunTest("Sidebar hint reflects the configured idle behavior", SidebarHintFormatting);
		RunTest("Interface language switches in both directions", InterfaceLanguageTranslation);
		RunTest("Settings language button updates the visible interface", SettingsLanguageToggle);
		RunTest("Sidebar display selection follows the physical left edge", SidebarDisplaySelection);
		RunTest("Tray-hidden windows leave the sidebar while taskbar-minimized windows remain", ManagedWindowVisibility);
		RunTest("Off-screen recovery preserves visible windows and centers only lost windows", OffscreenRecoveryGeometry);
		RunTest("Initial window layouts keep the first baseline and reject recycled handles", InitialWindowLayoutMemoryBehavior);
		RunTest("Idle auto-hide waits one minute and wakes at the left edge", IdleAutoHideBehavior);
		RunTest("Full-screen or maximized sidebar reveals at the edge and hides after pointer leave", LargeWindowTransientSidebar);
		RunTest("Focus enhanced mode reserves normal work and overlays only exclusive full-screen", FocusEnhancedSidebarBehavior);
		RunTest("Focus enhanced placement preserves windows already right of the card column", FocusEnhancedWindowGeometry);
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
				  "IgnoredProcesses": ["explorer", "yuanbao", "custom-app"]
				}
				""");
			var service = new SettingsService(path);
			Assert(service.Current.SchemaVersion == 8, "Settings schema was not upgraded for Focus enhanced mode.");
			Assert(service.Current.LowMemoryRendering, "Low-memory rendering should be enabled by default.");
			Assert(!service.Current.IgnoredProcesses.Contains("explorer", StringComparer.OrdinalIgnoreCase),
				"The legacy default Explorer ignore entry was not migrated.");
			Assert(!service.Current.IgnoredProcesses.Contains("yuanbao", StringComparer.OrdinalIgnoreCase),
				"Yuanbao should no longer be ignored by default.");
			Assert(service.Current.IgnoredProcesses.Contains("custom-app", StringComparer.OrdinalIgnoreCase),
				"A user-selected ignored process was lost during migration.");
			var migratedJson = File.ReadAllText(path);
			Assert(migratedJson.Contains("\"SchemaVersion\": 8", StringComparison.Ordinal),
				"The migrated schema was not written back to disk.");
			Assert(!migratedJson.Contains("explorer", StringComparison.OrdinalIgnoreCase),
				"The legacy Explorer ignore entry remained in the persisted settings.");
			Assert(!migratedJson.Contains("yuanbao", StringComparison.OrdinalIgnoreCase),
				"The legacy Yuanbao ignore entry remained in the persisted settings.");
			Assert(!service.Current.AutoHideSidebar, "Sidebar auto-hide should be disabled by default.");
			Assert(service.Current.IdleAutoHideEnabled && service.Current.IdleAutoHideSeconds == 60,
				"3D sidebar idle auto-hide should default to one minute.");
			Assert(service.Current.PreviewRefreshMinutes == 5 && service.Current.PausePreviewRefreshWhenHidden,
				"Smart preview defaults were not preserved during migration.");
			Assert(service.Current.UsePerspectiveCards, "macOS-style cards should be enabled by default.");
			Assert(Math.Abs(service.Current.CardScale - 0.60) < 0.001, "Default card scale should be 60%.");
			var settings = service.CloneCurrent();
			settings.CardScale = 99;
			settings.SidebarOpacity = 0;
			settings.IdleAutoHideSeconds = 1;
			settings.PreviewRefreshMinutes = 0;
			settings.StageMode = StageMode.Focus;
			settings.UiLanguage = UiLanguage.SimplifiedChinese;
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
			Assert(service.Current.PreviewRefreshMinutes == 1, "Preview refresh minimum was not clamped.");
			Assert(service.Current.IgnoredProcesses.Count == 2, "Ignored process names were not normalized.");
			var reloaded = new SettingsService(path);
			Assert(reloaded.Current.StageMode == StageMode.Focus, "Enum setting did not persist.");
			Assert(reloaded.Current.UiLanguage == UiLanguage.SimplifiedChinese, "Interface language did not persist.");
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
		using var frame = capture.Capture(new FakeWindow(0, "Missing", "missing.exe"), 254, 158, "+2");
		Assert(frame.IsPlaceholder, "Invalid HWND did not use the placeholder renderer.");
		Assert(frame.Pixels.Length == frame.Width * frame.Height * 4, "Placeholder pixel buffer size is invalid.");
		Assert(frame.Pixels[3] == 0, "Rounded placeholder corner is not transparent.");
		var backgroundIndex = ((frame.Height / 2) * frame.Width + 10) * 4;
		Assert(frame.Pixels[backgroundIndex] >= 210 && frame.Pixels[backgroundIndex + 1] >= 210 &&
			frame.Pixels[backgroundIndex + 2] >= 210 && frame.Pixels[backgroundIndex + 3] >= 225,
			"Placeholder does not paint a visible light gray card background.");
		var hasCenteredTitlePixel = false;
		for (var y = frame.Height / 3; y < frame.Height * 2 / 3 && !hasCenteredTitlePixel; y++)
		{
			for (var x = frame.Width / 4; x < frame.Width * 3 / 4; x++)
			{
				var pixel = (y * frame.Width + x) * 4;
				if (frame.Pixels[pixel + 3] > 180 &&
					(frame.Pixels[pixel] < 175 || frame.Pixels[pixel + 1] < 175 || frame.Pixels[pixel + 2] < 175))
				{
					hasCenteredTitlePixel = true;
					break;
				}
			}
		}
		Assert(hasCenteredTitlePixel, "Placeholder did not render the window title in its center.");
		Assert(PlaceholderTitleRenderer.FormatTitle("这是一个很长的中文窗口标题用于测试", "fallback").EndsWith("..."),
			"Long Chinese placeholder titles are not shortened.");
		Assert(PlaceholderTitleRenderer.FormatTitle("Quarterly Simulation Results Document", "fallback").EndsWith("..."),
			"Long English placeholder titles are not shortened.");
		Assert(PlaceholderTitleRenderer.FormatTitle("Abaqus", "fallback") == "Abaqus",
			"Short placeholder titles should not gain an ellipsis.");
		Assert(PlaceholderTitleRenderer.GetPreferredFontFamily("中") == PlaceholderTitleRenderer.ChineseFontFamily,
			"Chinese title runs do not select STZhongsong.");
		Assert(PlaceholderTitleRenderer.GetPreferredFontFamily("A") == PlaceholderTitleRenderer.LatinFontFamily,
			"Latin title runs do not select Times New Roman.");
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

	private static void PrototypeChildWindowSlotsStayStableAfterActivation()
	{
		var slots = new StableWindowOrder();
		var first = new FakeWindow(201, "First", "app.exe");
		var second = new FakeWindow(202, "Second", "app.exe");
		var third = new FakeWindow(203, "Third", "app.exe");
		var baseline = slots.Apply("app", new[] { first, second, third }, window => window.Handle);
		var afterActivation = slots.Apply("app", new[] { third, first, second }, window => window.Handle);

		Assert(
			baseline.Select(window => window.Handle).SequenceEqual(afterActivation.Select(window => window.Handle)),
			"Activating a child window changed its card slot.");

		var fourth = new FakeWindow(204, "Fourth", "app.exe");
		var withNewWindow = slots.Apply("app", new[] { fourth, third, first, second }, window => window.Handle);
		Assert(withNewWindow[^1].Handle == fourth.Handle, "A newly opened child window was not appended to the stable list.");
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

	private static void MultiWindowHoverExpansion()
	{
		Assert(!MultiWindowCardInteraction.ShouldExpandOnHover(1, false, true),
			"A single-window card armed hover expansion.");
		Assert(MultiWindowCardInteraction.ShouldExpandOnHover(2, false, true),
			"A collapsed multi-window primary card did not arm hover expansion.");
		Assert(!MultiWindowCardInteraction.ShouldExpandOnHover(2, true, true),
			"An already-expanded application armed a second hover expansion.");
		Assert(!MultiWindowCardInteraction.ShouldExpandOnHover(2, false, false),
			"A child card armed hover expansion.");
		var groupCard = new CardHitTarget(
			"example-app",
			null,
			new[] { Vector2.Zero, Vector2.UnitX, Vector2.One },
			0,
			IsPrimaryCard: true);
		Assert(groupCard.Window is null && groupCard.IsPrimaryCard &&
			MultiWindowCardInteraction.ShouldExpandOnHover(2, false, groupCard.IsPrimaryCard),
			"A synthetic application group card was not eligible for hover expansion.");
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
		using var frame = capture.CaptureApplicationCard(new FakeWindow(0, "Example", "missing.exe"), 254, 158);
		Assert(!frame.IsPlaceholder, "The synthetic application card was marked as a failed window capture.");
		Assert(frame.Pixels[3] == 0, "The rounded application card corner is not transparent.");
		var backgroundIndex = (8 * frame.Width + frame.Width / 2) * 4;
		Assert(frame.Pixels[backgroundIndex] > 245 && frame.Pixels[backgroundIndex + 1] > 245 &&
			frame.Pixels[backgroundIndex + 2] > 245 && frame.Pixels[backgroundIndex + 3] == 255,
			"The synthetic application card does not have a white background.");
	}

	private static void InitialCapturePolicy()
	{
		var capturedAt = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
		Assert(WindowCapturePolicy.NeedsCapture(DateTime.MinValue, capturedAt),
			"A new window card did not request its initial snapshot.");
		Assert(!WindowCapturePolicy.NeedsCapture(capturedAt, capturedAt.AddMinutes(4).AddSeconds(59)),
			"A window card refreshed before the five-minute interval elapsed.");
		Assert(WindowCapturePolicy.NeedsCapture(capturedAt, capturedAt.AddMinutes(5)),
			"A window card did not request a five-minute snapshot refresh.");
		Assert(!WindowCapturePolicy.NeedsCapture(capturedAt, capturedAt.AddMinutes(14).AddSeconds(59), 15),
			"A custom preview interval refreshed too early.");
		Assert(WindowCapturePolicy.NeedsCapture(capturedAt, capturedAt.AddMinutes(15), 15),
			"A custom preview interval did not refresh on time.");
	}

	private static void SidebarHintFormatting()
	{
		Assert(SidebarHintFormatter.Format(true, 60).Contains("1 min", StringComparison.Ordinal),
			"One-minute idle behavior is not reflected in the sidebar hint.");
		Assert(SidebarHintFormatter.Format(true, 45).Contains("45 sec", StringComparison.Ordinal),
			"Sub-minute idle behavior is not reflected in the sidebar hint.");
		Assert(SidebarHintFormatter.Format(false, 60).Contains("Always visible", StringComparison.Ordinal),
			"Disabled idle auto-hide still advertises a timeout.");
		Assert(SidebarHintFormatter.Format(true, 60, UiLanguage.SimplifiedChinese).Contains("1 分钟", StringComparison.Ordinal),
			"Simplified Chinese idle behavior text is not formatted correctly.");
	}

	private static void SidebarDisplaySelection()
	{
		var displays = new[]
		{
			new Rectangle(0, 0, 1920, 1040),
			new Rectangle(-2560, 100, 2560, 1400),
			new Rectangle(1920, -200, 1600, 900)
		};
		var selected = SidebarDisplayPolicy.SelectLeftmost(displays, area => area);
		Assert(selected.Left == -2560, "The sidebar did not select the physical leftmost display.");
	}

	private static void ManagedWindowVisibility()
	{
		Assert(ManagedWindowPresence.ShouldDisplay(true, false), "A visible background window was removed from the sidebar.");
		Assert(ManagedWindowPresence.ShouldDisplay(false, true), "A taskbar-minimized window was removed from the sidebar.");
		Assert(!ManagedWindowPresence.ShouldDisplay(false, false), "A tray-hidden background window remained in the sidebar.");
	}

	private static void OffscreenRecoveryGeometry()
	{
		var workAreas = new[]
		{
			new Rectangle(0, 0, 1920, 1040),
			new Rectangle(1920, 0, 1920, 1040)
		};
		Assert(OffscreenWindowRecovery.IsMeaningfullyVisible(new Rectangle(2200, 120, 900, 700), workAreas),
			"A window on the second active display was incorrectly treated as off-screen.");
		Assert(!OffscreenWindowRecovery.IsMeaningfullyVisible(new Rectangle(3830, 300, 900, 700), workAreas),
			"A tiny edge sliver incorrectly prevented off-screen recovery.");
		Assert(!OffscreenWindowRecovery.IsMeaningfullyVisible(new Rectangle(5000, 300, 900, 700), workAreas),
			"A fully disconnected-display window was incorrectly treated as visible.");

		var centered = OffscreenWindowRecovery.CenterInWorkArea(
			new Rectangle(5000, 300, 900, 700),
			workAreas[0]);
		Assert(centered == new Rectangle(510, 170, 900, 700),
			"The recovered window was not centered while preserving its normal size.");
	}

	private static void InterfaceLanguageTranslation()
	{
		Assert(UiText.Translate(UiLanguage.SimplifiedChinese, "Appearance") == "外观",
			"The settings interface did not translate to Simplified Chinese.");
		Assert(UiText.Translate(UiLanguage.English, "外观") == "Appearance",
			"The settings interface did not switch back to English.");
	}

	private static void SettingsLanguageToggle()
	{
		Exception? failure = null;
		var thread = new Thread(() =>
		{
			try
			{
				var draft = new AppSettings();
				using var form = new SettingsForm(draft);
				Assert(form.FormBorderStyle == FormBorderStyle.Sizable && form.MaximizeBox,
					"The settings window is not freely resizable.");
				Assert(form.MinimumSize.Width >= 640 && form.MinimumSize.Height >= 700,
					"The settings window does not have a safe minimum size.");
				var languageSelector = Descendants(form)
					.OfType<ComboBox>()
					.Single(combo => combo.Items.Cast<object>().Contains("Simplified Chinese"));
				languageSelector.SelectedItem = "Simplified Chinese";
				Assert(draft.UiLanguage == UiLanguage.SimplifiedChinese,
					"The in-page language option did not update the settings draft.");
				Assert(form.Text.Contains("设置", StringComparison.Ordinal),
					"The settings window title did not switch to Chinese.");
				Assert(Descendants(form).Any(control => control.Text == "外观"),
					"The settings groups did not switch to Chinese.");
				Assert(Descendants(form).Any(control => control.Text == "界面语言"),
					"The in-page interface language label did not switch to Chinese.");
				Assert(Descendants(form).Any(control => control.Text == "Focus 增强模式（保留卡片栏区域）"),
					"The Focus enhanced mode option was not exposed in Chinese.");
				var switchButton = Descendants(form)
					.OfType<Button>()
					.Single(button => button.Text == "English");
				Assert(switchButton.Text == "English",
					"The language button did not offer a switch back to English.");
				typeof(Button)
					.GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
					.Invoke(switchButton, new object[] { EventArgs.Empty });
				Assert(draft.UiLanguage == UiLanguage.English,
					"The compact language button did not switch back to English.");
				var focusToggle = Descendants(form)
					.OfType<CheckBox>()
					.Single(checkBox => checkBox.Text == "Focus enhanced mode (reserve the card column)");
				focusToggle.Checked = true;
				var idleToggle = Descendants(form)
					.OfType<CheckBox>()
					.Single(checkBox => checkBox.Text == "Auto-hide after no pointer activity");
				Assert(!idleToggle.Enabled,
					"Idle auto-hide remained editable while Focus enhanced mode was selected.");
				var saveButton = Descendants(form)
					.OfType<Button>()
					.Single(button => button.Text == "Save");
				typeof(Button)
					.GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
					.Invoke(saveButton, new object[] { EventArgs.Empty });
				Assert(draft.StageMode == StageMode.Focus,
					"The settings form did not persist the Focus enhanced mode selection.");
			}
			catch (Exception exception)
			{
				failure = exception;
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Assert(thread.Join(TimeSpan.FromSeconds(10)), "The settings language smoke test timed out.");
		if (failure is not null)
			throw new InvalidOperationException("Settings language smoke test failed.", failure);

		static IEnumerable<Control> Descendants(Control parent)
		{
			foreach (Control child in parent.Controls)
			{
				yield return child;
				foreach (var descendant in Descendants(child))
					yield return descendant;
			}
		}
	}

	private static void InitialWindowLayoutMemoryBehavior()
	{
		var original = new FakeWindow(41001, "Original", "original.exe");
		var replacement = new FakeWindow(41001, "Replacement", "replacement.exe");
		var initial = new InitialWindowLayout(new Rectangle(120, 80, 900, 640), WasMaximized: false);
		var replacementLayout = new InitialWindowLayout(new Rectangle(300, 180, 1100, 760), WasMaximized: true);
		var captureCount = 0;
		InitialWindowLayout? restored = null;
		var memory = new InitialWindowLayoutMemory(
			window =>
			{
				captureCount++;
				return ReferenceEquals(window, original) ? initial : replacementLayout;
			},
			(_, layout) =>
			{
				restored = layout;
				return true;
			});

		memory.Observe([original]);
		memory.Observe([original]);
		Assert(captureCount == 1, "Moving or resizing would have overwritten the initial layout baseline.");
		Assert(memory.TryRestore(original) && restored == initial, "The first observed layout was not restored.");

		memory.Observe([replacement]);
		Assert(!memory.HasSnapshot(original), "A recycled HWND inherited the previous window lifetime.");
		Assert(memory.TryRestore(replacement) && restored == replacementLayout, "A replacement window did not get its own layout baseline.");
		memory.Observe(Array.Empty<IWindow>());
		Assert(memory.Count == 0, "Closed-window layout memory was not released.");

		var alive = true;
		var retained = new InitialWindowLayoutMemory(
			_ => initial,
			(_, _) => true,
			_ => alive);
		retained.Observe([original]);
		retained.Observe(Array.Empty<IWindow>());
		Assert(retained.HasSnapshot(original), "A live window on another desktop lost its layout baseline.");
		alive = false;
		retained.Observe(Array.Empty<IWindow>());
		Assert(retained.Count == 0, "A destroyed off-desktop window was not forgotten.");
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
		Assert(SidebarIdleBehavior.ShouldRequestHiddenEdgePoll(false, new Point(7, 500), screen, 8),
			"A hidden sidebar did not request a background edge poll.");
		Assert(!SidebarIdleBehavior.ShouldRequestHiddenEdgePoll(true, new Point(7, 500), screen, 8),
			"A visible sidebar incorrectly requested a background edge poll.");
		Assert(SidebarIdleBehavior.GetHiddenEdgePollingInterval(false) == 100,
			"Normal hidden edge polling did not use the low-power interval.");
		Assert(SidebarIdleBehavior.GetHiddenEdgePollingInterval(true) == 50,
			"Full-screen hidden edge polling did not use the responsive interval.");
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

	private static void FocusEnhancedSidebarBehavior()
	{
		Assert(!FocusEnhancedBehavior.UsesTransientSidebar(StageMode.Focus, maximizedOrFullScreen: true, exclusiveFullScreen: false),
			"A maximized window incorrectly hid the Focus enhanced sidebar.");
		Assert(FocusEnhancedBehavior.UsesTransientSidebar(StageMode.Focus, maximizedOrFullScreen: true, exclusiveFullScreen: true),
			"An exclusive full-screen window did not receive the transient sidebar behavior.");
		Assert(FocusEnhancedBehavior.UsesTransientSidebar(StageMode.Coexist, maximizedOrFullScreen: true, exclusiveFullScreen: false),
			"Standard mode lost its maximized-window transient sidebar behavior.");
		Assert(FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, true, false, false),
			"Focus enhanced mode did not reserve the visible card column.");
		Assert(!FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, true, true, false),
			"Focus enhanced mode reserved space over an exclusive full-screen window.");
		Assert(!FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, true, false, true),
			"A temporary edge reveal unexpectedly rearranged the desktop work area.");
		Assert(!FocusEnhancedBehavior.ShouldIdleHide(StageMode.Focus, true),
			"Focus enhanced mode still allowed idle auto-hide.");
		Assert(FocusEnhancedBehavior.ShouldIdleHide(StageMode.Coexist, true),
			"Standard mode no longer respected its idle auto-hide setting.");
	}

	private static void FocusEnhancedWindowGeometry()
	{
		var workArea = new Rectangle(0, 0, 1920, 1040);
		var available = FocusEnhancedBehavior.CalculateAvailableWorkArea(workArea, 220);
		Assert(available == new Rectangle(220, 0, 1700, 1040),
			"The Focus enhanced work area did not begin after the reserved card column.");

		var overlapping = new Rectangle(80, 120, 900, 700);
		var moved = FocusEnhancedBehavior.KeepWindowOutOfReservedColumn(overlapping, available);
		Assert(moved == new Rectangle(220, 120, 900, 700),
			"An overlapping normal window was not moved to the right without changing its size.");

		var alreadyClear = new Rectangle(360, 90, 1000, 720);
		Assert(FocusEnhancedBehavior.KeepWindowOutOfReservedColumn(alreadyClear, available) == alreadyClear,
			"A normal window already clear of the card column was moved unnecessarily.");

		var oversized = new Rectangle(-100, 20, 2200, 900);
		Assert(FocusEnhancedBehavior.KeepWindowOutOfReservedColumn(oversized, available) ==
			new Rectangle(220, 20, 1700, 900),
			"An oversized window was not safely fitted to the remaining desktop width.");
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
