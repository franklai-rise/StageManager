using StageManager;
using StageManager.Card3DPrototype;
using StageManager.Card3DPrototype.NotificationArea;
using StageManager.Model;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Windows.Forms;

if (args.Length > 0 && args[0] == "--notification-area-worker")
{
	return InteractionMaintenanceTests.RunNotificationWorker(args.Skip(1).ToArray());
}
if (args is ["--live-activation-probe"])
{
	InteractionMaintenanceTests.VerifyLiveActivation();
	return 0;
}

if (args is ["--maintenance-worker-probe"])
{
	Thread.Sleep(TimeSpan.FromSeconds(15));
	return 0;
}

if (args is ["--desktop-toggle-probe", var probeMode])
{
	ApplicationConfiguration.Initialize();
	Application.Run(new Form
	{
		Text = $"Stage Manager desktop toggle probe - {probeMode}",
		Width = 620,
		Height = 420,
		ShowInTaskbar = true,
		StartPosition = FormStartPosition.CenterScreen,
		WindowState = probeMode.Equals("maximized", StringComparison.OrdinalIgnoreCase)
			? FormWindowState.Maximized
			: FormWindowState.Normal
	});
	return 0;
}

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
		RunTest("Main card column scrolls even when its content already fits", MainColumnAlwaysScrolls);
		RunTest("Precision wheel input preserves small and coalesced deltas", PrecisionScrollInput);
		RunTest("Elastic scrolling reverses, respects bounds and comes to rest", ElasticScrollSettling);
		RunTest("Scrolling keeps hidden-icons and desktop controls attached to the card column", ScrollRendererLayers);
		RunTest("Expansion grows down without moving the primary or upper cards at mixed DPI", ExpansionRegressionTests.AnchoredExpansion);
		RunTest("Moving card hits use the same easing and snap correctly when animations stop", ExpansionRegressionTests.MotionAndHits);
		RunTest("Expansion preserves an in-flight wheel spring", ExpansionRegressionTests.ScrollDuringExpansion);
		RunTest("Rapid expansion remains anchored and targets its own child windows", ExpansionRegressionTests.RapidExpansion);
		RunTest("Recently hidden previews are reused within a bounded expiring cache", ExpansionRegressionTests.WarmPreviews);
		RunTest("Changing DPI rebuilds visuals, textures and hits without changing group state", ExpansionRegressionTests.DpiChangesRebuildExistingCards);
		RunTest("Late or failed captures cannot overwrite newer card state", ExpansionRegressionTests.LateCaptureCannotUndoInvalidation);
		RunTest("Shutdown releases a captured frame before its UI callback executes", ExpansionRegressionTests.QueuedCaptureIsReleasedOnShutdown);
		RunTest("Idle preview scheduling avoids per-window temporary allocations", ExpansionRegressionTests.IdleCaptureSchedulingAllocations);
		RunTest("Settings are normalized and persisted atomically", SettingsPersistence);
		RunTest("Applied settings cannot be mutated by a previously saved draft", MaintenanceRegressionTests.AppliedSettingsOwnTheirSnapshot);
		RunTest("Desktop-icon status survives registry failures and recovers without toggling", MaintenanceRegressionTests.DesktopRegistryFailuresRecover);
		RunTest("Browser shortcut resolution survives inaccessible registry candidates", MaintenanceRegressionTests.BrowserRegistryFailuresUseFallback);
		RunTest("Window registration cannot publish after disposal", MaintenanceRegressionTests.RegistrationCannotCommitAfterDispose);
		RunTest("Cancelling classification prevents late window registration", MaintenanceRegressionTests.CancelledRegistrationCannotCommit);
		RunTest("Normal delayed registration still publishes exactly once", MaintenanceRegressionTests.RegistrationStillPublishesNormally);
		RunTest("Hosted application identity may differ from the window owner", MaintenanceRegressionTests.HostedApplicationIdentityIsPreserved);
		RunTest("Disposal cancels active and queued notification workers", MaintenanceRegressionTests.NotificationWorkersStopOnDispose);
		RunTest("Timed-out notification workers exit before the request finishes", MaintenanceRegressionTests.NotificationWorkersAreBoundedByTimeout);
		RunTest("Independent startup shortcut is owned by Windows Explorer", IndependentStartupShortcut);
		RunTest("Default and custom hotkeys parse", HotkeyParsing);
		RunTest("3D projection recedes toward the left edge", PerspectiveProjection);
		RunTest("Collapsed cards retain subtle hover feedback without expanding", CollapsedHoverFeedback);
		RunTest("Expanded multi-window cards form a full vertical child list", SubtleHoverProjection);
		RunTest("Hover contact expands immediately with a non-clickable card cascade", InteractionMaintenanceTests.HoverExpansionStartsImmediatelyAndCascades);
		RunTest("Activation fallback matches only the intended notification icon", InteractionMaintenanceTests.NotificationIconMatchingIsSpecific);
		RunTest("Capture fallback produces a light gray placeholder card", CaptureFallback);
		RunTest("Prototype stage slots do not jump after activation", PrototypeStageSlotsStayStable);
		RunTest("Prototype child-window slots do not jump after activation", PrototypeChildWindowSlotsStayStableAfterActivation);
		RunTest("Prototype card click preserves placement and toggles the foreground window", PrototypeClickPreservesPlacement);
		RunTest("Card clicks execute immediately on valid release", CardClicksAreImmediate);
		RunTest("A card double-click executes only its first click", CardDoubleClickRunsOnce);
		RunTest("Cancelled card presses never execute or swallow the next click", CancelledCardClicksDoNothing);
		RunTest("Card hints describe the current action in both languages", CardHintsMatchActions);
		RunTest("Pointer button transitions detect outside clicks without repeating held input", PointerButtonTransitions);
		RunTest("Vertical drag changes only the hidden-icons card height and clamps safely", NotificationAreaVerticalDragging);
		RunTest("Hidden-icons drag and refresh controls cannot trigger each other", NotificationAreaControlsAreSeparated);
		RunTest("Show Desktop toggle changes state only after a successful desktop operation", DesktopToggleState);
		RunTest("Desktop-icons toggle follows Windows state and changes only after success", DesktopIconVisibilityState);
		RunTest("Show Desktop restores normal, maximized and hidden application windows", DesktopToggleIntegrationTests.RestoresNativeWindows);
		RunTest("Partial desktop restore failures retain only failed windows for retry", DesktopToggleIntegrationTests.FailedRestoreRetainsOnlyPendingWindows);
		RunTest("Already restored desktop windows are not reported as failures", DesktopToggleIntegrationTests.AlreadyRestoredWindowDoesNotCauseFailure);
		RunTest("Notification-area icons keep native sizes in a non-overlapping grid", NotificationTrayIconLayout);
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
		RunTest("Focus mode never treats a maximized window as exclusive full-screen", FocusFullScreenClassification);
		RunTest("Focus enhanced mode reserves normal work and overlays only exclusive full-screen", FocusEnhancedSidebarBehavior);
		RunTest("Focus enhanced placement preserves windows already right of the card column", FocusEnhancedWindowGeometry);
		Console.WriteLine(_failures == 0 ? "All Stage_Manager_Lai tests passed." : $"{_failures} test(s) failed.");
		return _failures == 0 ? 0 : 1;
	}

	private static void NotificationAreaVerticalDragging()
	{
		Assert(NotificationAreaVerticalDragBehavior.CalculateOffset(-180, 125, 1.25f) == -80,
			"Vertical movement was not converted from device pixels.");
		Assert(NotificationAreaVerticalDragBehavior.CalculateOffset(-20, 100, 1f) == 0,
			"Downward dragging moved the card below its bottom anchor.");
		Assert(NotificationAreaVerticalDragBehavior.CalculateOffset(-1990, -100, 1f) == -2000,
			"Upward dragging exceeded the upper position limit.");
	}

	private static void NotificationAreaControlsAreSeparated()
	{
		Assert(!NotificationAreaControlBehavior.ShouldRefresh(true, true, dragActive: true),
			"Dragging incorrectly triggered a refresh.");
		Assert(!NotificationAreaControlBehavior.ShouldRefresh(true, false, dragActive: false),
			"Releasing outside the refresh button incorrectly triggered a refresh.");
		Assert(NotificationAreaControlBehavior.ShouldRefresh(true, true, dragActive: false),
			"A deliberate refresh click was rejected.");
	}

	private static void DesktopToggleState()
	{
		var invocations = 0;
		var requests = new List<bool>();
		var toggle = new DesktopToggleService(showDesktop => { invocations++; requests.Add(showDesktop); return true; });
		Assert(toggle.TryToggle(out var error) && error is null && toggle.IsDesktopShown,
			"The first successful click did not enter Show Desktop state.");
		Assert(toggle.TryToggle(out error) && error is null && !toggle.IsDesktopShown && invocations == 2 && requests.SequenceEqual([true, false]),
			"The second successful click did not restore the desktop state.");
		toggle.TryToggle(out _);
		toggle.MarkDesktopDismissed();
		Assert(!toggle.IsDesktopShown, "Opening a window did not clear the stale active indicator.");
		var failed = new DesktopToggleService(_ => false);
		Assert(!failed.TryToggle(out error) && !failed.IsDesktopShown && !string.IsNullOrWhiteSpace(error),
			"A rejected Windows Shell command changed the desktop state.");
		var restoredOnExit = new List<bool>();
		var exiting = new DesktopToggleService(showDesktop => { restoredOnExit.Add(showDesktop); return true; });
		Assert(exiting.TryToggle(out _) && exiting.TryRestore(out _) &&
			restoredOnExit.SequenceEqual([true, false]) && !exiting.IsDesktopShown,
			"Shutdown protection did not restore an active desktop session.");
	}

	private static void DesktopIconVisibilityState()
	{
		var systemVisible = false;
		var toggles = 0;
		var service = new DesktopIconVisibilityService(
			() => systemVisible,
			() => { toggles++; systemVisible = !systemVisible; return true; });
		Assert(!service.IconsVisible, "The desktop-icons card did not read the current Windows hidden state.");
		Assert(service.TryToggle(out var error) && error is null && service.IconsVisible && toggles == 1,
			"The desktop-icons card did not expose the successful Windows toggle state.");
		systemVisible = false;
		Assert(service.Refresh() && !service.IconsVisible,
			"An external Windows desktop-icon setting change did not update the card state.");

		var failed = new DesktopIconVisibilityService(() => false, () => false);
		Assert(!failed.TryToggle(out var failedError) && !failed.IconsVisible && !string.IsNullOrWhiteSpace(failedError),
			"A failed desktop-icon request incorrectly changed the card state.");
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
			Assert(service.Current.SchemaVersion == 16, "Settings schema was not upgraded for the desktop-icons card.");
			Assert(service.Current.LowMemoryRendering, "Low-memory rendering should be enabled by default.");
			Assert(!service.Current.IgnoredProcesses.Contains("explorer", StringComparer.OrdinalIgnoreCase),
				"The legacy default Explorer ignore entry was not migrated.");
			Assert(!service.Current.IgnoredProcesses.Contains("yuanbao", StringComparer.OrdinalIgnoreCase),
				"Yuanbao should no longer be ignored by default.");
			Assert(service.Current.IgnoredProcesses.Contains("custom-app", StringComparer.OrdinalIgnoreCase),
				"A user-selected ignored process was lost during migration.");
			var migratedJson = File.ReadAllText(path);
			Assert(migratedJson.Contains("\"SchemaVersion\": 16", StringComparison.Ordinal),
				"The migrated schema was not written back to disk.");
			Assert(!migratedJson.Contains("\"explorer\"", StringComparison.OrdinalIgnoreCase),
				"The legacy Explorer ignore entry remained in the persisted settings.");
			Assert(!migratedJson.Contains("\"yuanbao\"", StringComparison.OrdinalIgnoreCase),
				"The legacy Yuanbao ignore entry remained in the persisted settings.");
			Assert(!service.Current.AutoHideSidebar, "Sidebar auto-hide should be disabled by default.");
			Assert(service.Current.IdleAutoHideEnabled && service.Current.IdleAutoHideSeconds == 60,
				"3D sidebar idle auto-hide should default to one minute.");
			Assert(service.Current.PreviewRefreshMinutes == 5 && service.Current.PausePreviewRefreshWhenHidden,
				"Smart preview defaults were not preserved during migration.");
			Assert(service.Current.UsePerspectiveCards, "macOS-style cards should be enabled by default.");
			Assert(Math.Abs(service.Current.CardScale - 0.60) < 0.001, "Default card scale should be 60%.");
			Assert(service.Current.SidebarVerticalOffset == -80, "The sidebar should default to 80 pixels above center.");
			Assert(service.Current.ShowExplorerButton, "The Explorer quick button should be enabled by default.");
			Assert(service.Current.ShowChromeQuickLaunch && service.Current.ShowEdgeQuickLaunch,
				"Browser quick-launch cards should be enabled by default.");
			Assert(service.Current.ShowExpandedPinButton, "The expanded-card pin button should be enabled by default.");
			Assert(service.Current.ShowNotificationAreaCard, "The notification-area card should be enabled by default.");
			Assert(service.Current.ShowDesktopButton, "The Show Desktop button should be enabled by default.");
			Assert(service.Current.ShowDesktopIconsButton, "The desktop-icons button should be enabled by default.");
			Assert(service.Current.NotificationAreaVerticalOffset == 0, "The obsolete notification-area offset was not removed.");
			var settings = service.CloneCurrent();
			settings.CardScale = 99;
			settings.SidebarVerticalOffset = 999;
			settings.SidebarOpacity = 0;
			settings.IdleAutoHideSeconds = 1;
			settings.PreviewRefreshMinutes = 0;
			settings.StageMode = StageMode.Focus;
			settings.UiLanguage = UiLanguage.SimplifiedChinese;
			settings.UsePerspectiveCards = false;
			settings.ShowExplorerButton = false;
			settings.ShowChromeQuickLaunch = false;
			settings.ShowEdgeQuickLaunch = false;
			settings.ShowExpandedPinButton = false;
			settings.ShowNotificationAreaCard = false;
			settings.ShowDesktopButton = false;
			settings.ShowDesktopIconsButton = false;
			settings.NotificationAreaVerticalOffset = -250;
			settings.IgnoredProcesses = new List<string> { "yuanbao", "YuanBao", "  explorer  " };
			service.Apply(settings);
			Assert(service.Current.CardScale == 1.25, "Maximum card scale was not clamped.");
			Assert(service.Current.SidebarVerticalOffset == 400, "Maximum sidebar vertical offset was not clamped.");
			Assert(!service.Current.ShowExplorerButton, "The Explorer quick button preference was not persisted.");
			Assert(!service.Current.ShowChromeQuickLaunch && !service.Current.ShowEdgeQuickLaunch,
				"Browser quick-launch preferences were not persisted.");
			Assert(!service.Current.ShowExpandedPinButton, "The expanded-card pin button preference was not persisted.");
			Assert(!service.Current.ShowNotificationAreaCard, "The notification-area card preference was not persisted.");
			Assert(!service.Current.ShowDesktopButton, "The Show Desktop button preference was not persisted.");
			Assert(!service.Current.ShowDesktopIconsButton, "The desktop-icons button preference was not persisted.");
			Assert(service.Current.NotificationAreaVerticalOffset == 0, "An obsolete independent notification-card position was retained.");
			settings = service.CloneCurrent();
			settings.CardScale = 0;
			settings.SidebarVerticalOffset = -999;
			service.Apply(settings);
			Assert(service.Current.CardScale == 0.55, "Minimum card scale was not clamped.");
			Assert(service.Current.SidebarVerticalOffset == -400, "Minimum sidebar vertical offset was not clamped.");
			Assert(service.Current.SidebarOpacity == 0.65, "Opacity was not clamped.");
			Assert(service.Current.IdleAutoHideSeconds == 15, "Idle auto-hide minimum was not clamped.");
			Assert(service.Current.PreviewRefreshMinutes == 1, "Preview refresh minimum was not clamped.");
			Assert(service.Current.IgnoredProcesses.Count == 2, "Ignored process names were not normalized.");
			var reloaded = new SettingsService(path);
			Assert(reloaded.Current.StageMode == StageMode.Focus, "Enum setting did not persist.");
			Assert(reloaded.Current.UiLanguage == UiLanguage.SimplifiedChinese, "Interface language did not persist.");
			Assert(!reloaded.Current.UsePerspectiveCards, "Perspective-card setting did not persist.");
			Assert(!reloaded.Current.ShowChromeQuickLaunch && !reloaded.Current.ShowEdgeQuickLaunch,
				"Browser quick-launch preferences were lost after reload.");
			Assert(reloaded.Current.SidebarVerticalOffset == -400, "Sidebar vertical position did not persist.");
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

	private static void MainColumnAlwaysScrolls()
	{
		var fitting = SidebarScrollBehavior.Calculate(300, 240, 900, 12);
		Assert(fitting.Minimum < 0 && fitting.Maximum > 0,
			"Fitting content did not receive a usable two-way scroll range.");
		var overflowing = SidebarScrollBehavior.Calculate(12, 1200, 900, 12);
		Assert(overflowing.Minimum == 0 && overflowing.Maximum >= 324,
			"Overflowing content cannot reach its final cards.");
		var extended = SidebarScrollBehavior.Calculate(300, 240, 900, 12, 40);
		Assert(extended.Minimum == fitting.Minimum - 40 && extended.Maximum == fitting.Maximum + 40,
			"The main column did not gain extra travel in both directions.");
	}

	private static void PrecisionScrollInput()
	{
		var whole = new SidebarScrollMotion();
		var partial = new SidebarScrollMotion();
		var batched = new SidebarScrollMotion();
		foreach (var motion in new[] { whole, partial, batched })
			motion.SetRange(new SidebarScrollRange(-1000, 1000), 1f);
		whole.AddWheel(-120, 50, animated: true);
		for (var index = 0; index < 4; index++)
			partial.AddWheel(-30, 50, animated: true);
		batched.AddWheel(-360, 50, animated: true);
		Assert(whole.Target == 50 && partial.Target == whole.Target && batched.Target == 150,
			"Precision deltas or batched wheel notches were lost.");
		Assert(whole.Position == 0, "An animated wheel notch jumped straight to its target.");
		whole.Advance(1.0 / 60);
		Assert(whole.Position > 0 && whole.Position < 50, "The first frame did not interpolate toward the target.");
		whole.AddWheel(120, 50, animated: false);
		Assert(whole.Position == 0 && !whole.IsMoving, "Disabling animation left residual movement.");
	}

	private static void ElasticScrollSettling()
	{
		foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
		{
			var spring = new SidebarScrollMotion();
			var range = new SidebarScrollRange(-200 * dpi, 200 * dpi);
			spring.SetRange(range, dpi);
			spring.AddWheel(-960, 50 * dpi, animated: true);
			spring.SnapToTarget();
			spring.AddWheel(-120, 50 * dpi, animated: true);
			var maximum = spring.Position;
			for (var frame = 0; frame < 120; frame++)
			{
				spring.Advance(1.0 / 120);
				maximum = Math.Max(maximum, spring.Position);
			}
			Assert(maximum > range.Maximum + dpi && maximum <= range.Maximum + spring.OverscrollLimit,
				"Edge feedback either disappeared or exceeded the elastic travel limit.");
			Assert(!spring.IsMoving && Math.Abs(spring.Position - range.Maximum) < 0.1f,
				"The edge spring did not return to rest within one second.");
			for (var input = 0; input < 1000; input++)
			{
				spring.AddWheel(input % 8 < 4 ? -120 : 120, 50 * dpi, animated: true);
				spring.Advance(input % 2 == 0 ? 1.0 / 60 : 1.0 / 120);
				Assert(float.IsFinite(spring.Position) && spring.Position >= range.Minimum - spring.OverscrollLimit &&
					spring.Position <= range.Maximum + spring.OverscrollLimit, "Rapid reversal escaped its finite travel limits.");
			}
			for (var frame = 0; frame < 180; frame++) spring.Advance(1.0 / 60);
			Assert(!spring.IsMoving && spring.Position >= range.Minimum && spring.Position <= range.Maximum,
				"Alternating wheel input left a drifting spring.");
			spring.AddWheel(120, 50 * dpi, animated: true);
			spring.Advance(2);
			Assert(!spring.IsMoving, "A suspended UI resumed with an unbounded animation backlog.");
			spring.SetRange(new SidebarScrollRange(-10 * dpi, 10 * dpi), dpi);
			Assert(Math.Abs(spring.Position) <= 10 * dpi && !spring.IsMoving,
				"Collapsing a long group left the resting column outside its new bounds.");
		}
	}

	private static void ScrollRendererLayers()
	{
		Exception? failure = null;
		var thread = new Thread(() =>
		{
			try
			{
				using var dispatcher = new DispatcherQueueHelper();
				dispatcher.EnsureDispatcherQueue();
				using var compositor = new Windows.UI.Composition.Compositor();
				using var root = compositor.CreateContainerVisual();
				using var owner = new Form();
				using var renderer = new CompositionStageRenderer(owner, compositor, root, 0.8, false, true);
				renderer.Resize(900, 1440, 1.25f);
				renderer.SetNotificationAreaCardEnabled(true);
				renderer.SetDesktopButtonEnabled(true);
				renderer.SetSidebarPinButtonEnabled(true);
				var movingBefore = renderer.GetInteractivePolygons(false).First();
				var fixedBefore = renderer.GetInteractivePolygons(true).SelectMany(polygon => polygon).ToArray();
				CardHitTarget? FindTarget(Func<CardHitTarget, bool> predicate)
				{
					for (var y = 1; y < 1440; y += 2)
						for (var x = 1; x < 260; x += 2)
						{
							var candidate = renderer.HitTest(new Point(x, y));
							if (candidate is not null && predicate(candidate)) return candidate;
						}
					return null;
				}
				var notificationTarget = FindTarget(target => target.IsNotificationAreaCard);
				var desktopTarget = FindTarget(target => target.IsDesktopButton);
				var sidebarPinTarget = FindTarget(target => target.IsSidebarPinButton);
				Assert(notificationTarget is not null && desktopTarget is not null && sidebarPinTarget is not null,
					"The integrated utility controls were not laid out below the cards.");
				Assert(!CardInteraction.IsWindowCard(sidebarPinTarget),
					"The whole-sidebar pin was incorrectly treated as an application card.");
				Assert(fixedBefore.Length == 0, "A utility card still occupied the independent fixed layer.");
				var center = new Point((int)movingBefore.Average(point => point.X), (int)movingBefore.Average(point => point.Y));
				var targetBefore = renderer.HitTest(center);
				var revision = renderer.LayoutRevision;
				renderer.Scroll(-120);
				Assert(renderer.ScrollTranslationY < 0 && renderer.LayoutRevision == revision,
					"A wheel notch caused a full card layout or failed to move the main layer.");
				var movedCenter = new Point(center.X, center.Y + (int)Math.Round(renderer.ScrollTranslationY));
				Assert(targetBefore is not null && renderer.HitTest(movedCenter)?.StageKey == targetBefore.StageKey,
					"Hit testing did not follow the visible scroll translation.");
				var notificationCenter = new Point(
					(int)notificationTarget!.Polygon.Average(point => point.X),
					(int)notificationTarget.Polygon.Average(point => point.Y) + (int)Math.Round(renderer.ScrollTranslationY));
				var desktopCenter = new Point(
					(int)desktopTarget!.Polygon.Average(point => point.X),
					(int)desktopTarget.Polygon.Average(point => point.Y) + (int)Math.Round(renderer.ScrollTranslationY));
				Assert(renderer.HitTest(notificationCenter)?.IsNotificationAreaCard == true,
					"The hidden-icons card did not move with the card column.");
				Assert(renderer.HitTest(desktopCenter)?.IsDesktopButton == true,
					"The Show Desktop button did not move with the card column.");
				var cameras = root.Children.ToArray();
				Assert(cameras.Count(camera => camera.Offset.Y == 0) == 1 &&
					cameras.Count(camera => camera.Offset.Y == renderer.ScrollTranslationY) == 1,
					"The main card camera did not carry the integrated controls while scrolling.");
				renderer.Activate(desktopTarget);
				Assert(renderer.ConsumeDesktopToggleRequest() && !renderer.ConsumeDesktopToggleRequest(),
					"The Show Desktop command was not consumed exactly once.");
				renderer.Activate(sidebarPinTarget);
				Assert(renderer.ConsumeSidebarPinRequest() && !renderer.ConsumeSidebarPinRequest(),
					"The whole-sidebar pin command was not consumed exactly once.");
				renderer.SetSidebarPinned(true);
				Assert(renderer.IsSidebarPinned, "The sidebar pin did not enter its fixed state.");
				renderer.SetSidebarPinned(false);
				Assert(!renderer.IsSidebarPinned, "The sidebar pin did not release its fixed state.");

				var window = new FakeWindow(321, "Feedback test", "test.exe");
				renderer.Synchronize([new PrototypeStageSnapshot("test", "Test", [window], DateTime.UtcNow)]);
				CardHitTarget? cardTarget = null;
				for (var y = 1; y < 1400 && cardTarget is null; y++)
				{
					var hit = renderer.HitTest(new Point(100, y));
					if (hit?.Window?.Handle == window.Handle) cardTarget = hit;
				}
				Assert(cardTarget is not null, "The feedback test could not find its card.");
				var originalHits = renderer.GetInteractivePolygons(false).SelectMany(polygon => polygon).ToArray();
				revision = renderer.LayoutRevision;
				foreach (var feedback in new[] { CardFeedback.Pressed, CardFeedback.None })
				{
					renderer.SetCardFeedback(cardTarget, feedback);
					Assert(renderer.LayoutRevision == revision && originalHits.SequenceEqual(renderer.GetInteractivePolygons(false).SelectMany(polygon => polygon)),
						"Click feedback changed card layout or hit geometry.");
				}
			}
			catch (Exception exception) { failure = exception; }
		}) { IsBackground = true };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Assert(thread.Join(TimeSpan.FromSeconds(15)), "Scroll renderer smoke test timed out.");
		if (failure is not null) throw new InvalidOperationException("Scroll renderer smoke test failed.", failure);
	}

	private static void IndependentStartupShortcut()
	{
		var shortcut = AutoStart.GetStartupShortcutPath();
		Assert(shortcut.EndsWith(AutoStart.StartupShortcutName, StringComparison.OrdinalIgnoreCase),
			"The independent startup shortcut did not use the stable Stage Manager name.");
		Assert(shortcut.Contains("Startup", StringComparison.OrdinalIgnoreCase) || shortcut.Contains("\u542f\u52a8", StringComparison.OrdinalIgnoreCase),
			"The independent launcher was not placed in the signed-in user's Startup folder.");
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

	private static void PrototypeClickPreservesPlacement()
	{
		var selected = new IntPtr(101);
		Assert(WindowClickBehavior.ShouldProcessPointerPress(1), "A normal card click was suppressed.");
		Assert(!WindowClickBehavior.ShouldProcessPointerPress(2), "A double-click still triggered a card command.");
		Assert(WindowClickBehavior.Decide(selected, new IntPtr(202), false, true) == WindowClickAction.ActivatePreservingPlacement,
			"A visible window should be activated without a size-changing ShowWindow command.");
		Assert(WindowClickBehavior.Decide(selected, selected, false, true) == WindowClickAction.Minimize,
			"Clicking the selected foreground window should still minimize it.");
		Assert(WindowClickBehavior.Decide(selected, selected, false, true, allowMinimize: false) == WindowClickAction.ActivatePreservingPlacement,
			"The explicit Bring to front command must not minimize the current window.");
		Assert(WindowClickBehavior.Decide(selected, selected, true, true) == WindowClickAction.RestoreAndActivate,
			"Only a currently minimized window should receive a restore command.");
		Assert(WindowClickBehavior.Decide(selected, IntPtr.Zero, false, false) == WindowClickAction.Ignore,
			"A destroyed window should not trigger another application.");
		Assert(WindowClickBehavior.Decide(IntPtr.Zero, IntPtr.Zero, false, true) == WindowClickAction.Ignore,
			"An empty window handle should not trigger activation.");
	}

	private static void CardClicksAreImmediate()
	{
		var gate = new CardClickGesture();
		var target = new CardHitTarget("test", new FakeWindow(123, "Test", "test.exe"), [], 0);
		var point = new Point(30, 100);
		var tolerance = new Size(8, 8);
		Assert(gate.Begin(target, WindowClickAction.Minimize, 1, point, 1000, 500, tolerance), "First press was rejected.");
		Assert(gate.Take() is null, "A press executed before release.");
		Assert(gate.Release(target, point, tolerance), "Release on the original card was rejected.");
		var action = gate.Take();
		Assert(action?.Target == target && action.Action == WindowClickAction.Minimize,
			"The click was delayed or lost its original target/action.");
		Assert(gate.Take() is null, "One release executed twice.");
		var other = target with { StageKey = "other", Window = new FakeWindow(456, "Other", "other.exe") };
		Assert(gate.Begin(other, WindowClickAction.ActivatePreservingPlacement, 1, point, 1050, 500, tolerance),
			"A fast click on a different card was suppressed.");
		gate.Release(other, point, tolerance);
		Assert(gate.Take()?.Target == other, "The second card did not respond immediately.");
	}

	private static void CardDoubleClickRunsOnce()
	{
		var target = new CardHitTarget("test", new FakeWindow(123, "Test", "test.exe"), [], 0);
		var point = new Point(30, 100);
		var tolerance = new Size(8, 8);
		foreach (var clicks in new[] { 1, 2 })
		{
			var gate = new CardClickGesture();
			Assert(gate.Begin(target, WindowClickAction.Minimize, 1, point, 1000, 500, tolerance), "First press was rejected.");
			Assert(gate.Release(target, point, tolerance), "A release on the original card was rejected.");
			Assert(gate.Take()?.Action == WindowClickAction.Minimize, "The first click did not execute immediately.");
			Assert(!gate.Begin(target, WindowClickAction.RestoreAndActivate, clicks, point, 1300, 500, tolerance),
				"The native or fallback second click was not suppressed.");
			Assert(!gate.Release(target, point, tolerance) && gate.Take() is null,
				"A double-click restored the window after minimizing it.");
			Assert(gate.Begin(target, WindowClickAction.RestoreAndActivate, 1, point, 1600, 500, tolerance),
				"A later intentional click was suppressed.");
			gate.Release(target, point, tolerance);
			Assert(gate.Take()?.Action == WindowClickAction.RestoreAndActivate,
				"The later click failed to restore the window.");
		}
		var movedGate = new CardClickGesture();
		movedGate.Begin(target, null, 1, point, 1000, 500, tolerance);
		movedGate.Release(target, point, tolerance);
		movedGate.Take();
		Assert(movedGate.Begin(target, null, 1, new Point(50, 100), 1100, 500, tolerance),
			"A separate click outside the double-click area was suppressed.");
	}

	private static void CancelledCardClicksDoNothing()
	{
		var gate = new CardClickGesture();
		var target = new CardHitTarget("test", new FakeWindow(123, "Test", "test.exe"), [], 0);
		var point = new Point(30, 100);
		var tolerance = new Size(8, 8);
		gate.Begin(target, null, 1, point, 1000, 500, tolerance);
		Assert(gate.IsDrag(new Point(50, 100), tolerance), "A drag was treated as a click.");
		Assert(!gate.Release(target, new Point(50, 100), tolerance) && gate.Take() is null,
			"Dragging off the press point still executed a click.");
		Assert(gate.Begin(target, null, 1, point, 1100, 500, tolerance),
			"An unsuccessful drag swallowed the next click.");
		Assert(!gate.Release(target with { StageKey = "other" }, point, tolerance) && gate.Take() is null,
			"Release on another card was accepted.");
		gate.Begin(target, null, 1, point, 1200, 500, tolerance);
		gate.Cancel();
		Assert(gate.Take() is null && !gate.Release(target, point, tolerance),
			"A cancelled context-menu/scroll/capture-loss gesture ran later.");
		Assert(gate.Begin(target, null, 1, point, 1300, 500, tolerance),
			"A cancelled press swallowed the next click.");
		Assert(gate.Take() is null, "Holding the mouse button executed a click.");
		gate.Release(target, point, tolerance);
		Assert(gate.Take()?.Target == target, "A held press was lost after a valid release.");
	}

	private static void CardHintsMatchActions()
	{
		foreach (var language in new[] { UiLanguage.English, UiLanguage.SimplifiedChinese })
		{
			var chinese = language == UiLanguage.SimplifiedChinese;
			var background = CardHoverText.Window(language, "Report", "App", false, false, true);
			var foreground = CardHoverText.Window(language, "Report", "App", false, true, true);
			var minimized = CardHoverText.Window(language, "Report", "App", true, false, false);
			Assert(background.Contains(chinese ? "单击置于前台" : "Click to bring forward"), "Background hint described the wrong action.");
			Assert(foreground.Contains(chinese ? "单击最小化" : "Click to minimize"), "Foreground hint omitted minimize.");
			Assert(minimized.Contains(chinese ? "单击恢复" : "Click to restore"), "Minimized hint omitted restore.");
			Assert(!background.Contains("Double-click") && !background.Contains("双击"), "A removed double-click action was advertised.");
			var pinned = CardHoverText.Group(language, "App", 3, true, true, false, false);
			Assert(pinned.Contains("FIXED") && !pinned.Contains(chinese ? "单击收起" : "Click to collapse"), "A pinned group offered an unavailable collapse action.");
		}
		var compact = CardHoverText.CompactTitle("  多行\n\t标题  " + new string('长', 200));
		Assert(!compact.Contains('\n') && compact.EndsWith('…') && compact.Length < 50, "A long multiline title overflowed the hint.");
	}

	private static void PointerButtonTransitions()
	{
		var wasDown = false;
		Assert(PointerButtonTransition.DidStart(unchecked((short)0x8000), ref wasDown),
			"The first button-down sample was not detected.");
		Assert(!PointerButtonTransition.DidStart(unchecked((short)0x8000), ref wasDown),
			"A held pointer button generated repeated clicks.");
		Assert(!PointerButtonTransition.DidStart(0, ref wasDown) && !wasDown,
			"The pointer button release was not recorded.");
		Assert(PointerButtonTransition.DidStart(1, ref wasDown),
			"A short click occurring between timer samples was not detected.");
	}

	private static void NotificationTrayIconLayout()
	{
		var sizes = Enumerable.Range(0, 16)
			.Select(index => index % 2 == 0 ? new Size(20, 20) : new Size(24, 24))
			.ToArray();
		var rectangles = NotificationTrayLayout.Arrange(new Size(156, 156), sizes);
		Assert(rectangles.Count == 16, "The bottom card did not retain all 16 supported icons.");
		for (var index = 0; index < rectangles.Count; index++)
		{
			Assert(rectangles[index].Size == sizes[index], "A notification icon was resized by the card layout.");
			Assert(new Rectangle(Point.Empty, new Size(156, 156)).Contains(rectangles[index]),
				"A notification icon escaped the bottom card.");
			for (var previous = 0; previous < index; previous++)
				Assert(!rectangles[index].IntersectsWith(rectangles[previous]), "Notification icons overlap.");
		}
		var incompleteGrid = NotificationTrayLayout.Arrange(new Size(156, 156), sizes.Take(14).ToArray());
		Assert(incompleteGrid[12].Left > incompleteGrid[8].Left,
			"The incomplete final icon row was not centered.");
		Assert(NotificationTrayLayout.Arrange(new Size(156, 156), []).Count == 0,
			"An empty notification area produced phantom icon slots.");
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
		Assert(MultiWindowCardInteraction.Decide(4, true, true, true) == MultiWindowCardClickAction.KeepExpanded,
			"Clicking a fixed primary card was allowed to collapse the fixed child list.");
		Assert(MultiWindowCardInteraction.Decide(4, false, true, true) == MultiWindowCardClickAction.SelectWindow,
			"Another application was allowed to replace the fixed expanded group.");
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
		Assert(!MultiWindowCardInteraction.ShouldCollapseOnPointerLeave(false, TimeSpan.FromMilliseconds(749)),
			"An unfixed card collapsed before the leave grace period elapsed.");
		Assert(MultiWindowCardInteraction.ShouldCollapseOnPointerLeave(false, TimeSpan.FromMilliseconds(750)),
			"An unfixed card did not collapse after the leave grace period.");
		Assert(!MultiWindowCardInteraction.ShouldCollapseOnPointerLeave(true, TimeSpan.FromSeconds(2)),
			"An explicitly pinned hover expansion was incorrectly marked for automatic collapse.");
		Assert(!MultiWindowCardInteraction.CanCollapseExpandedStage(true, false),
			"A normal sidebar hide was allowed to discard the fixed expanded group.");
		Assert(MultiWindowCardInteraction.CanCollapseExpandedStage(false, false),
			"A normal unfixed group could no longer collapse.");
		Assert(MultiWindowCardInteraction.CanCollapseExpandedStage(true, true),
			"A forced cleanup could not release a stale fixed group.");
		var groupCard = new CardHitTarget(
			"example-app",
			null,
			new[] { Vector2.Zero, Vector2.UnitX, Vector2.One },
			0,
			IsPrimaryCard: true);
		Assert(groupCard.Window is null && groupCard.IsPrimaryCard &&
			MultiWindowCardInteraction.ShouldExpandOnHover(2, false, groupCard.IsPrimaryCard),
			"A synthetic application group card was not eligible for hover expansion.");
		var pinOffset = Card3DGeometry.CreateExpandedPinOffset(
			new Vector2(156.8f, 97.6f),
			new Vector2(54f, 32f),
			1f);
		Assert(Math.Abs((pinOffset.X + 27f) - 78.4f) < 0.01f &&
			Math.Abs((pinOffset.Y + 16f) - 48.8f) < 0.01f,
			"The expanded-card pin was not centered on the primary card.");
		var overlay = Card3DGeometry.ProjectCardOverlay(
			new Vector3(12f, 40f, 8f),
			1.008f,
			Vector3.Zero,
			Vector3.One,
			-7.5f,
			new Vector2(156.8f, 97.6f),
			new Vector2(138f, 48.8f),
			pinOffset,
			new Vector2(54f, 32f),
			new Vector2(450f, 500f),
			1200f);
		Assert(overlay.Length == 4 && overlay.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y)),
			"The card-attached pin projection produced invalid hit geometry.");
		var parentCard = Card3DGeometry.ProjectCard(
			new Vector3(12f, 40f, 8f),
			1.008f,
			Vector3.Zero,
			Vector3.One,
			-7.5f,
			new Vector2(156.8f, 97.6f),
			new Vector2(138f, 48.8f),
			new Vector2(450f, 500f),
			1200f);
		var overlayCenter = new Vector2(overlay.Average(point => point.X), overlay.Average(point => point.Y));
		var cardCenter = new Vector2(parentCard.Average(point => point.X), parentCard.Average(point => point.Y));
		Assert(Vector2.Distance(overlayCenter, cardCenter) < 2f,
			"The pin's projected center drifted away from the tilted primary card center.");
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
				Assert(Descendants(form).Any(control => control.Text == "在卡片上方显示文件资源管理器按钮"),
					"The Explorer quick button option was not exposed in Chinese.");
				Assert(Descendants(form).Any(control => control.Text == "多窗口卡片展开时显示固定按钮"),
					"The expanded-card pin button option was not exposed in Chinese.");
				Assert(Descendants(form).Any(control => control.Text == "在底部卡片中显示 Windows 隐藏图标"),
					"The hidden-icons card toggle was not exposed in Chinese.");
				Assert(Descendants(form).Any(control => control.Text == "在窗口卡片下方显示一键最小化／恢复快捷卡"),
					"The Show Desktop button toggle was not exposed in Chinese.");
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
		Assert(!FocusEnhancedBehavior.UsesTransientSidebar(
			StageMode.Focus,
			maximizedOrFullScreen: true,
			exclusiveFullScreen: true,
			managedForeground: false),
			"An unmanaged full-screen shell overlay such as Alt+Tab hid the Focus sidebar.");
		Assert(FocusEnhancedBehavior.UsesTransientSidebar(StageMode.Coexist, maximizedOrFullScreen: true, exclusiveFullScreen: false),
			"Standard mode lost its maximized-window transient sidebar behavior.");
		Assert(FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, true, false, false),
			"Focus enhanced mode did not reserve the visible card column.");
		Assert(!FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, true, true, false),
			"Focus enhanced mode reserved space over an exclusive full-screen window.");
		Assert(!FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, true, false, true),
			"A temporary edge reveal unexpectedly rearranged the desktop work area.");
		Assert(FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, false, false, false, manuallyCollapsed: true),
			"A manually collapsed Focus sidebar released the blank reserved column.");
		Assert(FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, true, false, true, manuallyCollapsed: true),
			"Revealing a manually collapsed Focus sidebar changed the reserved work area.");
		Assert(!FocusEnhancedBehavior.ShouldReserveSidebar(StageMode.Focus, false, true, false, manuallyCollapsed: true),
			"A manually collapsed Focus sidebar covered an exclusive full-screen window.");
		Assert(!FocusEnhancedBehavior.ShouldIdleHide(StageMode.Focus, true),
			"Focus enhanced mode still allowed idle auto-hide.");
		Assert(!FocusEnhancedBehavior.CanHideSidebar(StageMode.Focus, exclusiveFullScreenActive: false),
			"A stale or delayed request could hide the persistent Focus sidebar outside full-screen.");
		Assert(FocusEnhancedBehavior.CanHideSidebar(StageMode.Focus, exclusiveFullScreenActive: true),
			"Focus mode could not temporarily hide while an exclusive full-screen window was active.");
		Assert(FocusEnhancedBehavior.CanHideSidebar(StageMode.Focus, exclusiveFullScreenActive: false, manualCollapse: true),
			"The Focus collapse arrow did not allow a deliberate manual hide.");
		Assert(FocusEnhancedBehavior.CanHideSidebar(StageMode.Coexist, exclusiveFullScreenActive: false),
			"Standard mode lost manual and idle sidebar hiding.");
		Assert(FocusEnhancedBehavior.ShouldPollHiddenSidebar(StageMode.Focus, pointerAtLeftEdge: false),
			"A hidden Focus sidebar stopped checking whether a long full-screen session had ended.");
		Assert(!FocusEnhancedBehavior.ShouldPollHiddenSidebar(StageMode.Coexist, pointerAtLeftEdge: false),
			"A normally hidden standard sidebar continued unnecessary foreground polling.");
		Assert(FocusEnhancedBehavior.ShouldPollHiddenSidebar(StageMode.Coexist, pointerAtLeftEdge: true),
			"Standard mode no longer woke when the pointer reached the left edge.");
		Assert(FocusEnhancedBehavior.ShouldShowCollapseButton(StageMode.Focus),
			"Focus enhanced mode did not show its manual collapse arrow.");
		Assert(!FocusEnhancedBehavior.ShouldShowSidebarPinButton(StageMode.Focus, false, false) &&
			FocusEnhancedBehavior.ShouldShowSidebarPinButton(StageMode.Focus, true, false) &&
			FocusEnhancedBehavior.ShouldShowSidebarPinButton(StageMode.Focus, true, true) &&
			!FocusEnhancedBehavior.ShouldShowSidebarPinButton(StageMode.Coexist, true, true),
			"The whole-sidebar pin was visible outside a revealed Focus sidebar.");
		Assert(!FocusEnhancedBehavior.ShouldApplyTransientHide(TransientSidebarAction.Hide, pinned: true, exclusiveFullScreenActive: false) &&
			FocusEnhancedBehavior.ShouldApplyTransientHide(TransientSidebarAction.Hide, pinned: false, exclusiveFullScreenActive: false) &&
			FocusEnhancedBehavior.ShouldApplyTransientHide(TransientSidebarAction.Hide, pinned: true, exclusiveFullScreenActive: true),
			"The sidebar pin did not hold an edge reveal or improperly blocked exclusive full-screen.");
		Assert(FocusEnhancedBehavior.ShouldShowCollapseButton(StageMode.Coexist),
			"Standard mode lost its manual sidebar-collapse button.");
		Assert(FocusEnhancedBehavior.ShouldIdleHide(StageMode.Coexist, true),
			"Standard mode no longer respected its idle auto-hide setting.");
		Assert(FocusEnhancedBehavior.ShouldRestoreAfterTransientSession(StageMode.Focus, false),
			"Focus mode failed to restore its sidebar after a long full-screen session lost the transient visibility marker.");
		Assert(!FocusEnhancedBehavior.ShouldRestoreAfterTransientSession(StageMode.Focus, false, manuallyCollapsed: true),
			"Leaving full screen overrode the user's manual Focus collapse.");
		Assert(!FocusEnhancedBehavior.ShouldRestoreAfterTransientSession(StageMode.Coexist, false),
			"Standard mode restored a sidebar that was intentionally hidden before the transient session.");
		var physicalDisplay = new Rectangle(0, 0, 2560, 1440);
		var appBarReducedWorkArea = new Rectangle(271, 0, 2289, 1440);
		Assert(FocusEnhancedBehavior.GetSidebarHostArea(StageMode.Focus, physicalDisplay, appBarReducedWorkArea) == physicalDisplay,
			"Focus mode anchored its own sidebar to the AppBar-reduced work area instead of the physical left edge.");
		Assert(FocusEnhancedBehavior.GetSidebarHostArea(StageMode.Coexist, physicalDisplay, appBarReducedWorkArea) == appBarReducedWorkArea,
			"Standard mode stopped respecting the ordinary desktop work area.");
		Assert(IgnoredApplicationPolicy.ShouldShowCard("demo", Array.Empty<string>()),
			"A normal application was incorrectly removed from the card catalog.");
		Assert(!IgnoredApplicationPolicy.ShouldShowCard("demo", new[] { "DEMO" }),
			"An ignored application still generated a card.");
	}

	private static void FocusFullScreenClassification()
	{
		Assert(!FullScreenService.IsExclusiveFullScreenCandidate(
			isWindow: true,
			isMinimized: false,
			isMaximized: true,
			matchesDisplayBounds: true),
			"A maximized window was still treated as exclusive full-screen.");
		Assert(FullScreenService.IsExclusiveFullScreenCandidate(
			isWindow: true,
			isMinimized: false,
			isMaximized: false,
			matchesDisplayBounds: true),
			"A non-maximized borderless full-screen window was not recognized.");
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
