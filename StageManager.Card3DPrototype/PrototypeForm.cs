using StageManager.Services;
using StageManager.Settings;
using StageManager.Native.Window;
using StageManager.Native.PInvoke;
using StageManager.Card3DPrototype.NotificationArea;
using StageManager.Card3DPrototype.QuickLaunch;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Card3DPrototype.Updates;
using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Windows.Forms;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace StageManager.Card3DPrototype;

internal sealed class PrototypeForm : Form
{
	internal event EventHandler? StartupReady;
	private readonly System.Windows.Forms.Timer _whatsNewTimer = new() { Interval = 2000 };
	private WhatsNewForm? _whatsNew;
	private readonly Dictionary<IntPtr, long> _permissionRefresh = new();
	private readonly HashSet<string> _launchesInFlight = new(StringComparer.OrdinalIgnoreCase);
	private const int WsExToolWindow = 0x00000080;
	private const int WsExNoActivate = 0x08000000;
	private const int WsExNoRedirectionBitmap = 0x00200000;
	private const int WmHotkey = 0x0312;
	private const uint ModNoRepeat = 0x4000;
	private const int ToggleSidebarHotkeyId = 0x4C41;
	private const int PreviousStageHotkeyId = 0x4C42;
	private const int NextStageHotkeyId = 0x4C43;
	private const int EdgeActivationWidth = 8;
	private const int VirtualKeyLeftButton = 0x01;
	private const int VirtualKeyRightButton = 0x02;
	private const int VirtualKeyMiddleButton = 0x04;
	private readonly DispatcherQueueHelper _dispatcherQueue = new();
	private readonly System.Windows.Forms.Timer _stageTimer = new() { Interval = 500 };
	private readonly System.Windows.Forms.Timer _pointerTimer = new() { Interval = 50 };
	private readonly System.Windows.Forms.Timer _hoverHintTimer = new() { Interval = 500 };
	private readonly System.Windows.Forms.Timer _regionCollapseTimer = new() { Interval = 260 };
	private readonly System.Windows.Forms.Timer _displayChangeTimer = new() { Interval = 250 };
	private readonly System.Windows.Forms.Timer _previewReleaseTimer = new() { Interval = 30000 };
	private readonly System.Windows.Forms.Timer _aiUsageRefreshTimer = new() { Interval = 300000 };
	private readonly System.Threading.Timer _hiddenEdgeTimer;
	private readonly ToolTip _toolTip = new() { InitialDelay = 250, ReshowDelay = 80, AutoPopDelay = 3000, ShowAlways = true };
	private readonly ContextMenuStrip _contextMenu = new() { AutoClose = true };
	private readonly ContextMenuStrip _cardContextMenu = new() { AutoClose = true };
	private readonly InitialWindowLayoutMemory _initialWindowLayouts = new();
	private readonly FocusAppBarReservation _focusAppBarReservation = new();
	private readonly NotificationAreaClient _notificationAreaClient = new();
	private readonly DesktopToggleService _desktopToggle = new();
	private readonly DesktopIconVisibilityService _desktopIcons = new();
	private readonly CancellationTokenSource _notificationAreaCancellation = new();
	private CancellationTokenSource _aiUsageCancellation = new();
	private readonly AiUsageService _aiUsageService;
	private readonly List<NotificationIconActivation> _notificationActivations = new();
	private readonly HashSet<int> _registeredHotkeys = new();
	private readonly CardClickGesture _cardClick = new();
	private SidebarMouseWheelHook? _mouseWheelHook;
	private Region? _mainCardRegion;
	private Region? _fixedCardRegion;
	private long _regionLayoutRevision = -1;
	private Screen _sidebarDisplay;
	private Rectangle _sidebarScreenBounds;
	private Compositor? _compositor;
	private DesktopWindowTarget? _target;
	private ContainerVisual? _root;
	private PrototypeStageCatalog? _catalog;
	private CompositionStageRenderer? _renderer;
	private NotifyIcon? _trayIcon;
	private string? _toolTipKey;
	private CardHitTarget? _hoverHintTarget;
	private AiUsageDetailsForm? _aiUsageDetails;
	private readonly AiUsagePopupController _aiUsagePopup = new();
	private bool _settingsOpen;
	private DateTime _lastSidebarInteractionUtc = DateTime.UtcNow;
	private DateTime _transientRevealUtc = DateTime.MinValue;
	private DateTime _nextFocusConstraintUtc = DateTime.MinValue;
	private DateTime _nextFocusAnchorCheckUtc = DateTime.MinValue;
	private volatile bool _sidebarVisible = true;
	private bool _focusManuallyCollapsed;
	private bool _transientSession;
	private bool _edgeRevealSession;
	private bool _sidebarWasVisibleBeforeTransientSession;
	private bool _transientOverlayRaised;
	private bool _demoteOverlayAfterHide;
	private volatile bool _closing;
	private int _hiddenEdgeUiRequestPending;
	private int _pendingMouseWheelDelta;
	private bool _appBarReapplyPending;
	private bool _leftPointerButtonDown;
	private bool _rightPointerButtonDown;
	private bool _middlePointerButtonDown;
	private bool _notificationAreaDragActive;
	private bool _notificationAreaRefreshPressActive;
	private CancellationTokenSource? _activationVerification;
	private int _notificationAreaDragStartY;
	private int _notificationAreaDragStartOffset;
	private bool IsFocusEnhanced => _catalog?.Settings.Current.StageMode == StageMode.Focus;
	private UiLanguage CurrentLanguage => _catalog?.Settings.Current.UiLanguage ?? UiLanguage.English;
	private string L(string english, string chinese) => UiText.Get(CurrentLanguage, english, chinese);

	public PrototypeForm()
	{
		_hiddenEdgeTimer = new(_ => PollHiddenEdgeFromBackground(), null, Timeout.Infinite, Timeout.Infinite);
		_aiUsageService = new AiUsageService();
		_aiUsageService.Updated += AiUsageService_Updated;
		Text = "Stage_Manager_Lai";
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		TopMost = false;
		_sidebarDisplay = SidebarDisplayPolicy.SelectLeftmost(Screen.AllScreens, screen => screen.Bounds);
		_sidebarScreenBounds = _sidebarDisplay.WorkingArea;
		Bounds = new Rectangle(_sidebarDisplay.WorkingArea.Left, _sidebarDisplay.WorkingArea.Top, Math.Min(900, _sidebarDisplay.WorkingArea.Width), _sidebarDisplay.WorkingArea.Height);
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

		var toggleItem = new ToolStripMenuItem("Show / Hide sidebar");
		toggleItem.Click += (_, _) => RunAfterContextMenuCloses(_contextMenu, ToggleSidebarVisibility);
		var settingsItem = new ToolStripMenuItem("Settings...");
		settingsItem.Click += (_, _) => RunAfterContextMenuCloses(_contextMenu, ShowSettings);
		var refreshItem = new ToolStripMenuItem("Refresh all previews now");
		refreshItem.Click += (_, _) => RunAfterContextMenuCloses(
			_contextMenu,
			() => _renderer?.RefreshAllPreviews());
		var exitItem = new ToolStripMenuItem("Exit Stage_Manager_Lai");
		exitItem.Click += (_, _) => RunAfterContextMenuCloses(_contextMenu, Close);
	_contextMenu.Items.Add(new ToolStripMenuItem(AppVersion.Label) { Enabled = false });
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(toggleItem);
		_contextMenu.Items.Add(refreshItem);
		_contextMenu.Items.Add(settingsItem);
		var updatesItem = new ToolStripMenuItem("Check for updates");
		updatesItem.Click += (_, _) => RunAfterContextMenuCloses(_contextMenu, () => { using var dialog = new UpdateForm(_catalog?.Settings.Current.UiLanguage ?? UiLanguage.English); dialog.ShowDialog(); });
		_contextMenu.Items.Add(updatesItem);
		_whatsNewTimer.Tick += (_, _) =>
		{
			if (_closing || _settingsOpen || _catalog is null || Screen.AllScreens.Any(screen => FullScreenService.IsExclusiveFullScreenOn(NativeMethods.GetForegroundWindow(), screen))) return;
			_whatsNewTimer.Stop();
			if (WhatsNewState.ShouldShow()) ShowWhatsNew();
		};
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(exitItem);
		_stageTimer.Tick += (_, _) =>
		{
			RefreshStages();
			RefreshDesktopIconState();
		};
		_pointerTimer.Tick += (_, _) => PollPointer();
		_aiUsageRefreshTimer.Tick += (_, _) => _ = RefreshAiUsageAsync(manual: false);
		_hoverHintTimer.Tick += (_, _) => ShowHoverHint();
		_regionCollapseTimer.Tick += (_, _) =>
		{
			_regionCollapseTimer.Stop();
			if (!_sidebarVisible)
				UpdateWindowRegion(false);
			if (_demoteOverlayAfterHide)
			{
				_demoteOverlayAfterHide = false;
				SetTransientOverlayRaised(false);
			}
		};
		_displayChangeTimer.Tick += (_, _) =>
		{
			_displayChangeTimer.Stop();
			_focusAppBarReservation.Remove();
			UpdateSidebarDisplay(force: true);
			UpdateFocusReservation(force: true);
		};
		_previewReleaseTimer.Tick += (_, _) =>
		{
			_previewReleaseTimer.Stop();
			if (_sidebarVisible)
				return;
			_renderer?.ReleasePreviewSurfaces();
			NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
		};
		SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
	}

	protected override bool ShowWithoutActivation => true;

	protected override CreateParams CreateParams
	{
		get
		{
			var parameters = base.CreateParams;
			parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap;
			return parameters;
		}
	}

	protected override async void OnShown(EventArgs e)
	{
		base.OnShown(e);
		try
		{
			_dispatcherQueue.EnsureDispatcherQueue();
			_compositor = new Compositor();
			_target = CompositionInterop.CreateDesktopWindowTarget(_compositor, Handle, false);
			_root = _compositor.CreateContainerVisual();
			_root.Size = new Vector2(ClientSize.Width, ClientSize.Height);
			_target.Root = _root;
			_catalog = new PrototypeStageCatalog();
			await _catalog.StartAsync();
			_catalog.Settings.SettingsChanged += Settings_SettingsChanged;
			_renderer = new CompositionStageRenderer(
				this,
				_compositor,
				_root,
				_catalog.Settings.Current.CardScale,
				_catalog.Settings.Current.AnimationsEnabled,
				_catalog.Settings.Current.LowMemoryRendering);
			_renderer.Resize(ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
			_renderer.ScrollPositionChanged += (_, _) => UpdateWindowRegion(_sidebarVisible);
			_mouseWheelHook = new SidebarMouseWheelHook();
			CreateTrayIcon();
			ApplyRuntimeSettings(updateStartup: false);
			RefreshStages();
			UpdateWindowRegion(true);
			_ = RefreshNotificationAreaAsync(initialDelay: true);
			_stageTimer.Start();
			_pointerTimer.Start();
			StartupReady?.Invoke(this, EventArgs.Empty);
			if (WhatsNewState.ShouldShow()) _whatsNewTimer.Start();
		}
		catch (Exception exception)
		{
			var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "3DRenderer");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "last-error.log"), exception.ToString());
			Close();
		}
	}

	protected override void OnPaintBackground(PaintEventArgs e)
	{
		// The Composition visual tree owns every visible pixel.
	}

	protected override void OnResize(EventArgs e)
	{
		base.OnResize(e);
		_renderer?.Resize(ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
		UpdateWindowRegion(_sidebarVisible);
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if (_renderer is null || !_sidebarVisible)
			return;
		if (_cardClick.Pending is not null)
		{
			if (_cardClick.IsDrag(e.Location, SystemInformation.DragSize))
				CancelPendingSingleCardClick();
			return;
		}
		if (_notificationAreaDragActive)
		{
			var offset = NotificationAreaVerticalDragBehavior.CalculateOffset(
				_notificationAreaDragStartOffset,
				e.Y - _notificationAreaDragStartY,
				DeviceDpi / 96f);
			_renderer.PreviewNotificationAreaVerticalOffset(offset);
			Cursor = Cursors.SizeNS;
			return;
		}
		if (_renderer.IsScrolling)
		{
			_lastSidebarInteractionUtc = DateTime.UtcNow;
			HideHoverHint();
			return;
		}
		var initialTarget = _renderer.HitTest(e.Location);
		if (initialTarget is null)
		{
			Cursor = Cursors.Default;
			HideHoverHint();
			return;
		}

		_lastSidebarInteractionUtc = DateTime.UtcNow;
		var wasExpanded = _renderer.HasExpandedStage;
		_renderer.UpdatePointer(e.Location);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		UpdateWindowRegion(true);

		var target = _renderer.HitTest(e.Location) ?? initialTarget;
		Cursor = target.IsNotificationAreaDragHandle
			? Cursors.SizeNS
			: Cursors.Hand;
		UpdateHoverExpandCandidate(target, e.Location);
		UpdateHoverHint(target);
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		_renderer?.StopEdgeScroll();
		HideHoverHint();
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		if (e.Button == MouseButtons.Right)
		{
			CancelPendingSingleCardClick();
			var target = _renderer?.HitTest(e.Location);
			if (target?.IsNotificationAreaCard == true)
				ShowNotificationAreaContextMenu(target);
			else if (target?.IsAiUsageCard == true)
				OpenAiUsageDetails(fromClick: true);
			else if (target is not null && !target.IsExplorerButton && target.QuickLaunchApp is null &&
				!target.IsExpandAllButton && !target.IsPinButton && !target.IsSidebarCollapseButton && !target.IsSidebarPinButton &&
				!target.IsDesktopButton && !target.IsDesktopIconsButton && target.PageDelta == 0)
				ShowCardContextMenu(target);
			else
				ShowOwnedContextMenu(_contextMenu);
			return;
		}
		if (e.Button != MouseButtons.Left || _renderer is null)
			return;
		var clickTarget = _renderer.HitTest(e.Location);
		if (!WindowClickBehavior.ShouldProcessPointerPress(e.Clicks))
		{
			CancelPendingSingleCardClick();
			_renderer.ReleasePointerPress();
			return;
		}
		if (CardInteraction.IsWindowCard(clickTarget))
		{
			WindowClickAction? action = clickTarget!.Window is { } selected
				? WindowClickBehavior.Decide(selected.Handle, NativeMethods.GetForegroundWindow(),
					NativeMethods.IsIconic(selected.Handle), NativeMethods.IsWindow(selected.Handle))
				: null;
			TraceCardClick($"DOWN selected={clickTarget!.Window?.Handle} app={clickTarget.Window?.ProcessName} foreground={NativeMethods.GetForegroundWindow()} self={Handle} action={action}");
			if (_cardClick.Begin(clickTarget, action, e.Clicks, e.Location, Environment.TickCount64,
				SystemInformation.DoubleClickTime, SystemInformation.DoubleClickSize))
			{
				Capture = true;
				_renderer.SetCardFeedback(clickTarget, CardFeedback.Pressed);
			}
			else
				CancelPendingSingleCardClick();
			return;
		}
		CancelPendingSingleCardClick();
		if (clickTarget?.IsNotificationAreaDragHandle == true)
		{
			_renderer.FinishScroll();
			_notificationAreaRefreshPressActive = false;
			_notificationAreaDragActive = true;
			_notificationAreaDragStartY = e.Y;
			_notificationAreaDragStartOffset = _renderer.NotificationAreaVerticalOffset;
			_renderer.SetNotificationAreaDragPressed(true);
			UseNotificationAreaDragRegion();
			Capture = true;
			Cursor = Cursors.SizeNS;
			return;
		}
		if (clickTarget?.IsNotificationAreaRefreshButton == true)
		{
			_notificationAreaRefreshPressActive = true;
			_renderer.SetNotificationAreaRefreshPressed(true);
			Capture = true;
			Cursor = Cursors.Hand;
			return;
		}
		ExecutePrimaryClick(clickTarget);
	}

	private void CancelPendingSingleCardClick()
	{
		var wasPressed = _cardClick.IsPressed;
		_cardClick.Cancel();
		_renderer?.SetCardFeedback(null, CardFeedback.None);
		if (wasPressed)
			Capture = false;
	}

	private void ExecutePrimaryClick(CardHitTarget? clickTarget, WindowClickAction? requestedAction = null)
	{
		if (_renderer is null || clickTarget is null)
			return;
		var wasExpanded = _renderer.HasExpandedStage;
		var window = _renderer.Activate(clickTarget);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		UpdateWindowRegion(true);
		if (_renderer.ConsumeExplorerLaunchRequest())
		{
			OpenFileExplorer();
			return;
		}
		if (_renderer.ConsumeQuickLaunchRequest() is { } quickLaunch)
		{
			OpenQuickLaunchApp(quickLaunch);
			return;
		}
		if (_renderer.ConsumeAiUsageClickRequest())
		{
			OpenAiUsageDetails(clickTarget, fromClick: true);
			return;
		}
		if (_renderer.ConsumeSidebarCollapseRequest())
		{
			_renderer.SetSidebarPinned(false);
			SetSidebarVisible(false, manualCollapse: true);
			return;
		}
		if (_renderer.ConsumeSidebarPinRequest())
		{
			var pin = !_renderer.IsSidebarPinned;
			_renderer.SetSidebarPinned(pin);
			if (pin)
			{
				_edgeRevealSession = false;
				SetTransientOverlayRaised(false);
				UpdateFocusReservation(force: true);
			}
			else if (_focusManuallyCollapsed)
			{
				_edgeRevealSession = true;
				_transientRevealUtc = DateTime.UtcNow;
			}
			return;
		}
		if (_renderer.ConsumeDesktopToggleRequest())
		{
			ToggleDesktop();
			return;
		}
		if (_renderer.ConsumeDesktopIconsToggleRequest())
		{
			ToggleDesktopIcons();
			return;
		}
		if (_renderer.ConsumeNotificationIconActivationRequest() is { } notificationIcon)
		{
			_ = ActivateNotificationIconAsync(notificationIcon);
			return;
		}
		if (_renderer.ConsumeNotificationAreaOpenRequest())
		{
			_ = _notificationAreaClient.ShowNativeOverflowAsync(_notificationAreaCancellation.Token);
			return;
		}
		if (window is null)
			return;
		ActivateSelectedWindow(window, allowMinimize: true, requestedAction);
		BeginInvoke(new Action(RefreshStages));
	}

	protected override void OnMouseWheel(MouseEventArgs e)
	{
		base.OnMouseWheel(e);
		// Accepted hook events return nonzero and never reach WM_MOUSEWHEEL.
		// This is the fallback for passed-through input; timestamp suppression
		// would lose distinct events during hook recovery or a UI heartbeat expiry.
		if (_renderer?.CanScrollAt(e.Location) != true || _notificationAreaDragActive)
			return;
		CancelPendingSingleCardClick();
		HideHoverHint();
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		_renderer.Scroll(e.Delta, e.Location);
	}

	private void ProcessPendingMouseWheel()
	{
		var delta = Interlocked.Exchange(ref _pendingMouseWheelDelta, 0);
		if (_closing || delta == 0 || !_sidebarVisible || _renderer is null) return;
		CancelPendingSingleCardClick();
		HideHoverHint();
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		_renderer.Scroll(delta, PointToClient(Cursor.Position));
	}

	protected override void WndProc(ref Message message)
	{
		if (message.Msg == SidebarMouseWheelHook.WheelMessage)
		{
			var delta = _mouseWheelHook?.TakePendingDelta() ?? 0;
			// The hook already accepted this input at its event-time coordinates.
			// Do not discard it just because the pointer moved before UI dispatch.
			if (delta != 0 && !_closing && _sidebarVisible && !_notificationAreaDragActive)
			{
				Interlocked.Add(ref _pendingMouseWheelDelta, delta);
				ProcessPendingMouseWheel();
			}
			message.Result = IntPtr.Zero;
			return;
		}
		const int wmNcHitTest = 0x0084;
		const int htClient = 1;
		const int htTransparent = -1;
		const int wmMouseActivate = 0x0021;
		const int maNoActivate = 3;
		if (_focusAppBarReservation.IsPositionChangedMessage(message.Msg, message.WParam))
		{
			QueueFocusReservationReapply();
			message.Result = IntPtr.Zero;
			return;
		}
		if (message.Msg == WmHotkey)
		{
			switch (message.WParam.ToInt32())
			{
				case ToggleSidebarHotkeyId:
					ToggleSidebarVisibility();
					break;
				case PreviousStageHotkeyId:
					ActivateRelativeStage(-1);
					break;
				case NextStageHotkeyId:
					ActivateRelativeStage(1);
					break;
			}
			message.Result = IntPtr.Zero;
			return;
		}
		if (message.Msg == wmMouseActivate)
		{
			message.Result = (IntPtr)maNoActivate;
			return;
		}
		if (message.Msg == wmNcHitTest && _renderer is not null)
		{
			var screenX = unchecked((short)(long)message.LParam);
			var screenY = unchecked((short)((long)message.LParam >> 16));
			var client = PointToClient(new Point(screenX, screenY));
			message.Result = (IntPtr)(_sidebarVisible && _renderer.HitTest(client) is not null ? htClient : htTransparent);
			return;
		}
		base.WndProc(ref message);
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_whatsNewTimer.Stop(); _whatsNewTimer.Dispose(); _whatsNew?.Dispose();
		_closing = true;
		_desktopToggle.TryRestore(out _);
		_mouseWheelHook?.Dispose();
		_mouseWheelHook = null;
		_notificationAreaCancellation.Cancel();
		CancelActivationVerification();
		_focusAppBarReservation.Dispose();
		UnregisterHotkeys();
		_stageTimer.Stop();
		_pointerTimer.Stop();
		_cardClick.Cancel();
		_hoverHintTimer.Stop();
		_hiddenEdgeTimer.Change(Timeout.Infinite, Timeout.Infinite);
		_regionCollapseTimer.Stop();
		_displayChangeTimer.Stop();
		_previewReleaseTimer.Stop();
		_aiUsageRefreshTimer.Stop();
		_stageTimer.Dispose();
		_pointerTimer.Dispose();
		_hoverHintTimer.Dispose();
		_hiddenEdgeTimer.Dispose();
		_regionCollapseTimer.Dispose();
		_displayChangeTimer.Dispose();
		_previewReleaseTimer.Dispose();
		_aiUsageRefreshTimer.Dispose();
		_toolTip.Dispose();
		_contextMenu.Dispose();
		_cardContextMenu.Dispose();
		_initialWindowLayouts.Clear();
		_notificationAreaClient.Dispose();
		_notificationAreaCancellation.Dispose();
		_aiUsageCancellation.Cancel();
		_aiUsageService.Updated -= AiUsageService_Updated;
		_aiUsageService.Dispose();
		if (_aiUsageDetails is { } aiDetails)
		{
			_aiUsageDetails = null;
			aiDetails.Close();
			aiDetails.Dispose();
		}
		_aiUsageCancellation.Dispose();
		SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
		if (_trayIcon is not null)
		{
			_trayIcon.Visible = false;
			_trayIcon.Icon?.Dispose();
			_trayIcon.Dispose();
			_trayIcon = null;
		}
		if (_catalog is not null)
			_catalog.Settings.SettingsChanged -= Settings_SettingsChanged;
		_catalog?.Dispose();
		_catalog = null;
		_renderer?.Dispose();
		_renderer = null;
		_target?.Dispose();
		_target = null;
		_root?.Dispose();
		_root = null;
		_compositor?.Dispose();
		_compositor = null;
		var oldRegion = Region;
		Region = null;
		oldRegion?.Dispose();
		_mainCardRegion?.Dispose();
		_fixedCardRegion?.Dispose();
		_dispatcherQueue.Dispose();
		base.OnFormClosed(e);
	}

	internal async Task WaitForBackgroundShutdownAsync()
	{
		await _notificationAreaClient.ShutdownAsync().ConfigureAwait(false);
		try { await _aiUsageService.WaitForShutdownAsync().ConfigureAwait(false); }
		catch (OperationCanceledException) { }
	}

	private void RefreshStages()
	{
		if (_closing || _catalog is null || _renderer is null)
			return;
		var stages = _catalog.GetStages();
		_initialWindowLayouts.Observe(stages
			.SelectMany(stage => stage.Windows)
			.DistinctBy(window => window.Handle));
		var previousRevision = _renderer.LayoutRevision;
		_renderer.Synchronize(stages);
		if (_sidebarVisible && previousRevision != _renderer.LayoutRevision)
		{
			UpdateWindowRegion(true);
			UpdateFocusReservation();
		}
	}

	private void Settings_SettingsChanged(object? sender, EventArgs e)
	{
		_catalog?.ReevaluateWindows();
		ApplyRuntimeSettings(updateStartup: true);
	}

	private void ApplyRuntimeSettings(bool updateStartup)
	{
		HideHoverHint();
		CancelPendingSingleCardClick();
		if (_catalog is null || _renderer is null)
			return;
		var settings = _catalog.Settings.Current;
		var aiConfigurationChanged = _aiUsageService.Configure(settings);
		_renderer.SetAnimationsEnabled(settings.AnimationsEnabled);
		_renderer.SetCardScale(settings.CardScale);
		_renderer.SetDockEdge(settings.SidebarEdge);
		UpdateSidebarDisplay(force: true);
		var layoutRegionChanged = _renderer.SetSidebarVerticalOffset(settings.SidebarVerticalOffset);
		if (_renderer.SetExplorerButtonEnabled(settings.ShowExplorerButton))
			layoutRegionChanged = true;
		if (_renderer.SetQuickLaunchApps(settings.QuickLaunchApps ?? QuickLaunchDefinition.LegacyDefaults(settings.ShowChromeQuickLaunch, settings.ShowEdgeQuickLaunch)))
			layoutRegionChanged = true;
		if (_renderer.SetAiUsageCardEnabled(settings.ShowAiUsageCard && settings.AiUsageEnabled))
			layoutRegionChanged = true;
		_renderer.SetAiUsageLanguage(settings.UiLanguage);
		_renderer.SetAiUsageSnapshots(_aiUsageService.Current);
		_aiUsageDetails?.SetLanguage(settings.UiLanguage);
		_aiUsageDetails?.SetSnapshots(_aiUsageService.Current);
		if (settings.ShowAiUsageCard && settings.AiUsageEnabled)
		{
			if (_aiUsageCancellation.IsCancellationRequested)
			{
				_aiUsageCancellation.Dispose();
				_aiUsageCancellation = new CancellationTokenSource();
			}
			if (!_aiUsageRefreshTimer.Enabled)
				_aiUsageRefreshTimer.Start();
			if (aiConfigurationChanged) _ = RefreshAiUsageAsync(manual: false);
		}
		else
		{
			_aiUsageRefreshTimer.Stop();
			if (!_aiUsageCancellation.IsCancellationRequested)
				_aiUsageCancellation.Cancel();
			_aiUsagePopup.Dismiss();
			CloseAiUsageDetails(immediate: true);
		}
		if (_renderer.SetPinButtonEnabled(settings.ShowExpandedPinButton))
			layoutRegionChanged = true;
		if (_renderer.SetCollapseButtonEnabled(FocusEnhancedBehavior.ShouldShowCollapseButton(settings.StageMode)))
			layoutRegionChanged = true;
		if (_renderer.SetSidebarPinButtonEnabled(FocusEnhancedBehavior.ShouldShowSidebarPinButton(
			settings.StageMode, _focusManuallyCollapsed, _renderer.IsSidebarPinned)))
			layoutRegionChanged = true;
		if (_renderer.SetNotificationAreaVerticalOffset(settings.NotificationAreaVerticalOffset))
			layoutRegionChanged = true;
		if (_renderer.SetNotificationAreaCardEnabled(settings.ShowNotificationAreaCard))
		{
			layoutRegionChanged = true;
			if (settings.ShowNotificationAreaCard)
				_ = RefreshNotificationAreaAsync(initialDelay: false);
		}
		if (_renderer.SetDesktopButtonEnabled(settings.ShowDesktopButton))
			layoutRegionChanged = true;
		if (_renderer.SetDesktopIconsButtonEnabled(settings.ShowDesktopIconsButton))
			layoutRegionChanged = true;
		_renderer.SetDesktopIconsVisible(_desktopIcons.IconsVisible);
		if (layoutRegionChanged)
			UpdateWindowRegion(_sidebarVisible);
		_renderer.SetPreviewPolicy(settings.PreviewRefreshMinutes, settings.PausePreviewRefreshWhenHidden);
		UiText.Apply(_contextMenu.Items, settings.UiLanguage);
		RegisterHotkeys();
		var wasFocusManuallyCollapsed = _focusManuallyCollapsed;
		if (settings.StageMode != StageMode.Focus)
		{
			_renderer.SetSidebarPinned(false);
			_focusManuallyCollapsed = false;
			_focusAppBarReservation.Remove();
		}
		var shouldRestoreHiddenSidebar = settings.StageMode == StageMode.Focus
			? !_focusManuallyCollapsed || _renderer.IsSidebarPinned
			: wasFocusManuallyCollapsed || !settings.IdleAutoHideEnabled;
		if (shouldRestoreHiddenSidebar && !_sidebarVisible)
		{
			_edgeRevealSession = false;
			SetSidebarVisible(true);
		}
		if (updateStartup)
		{
			try
			{
				StageManager.AutoStart.SetStartup(StageManager.AutoStart.DefaultAppName, settings.StartWithWindows);
			}
			catch (Exception exception)
			{
				MessageBox.Show(
					this,
					L($"Settings were saved, but the startup entry could not be updated.\n\n{exception.Message}", $"设置已保存，但无法更新开机启动项。\n\n{exception.Message}"),
					L("Startup setting", "开机启动设置"),
					MessageBoxButtons.OK,
					MessageBoxIcon.Warning);
			}
		}
		UpdateSidebarDisplay(force: true);
		RefreshStages();
		UpdateFocusReservation(force: true);
	}

	private void ShowSettings() => ShowSettings(false);
	private void ShowWhatsNew()
	{
		if (_whatsNew is { IsDisposed: false }) return;
		_whatsNew = new WhatsNewForm(_catalog?.Settings.Current.UiLanguage ?? UiLanguage.English);
		_whatsNew.OpenAiSettings += (_, _) => ShowSettings(aiUsage: true);
		_whatsNew.Show(this);
	}
	private void ShowSettings(bool aiUsage)
	{
		if (_catalog is null || _settingsOpen)
			return;
		SetSidebarVisible(true);
		_aiUsagePopup.Dismiss();
		CloseAiUsageDetails(immediate: true);
		CancelPendingSingleCardClick();
		HideHoverHint();
		_settingsOpen = true;
		PublishWheelRegion();
		try
		{
		using var dialog = new SettingsForm(_catalog.Settings.CloneCurrent(), _catalog.GetApplicationChoices(), _aiUsageService);
		if (aiUsage) dialog.Shown += (_, _) => dialog.ScrollToAiUsage();
		if (dialog.ShowDialog() == DialogResult.OK)
			_catalog.Settings.Apply(dialog.Draft);
		}
		finally { _settingsOpen = false; _lastSidebarInteractionUtc = DateTime.UtcNow; PublishWheelRegion(); }
	}

	private async Task RefreshAiUsageAsync(bool manual)
	{
		if (_closing || _catalog is null)
			return;
		try
		{
			_aiUsageDetails?.SetRefreshing(true);
			await _aiUsageService.RefreshAsync(_catalog.Settings.Current, manual, _aiUsageCancellation.Token);
		}
		catch (OperationCanceledException) { }
		catch (ObjectDisposedException) when (_closing) { }
		catch (Exception exception)
		{
			// Keep provider errors out of logs: exception messages may contain request details.
			System.Diagnostics.Debug.WriteLine($"AI usage refresh failed: {exception.GetType().Name}");
		}
		finally { if (!_closing) _aiUsageDetails?.SetRefreshing(false); }
	}

	private void AiUsageService_Updated(object? sender, EventArgs e)
	{
		if (_closing || !IsHandleCreated)
			return;
		try
		{
			BeginInvoke(() =>
			{
				if (_closing) return;
				_renderer?.SetAiUsageSnapshots(_aiUsageService.Current);
				_aiUsageDetails?.SetSnapshots(_aiUsageService.Current);
				UpdateWindowRegion(_sidebarVisible);
				UpdateFocusReservation();
			});
		}
		catch (InvalidOperationException) when (_closing) { }
	}

	private void OpenAiUsageDetails(CardHitTarget? target = null, bool fromClick = false)
	{
		if (_closing || _settingsOpen || _catalog is null || _renderer is null)
			return;
		if (fromClick)
		{
			_contextMenu.Close(ToolStripDropDownCloseReason.AppClicked);
			_cardContextMenu.Close(ToolStripDropDownCloseReason.AppClicked);
		}
		else if (_contextMenu.Visible || _cardContextMenu.Visible) return;
		var settings = _catalog.Settings.Current;
		if (!settings.ShowAiUsageCard || !settings.AiUsageEnabled)
			return;
		target ??= _renderer.HitTest(PointToClient(Cursor.Position));
		var selected = AiProviderAtPointer(target);
		if (fromClick)
		{
			_aiUsagePopup.Click();
			if (_aiUsageService.Current.FirstOrDefault(s => s.ProviderId == selected)?.NeedsConnection == true)
			{
				_aiUsagePopup.Dismiss(); CloseAiUsageDetails(immediate: true); ShowSettings(aiUsage: true); return;
			}
		}
		if (_aiUsageDetails is { IsDisposed: false } existing)
		{
			existing.KeepOpen(); existing.SelectProvider(selected);
			return;
		}

		var panel = new AiUsageDetailsForm(_aiUsageService, settings.UiLanguage);
		HideHoverHint();
		_ = NativeMethods.GetAsyncKeyState(0x1B); // Discard an Esc press that predates this popup.
		_aiUsageDetails = panel;
		panel.SetSnapshots(_aiUsageService.Current);
		panel.SelectProvider(selected);
		panel.CloseRequested += (_, _) => { _aiUsagePopup.Dismiss(); CloseAiUsageDetails(immediate: true); };
		panel.RefreshRequested += async (_, _) => await RefreshAiUsageAsync(manual: true);
		panel.SettingsRequested += (_, _) =>
		{
			_aiUsagePopup.Dismiss();
			CloseAiUsageDetails(immediate: true);
			ShowSettings(aiUsage: true);
		};
		panel.FormClosed += (_, _) =>
		{
			if (ReferenceEquals(_aiUsageDetails, panel)) _aiUsageDetails = null;
		};
		var cardBounds = target?.IsAiUsageCard == true && target.Polygon.Count > 0
			? RectangleF.FromLTRB(target.Polygon.Min(point => point.X), target.Polygon.Min(point => point.Y),
				target.Polygon.Max(point => point.X), target.Polygon.Max(point => point.Y))
			: new RectangleF(12, ClientSize.Height / 2f, 196, 46);
		if (target?.IsAiUsageCard == true) cardBounds.Offset(_renderer.ScrollTranslationX, _renderer.ScrollTranslationY);
		var topLeft = PointToScreen(Point.Round(new PointF(cardBounds.Left, cardBounds.Top)));
		var bottomRight = PointToScreen(Point.Round(new PointF(cardBounds.Right, cardBounds.Bottom)));
		var cardScreenBounds = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
		var workingArea = Screen.FromPoint(new Point(cardScreenBounds.Left, cardScreenBounds.Top)).WorkingArea;
		panel.FitToWorkingArea(workingArea, DeviceDpi / 96f);
		var x = cardScreenBounds.Right + 10;
		if (x + panel.Width > workingArea.Right)
			x = cardScreenBounds.Left - panel.Width - 10;
		var maxY = Math.Max(workingArea.Top, workingArea.Bottom - panel.Height);
		var maxX = Math.Max(workingArea.Left, workingArea.Right - panel.Width);
		var y = Math.Clamp(cardScreenBounds.Top - 8, workingArea.Top, maxY);
		if (SidebarDockLayout.IsHorizontal(DockEdge))
		{
			x = cardScreenBounds.Left;
			y = DockEdge == SidebarEdge.Top ? cardScreenBounds.Bottom + 10 : cardScreenBounds.Top - panel.Height - 10;
			y = Math.Clamp(y, workingArea.Top, maxY);
		}
		panel.Location = new Point(Math.Clamp(x, workingArea.Left, maxX), y);
		panel.Show(this);
		panel.BringToFront();
		_lastSidebarInteractionUtc = DateTime.UtcNow;
	}

	private string? AiProviderAtPointer(CardHitTarget? target)
	{
		var providers = _aiUsageService.Current;
		if (providers.Count == 0 || target?.IsAiUsageCard != true || target.Polygon.Count == 0) return null;
		var left = target.Polygon.Min(p => p.X) + (_renderer?.ScrollTranslationX ?? 0); var right = target.Polygon.Max(p => p.X) + (_renderer?.ScrollTranslationX ?? 0);
		var pointer = PointToClient(Cursor.Position);
		var top = target.Polygon.Min(p => p.Y) + (_renderer?.ScrollTranslationY ?? 0);
		var bottom = target.Polygon.Max(p => p.Y) + (_renderer?.ScrollTranslationY ?? 0);
		var frameHeight = bottom - top;
		top += frameHeight * (_renderer?.AiContentTopFraction ?? 0);
		bottom = top + frameHeight * (_renderer?.AiContentHeightFraction ?? 1);
		var column = Math.Clamp((int)((pointer.X - left) / Math.Max(1, right - left) * 2), 0, 1);
		var rowCount = AiUsagePresentation.HasAntigravity(providers) ? 3 : 2;
		var row = Math.Clamp((int)((pointer.Y - top) / Math.Max(1, bottom - top) * rowCount), 0, rowCount - 1);
		if (row == 2) return "antigravity";
		return AiUsagePresentation.ProviderOrder[row * 2 + column];
	}

	private void CloseAiUsageDetails(bool immediate = false)
	{
		if (_aiUsageDetails is not { IsDisposed: false } panel)
			return;
		if (!immediate)
		{
			panel.BeginFadeOut();
			return;
		}
		_aiUsageDetails = null;
		panel.Close();
		panel.Dispose();
	}

	private void UpdateAiUsagePointer(CardHitTarget? hit, Point screenPoint, DateTime nowUtc)
	{
		var onCard = hit?.IsAiUsageCard == true;
		var onPanel = _aiUsageDetails is { IsDisposed: false } panel && panel.IsPointerInside(screenPoint);
		var open = _aiUsageDetails is { IsDisposed: false };
		if (open && (NativeMethods.GetAsyncKeyState(0x1B) & 0x8001) != 0)
		{ _aiUsagePopup.Dismiss(); CloseAiUsageDetails(immediate: true); return; }
		switch (_aiUsagePopup.Update(onCard, onPanel, open, nowUtc))
		{
			case AiPopupAction.Open: OpenAiUsageDetails(hit); break;
			case AiPopupAction.KeepOpen:
				_aiUsageDetails?.KeepOpen();
				if (onCard) _aiUsageDetails?.SelectProvider(AiProviderAtPointer(hit));
				break;
			case AiPopupAction.FadeOut: CloseAiUsageDetails(); break;
		}
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		base.OnMouseLeave(e);
		HideHoverHint();
		if (_notificationAreaDragActive)
			return;
		_renderer?.ReleasePointerPress();
		Cursor = Cursors.Default;
	}

	protected override void OnMouseCaptureChanged(EventArgs e)
	{
		base.OnMouseCaptureChanged(e);
		if (!Capture && _cardClick.IsPressed)
			CancelPendingSingleCardClick();
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		base.OnMouseUp(e);
		if (e.Button == MouseButtons.Left && _cardClick.IsPressed)
		{
			var click = _cardClick.Release(_renderer?.HitTest(e.Location), e.Location, SystemInformation.DragSize)
				? _cardClick.Take()
				: null;
			Capture = false;
			_renderer?.SetCardFeedback(null, CardFeedback.None);
			if (click is not null && !_closing && _sidebarVisible)
				ExecutePrimaryClick(click.Target, click.Action);
			return;
		}
		if (e.Button == MouseButtons.Left && _notificationAreaDragActive)
		{
			_notificationAreaDragActive = false;
			Capture = false;
			_renderer?.SetNotificationAreaDragPressed(false);
			_renderer?.CommitNotificationAreaVerticalOffset();
			UpdateWindowRegion(true);
			PersistNotificationAreaVerticalOffset();
			Cursor = Cursors.SizeNS;
			return;
		}
		if (e.Button == MouseButtons.Left && _notificationAreaRefreshPressActive)
		{
			_notificationAreaRefreshPressActive = false;
			Capture = false;
			var releaseOverRefresh = _renderer?.HitTest(e.Location)?.IsNotificationAreaRefreshButton == true;
			_renderer?.SetNotificationAreaRefreshPressed(false);
			if (NotificationAreaControlBehavior.ShouldRefresh(
				refreshPressActive: true,
				releaseOverRefresh,
				dragActive: _notificationAreaDragActive))
			{
				_ = RefreshNotificationAreaAsync(initialDelay: false);
			}
			Cursor = releaseOverRefresh ? Cursors.Hand : Cursors.Default;
			return;
		}
		_renderer?.ReleasePointerPress();
	}

	private void UseNotificationAreaDragRegion()
	{
		if (_renderer is null || !IsHandleCreated)
			return;
		var replacement = new Region(_renderer.InteractionBounds);
		var previous = Region;
		Region = replacement;
		previous?.Dispose();
	}

	private void PersistNotificationAreaVerticalOffset()
	{
		if (_catalog is null || _renderer is null)
			return;
		var settings = _catalog.Settings.CloneCurrent();
		settings.NotificationAreaVerticalOffset = _renderer.NotificationAreaVerticalOffset;
		_catalog.Settings.Apply(settings);
	}

	private void OpenFileExplorer()
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "explorer.exe",
				UseShellExecute = true
			});
		}
		catch (Exception exception)
		{
			MessageBox.Show(
				this,
				L($"File Explorer could not be opened.\n\n{exception.Message}", $"无法打开文件资源管理器。\n\n{exception.Message}"),
				L("Open File Explorer", "打开文件资源管理器"),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning);
		}
	}

	private void ToggleDesktop()
	{
		if (_desktopToggle.TryToggle(out var error))
		{
			_renderer?.SetDesktopShown(_desktopToggle.IsDesktopShown);
			return;
		}
		_renderer?.SetDesktopShown(_desktopToggle.IsDesktopShown);
		_trayIcon?.ShowBalloonTip(
			3000,
			"Stage_Manager_Lai",
			L($"Windows could not switch the desktop. {error}", $"Windows 无法切换到桌面。{error}"),
			ToolTipIcon.Warning);
	}

	private void ToggleDesktopIcons()
	{
		if (_desktopIcons.TryToggle(out var error))
		{
			_renderer?.SetDesktopIconsVisible(_desktopIcons.IconsVisible);
			return;
		}
		_renderer?.SetDesktopIconsVisible(_desktopIcons.IconsVisible);
		_trayIcon?.ShowBalloonTip(
			3000,
			"Stage_Manager_Lai",
			L($"Windows could not toggle the desktop icons. {error}", $"Windows 无法切换桌面图标。{error}"),
			ToolTipIcon.Warning);
	}

	private void RefreshDesktopIconState()
	{
		if (_catalog?.Settings.Current.ShowDesktopIconsButton != true)
			return;
		if (_desktopIcons.Refresh())
			_renderer?.SetDesktopIconsVisible(_desktopIcons.IconsVisible);
	}

	private async void OpenQuickLaunchApp(QuickLaunchDefinition app)
	{
		if (!_launchesInFlight.Add(app.Id)) return;
		try
		{
			var result = await QuickLaunchCatalog.LaunchAsync(app, _notificationAreaCancellation.Token);
			if (_closing || result.Status is QuickLaunchStatus.Succeeded or QuickLaunchStatus.Cancelled) return;
			var message = result.Status switch
			{
				QuickLaunchStatus.Busy => L("Windows launch requests are still busy. Try again later.", "Windows 启动请求仍在处理中，请稍后再试。"),
				QuickLaunchStatus.TimedOut when result.ShellDispatched => L("Windows has not finished opening this app. It may still open later; avoid clicking repeatedly.", "Windows 尚未完成启动。应用仍可能稍后打开，请勿反复点击。"),
				QuickLaunchStatus.TimedOut => L("The launch request timed out before being sent to Windows.", "启动请求在发送给 Windows 前超时。"),
				_ => L($"{app.DisplayName} could not be opened. Check its path in Settings → Quick launch.", $"无法打开 {app.DisplayName}。请在设置 → 快捷应用中检查路径或重新添加。")
			};
			MessageBox.Show(this, message, L("Quick launch", "快捷启动"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
		catch (OperationCanceledException) { }
		finally { _launchesInFlight.Remove(app.Id); }
	}

	private void ShowCardContextMenu(CardHitTarget target)
	{
		if (_catalog is null || _renderer is null)
			return;
		while (_cardContextMenu.Items.Count > 0)
		{
			var item = _cardContextMenu.Items[0];
			_cardContextMenu.Items.RemoveAt(0);
			item.Dispose();
		}
		var stage = _catalog.GetStages().FirstOrDefault(snapshot =>
			string.Equals(snapshot.Key, target.StageKey, StringComparison.OrdinalIgnoreCase));
		var title = target.Window?.Title ?? stage?.Title ?? L("Application", "应用");
		_cardContextMenu.Items.Add(new ToolStripMenuItem(title) { Enabled = false });
		_cardContextMenu.Items.Add(new ToolStripSeparator());
		if (target.Window is { } window)
		{
			var activateItem = new ToolStripMenuItem(L("Bring this window to front", "将此窗口置于前台"));
			activateItem.Click += (_, _) => RunAfterContextMenuCloses(
				_cardContextMenu,
				() => ActivateSelectedWindow(window));
			_cardContextMenu.Items.Add(activateItem);

		}

		var actionWindows = ResolveCardActionWindows(target, stage);
		if (actionWindows.Count > 0)
		{
			var appliesToGroup = target.Window is null && actionWindows.Count > 1;
			_cardContextMenu.Items.Add(new ToolStripSeparator());

			var maximizeItem = new ToolStripMenuItem(appliesToGroup
				? L("Maximize all windows in this card", "最大化此卡片中的全部窗口")
				: L("Maximize this window", "最大化此窗口"))
			{
				Enabled = actionWindows.Any(candidate => NativeMethods.IsWindow(candidate.Handle))
			};
			maximizeItem.Click += (_, _) => RunAfterContextMenuCloses(
				_cardContextMenu,
				() => MaximizeWindows(actionWindows));
			_cardContextMenu.Items.Add(maximizeItem);

			var restoreLayoutItem = new ToolStripMenuItem(appliesToGroup
				? L("Restore all windows to initial size and position", "恢复全部窗口的初始大小和位置")
				: L("Restore initial size and position", "恢复初始大小和位置"))
			{
				Enabled = actionWindows.Any(_initialWindowLayouts.HasSnapshot)
			};
			restoreLayoutItem.Click += (_, _) => RunAfterContextMenuCloses(
				_cardContextMenu,
				() => RestoreInitialLayouts(actionWindows));
			_cardContextMenu.Items.Add(restoreLayoutItem);

			var centerItem = new ToolStripMenuItem(appliesToGroup
				? L("Move all windows to current display center", "将全部窗口移到当前显示器中央")
				: L("Move to current display center", "移到当前显示器中央"))
			{
				Enabled = actionWindows.Any(candidate => NativeMethods.IsWindow(candidate.Handle))
			};
			centerItem.Click += (_, _) => RunAfterContextMenuCloses(
				_cardContextMenu,
				() => CenterWindowsOnCurrentDisplay(actionWindows));
			_cardContextMenu.Items.Add(centerItem);

			var closeItem = new ToolStripMenuItem(appliesToGroup
				? L("Close all windows in this card", "关闭此卡片中的全部窗口")
				: L("Close this window", "关闭此窗口"))
			{
				Enabled = actionWindows.Any(candidate => NativeMethods.IsWindow(candidate.Handle)),
				ForeColor = Color.Firebrick
			};
			closeItem.Click += (_, _) => RunAfterContextMenuCloses(
				_cardContextMenu,
				() => CloseWindows(actionWindows));
			_cardContextMenu.Items.Add(closeItem);
			_cardContextMenu.Items.Add(new ToolStripSeparator());
		}

		var refreshItem = new ToolStripMenuItem(L("Refresh preview now", "立即刷新预览"));
		refreshItem.Click += (_, _) => RunAfterContextMenuCloses(
			_cardContextMenu,
			() => _renderer.RefreshStagePreviews(target.StageKey));
		_cardContextMenu.Items.Add(refreshItem);
		var processNames = (stage?.Windows ?? Array.Empty<StageManager.Native.Window.IWindow>())
			.Select(window => window.ProcessName)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (processNames.Length > 0)
		{
			_cardContextMenu.Items.Add(new ToolStripSeparator());
			var ignoreItem = new ToolStripMenuItem(processNames.Length == 1
				? L($"Ignore {processNames[0]}", $"忽略 {processNames[0]}")
				: L("Ignore applications in this card", "忽略此卡片中的应用"));
			ignoreItem.Click += (_, _) => RunAfterContextMenuCloses(
				_cardContextMenu,
				() =>
				{
					_catalog?.Settings.AddIgnoredProcesses(processNames);
					_trayIcon?.ShowBalloonTip(
						4500,
						L("Application hidden", "应用已隐藏"),
						L(
							"Restore it from Settings > Ignored applications by clearing its check box.",
							"如需恢复，请打开“设置 > 已忽略的应用”，取消勾选后保存。"),
						ToolTipIcon.Info);
				});
			_cardContextMenu.Items.Add(ignoreItem);
		}
		ShowOwnedContextMenu(_cardContextMenu);
	}

	private void ShowNotificationAreaContextMenu(CardHitTarget target)
	{
		while (_cardContextMenu.Items.Count > 0)
		{
			var item = _cardContextMenu.Items[0];
			_cardContextMenu.Items.RemoveAt(0);
			item.Dispose();
		}
		var title = string.IsNullOrWhiteSpace(target.NotificationIconName)
			? L("Windows hidden icons", "Windows 隐藏图标")
			: target.NotificationIconName;
		_cardContextMenu.Items.Add(new ToolStripMenuItem(title) { Enabled = false });
		_cardContextMenu.Items.Add(new ToolStripSeparator());
		var openItem = new ToolStripMenuItem(L("Open the Windows hidden-icons panel", "打开 Windows 隐藏图标面板"));
		openItem.Click += (_, _) => RunAfterContextMenuCloses(
			_cardContextMenu,
			() => _ = _notificationAreaClient.ShowNativeOverflowAsync(_notificationAreaCancellation.Token));
		_cardContextMenu.Items.Add(openItem);
		var refreshItem = new ToolStripMenuItem(L("Refresh hidden icons", "刷新隐藏图标"));
		refreshItem.Click += (_, _) => RunAfterContextMenuCloses(
			_cardContextMenu,
			() => _ = RefreshNotificationAreaAsync(initialDelay: false));
		_cardContextMenu.Items.Add(refreshItem);
		ShowOwnedContextMenu(_cardContextMenu);
	}

	private async Task RefreshNotificationAreaAsync(bool initialDelay)
	{
		try
		{
			if (initialDelay)
				await Task.Delay(1200, _notificationAreaCancellation.Token);
			if (_closing || _catalog?.Settings.Current.ShowNotificationAreaCard != true)
				return;
			var icons = await _notificationAreaClient.CaptureAsync(_notificationAreaCancellation.Token);
			try
			{
				if (_closing || _renderer is null)
					return;
				_notificationActivations.Clear();
				_notificationActivations.AddRange(icons.Select(icon => new NotificationIconActivation(icon.Ordinal, icon.Name)));
				_renderer.UpdateNotificationAreaIcons(icons);
				UpdateWindowRegion(_sidebarVisible);
			}
			finally
			{
				foreach (var icon in icons)
					icon.Dispose();
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task ActivateNotificationIconAsync(NotificationIconActivation activation)
	{
		try
		{
			var activated = await _notificationAreaClient.InvokeAsync(activation, _notificationAreaCancellation.Token);
			if (!activated && !_closing)
			{
				_trayIcon?.ShowBalloonTip(
					3000,
					L("Hidden icon changed", "隐藏图标已变化"),
					L("Refresh the bottom card and try again.", "请刷新底部卡片后重试。"),
					ToolTipIcon.Info);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void ShowOwnedContextMenu(ContextMenuStrip menu)
	{
		_aiUsagePopup.Dismiss();
		CloseAiUsageDetails(immediate: true);
		CancelPendingSingleCardClick();
		HideHoverHint();
		if (menu.Visible)
			menu.Close(ToolStripDropDownCloseReason.CloseCalled);
		PrimePointerButtonState();
		menu.Show(this, PointToClient(Cursor.Position));
	}

	private void PrimePointerButtonState()
	{
		_leftPointerButtonDown = IsPointerButtonDown(VirtualKeyLeftButton);
		_rightPointerButtonDown = IsPointerButtonDown(VirtualKeyRightButton);
		_middlePointerButtonDown = IsPointerButtonDown(VirtualKeyMiddleButton);
	}

	private void DismissContextMenusOnOutsideClick(Point screenPoint)
	{
		var leftStarted = PointerButtonStarted(VirtualKeyLeftButton, ref _leftPointerButtonDown);
		var rightStarted = PointerButtonStarted(VirtualKeyRightButton, ref _rightPointerButtonDown);
		var middleStarted = PointerButtonStarted(VirtualKeyMiddleButton, ref _middlePointerButtonDown);
		if (!leftStarted && !rightStarted && !middleStarted)
			return;

		CloseIfClickedOutside(_cardContextMenu, screenPoint);
		CloseIfClickedOutside(_contextMenu, screenPoint);
		if (_aiUsageDetails is { IsDisposed: false } aiPanel && !aiPanel.IsPointerInside(screenPoint) &&
			_renderer?.HitTest(PointToClient(screenPoint))?.IsAiUsageCard != true)
		{ _aiUsagePopup.Dismiss(); CloseAiUsageDetails(immediate: true); }
	}

	private static bool PointerButtonStarted(int virtualKey, ref bool wasDown)
		=> PointerButtonTransition.DidStart(NativeMethods.GetAsyncKeyState(virtualKey), ref wasDown);

	private static bool IsPointerButtonDown(int virtualKey) =>
		(NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

	private static void CloseIfClickedOutside(ContextMenuStrip menu, Point screenPoint)
	{
		if (menu.Visible && !menu.Bounds.Contains(screenPoint))
			menu.Close(ToolStripDropDownCloseReason.AppClicked);
	}

	private void RunAfterContextMenuCloses(ContextMenuStrip menu, Action action)
	{
		menu.Close(ToolStripDropDownCloseReason.ItemClicked);
		if (_closing || !IsHandleCreated)
			return;
		try
		{
			BeginInvoke(new Action(() =>
			{
				if (!_closing)
					action();
			}));
		}
		catch (InvalidOperationException) when (_closing)
		{
		}
	}

	private static IReadOnlyList<IWindow> ResolveCardActionWindows(
		CardHitTarget target,
		PrototypeStageSnapshot? stage)
	{
		if (target.Window is { } exactWindow)
			return [exactWindow];
		return stage?.Windows
			.Where(window => NativeMethods.IsWindow(window.Handle))
			.DistinctBy(window => window.Handle)
			.ToArray()
			?? [];
	}

	private void RestoreInitialLayouts(IReadOnlyList<IWindow> windows)
	{
		var restored = false;
		foreach (var window in windows)
			restored |= _initialWindowLayouts.TryRestore(window);
		if (restored)
			CompleteWindowCardAction(windows, activate: true);
	}

	private void MaximizeWindows(IReadOnlyList<IWindow> windows)
	{
		var maximized = false;
		foreach (var window in windows.Where(candidate => NativeMethods.IsWindow(candidate.Handle)))
		{
			NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwShowMaximized);
			maximized = true;
		}
		if (maximized)
			CompleteWindowCardAction(windows, activate: true);
	}

	private void CenterWindowsOnCurrentDisplay(IReadOnlyList<IWindow> windows)
	{
		var display = Screen.FromPoint(Cursor.Position);
		var moved = false;
		foreach (var window in windows)
		{
			var restoreMaximized = OffscreenWindowRecovery.ShouldRestoreMaximized(window);
			if (!OffscreenWindowRecovery.TryCenterOnDisplay(window, display, restoreMaximized))
				continue;
			moved = true;
			if (window.IsMinimized)
				NativeMethods.ShowWindowAsync(
					window.Handle,
					restoreMaximized ? NativeMethods.SwShowMaximized : NativeMethods.SwRestore);
		}
		if (moved)
			CompleteWindowCardAction(windows, activate: true);
	}

	private void CloseWindows(IReadOnlyList<IWindow> windows)
	{
		foreach (var window in windows.Where(candidate => NativeMethods.IsWindow(candidate.Handle)))
			window.Close();
		CompleteWindowCardAction(windows, activate: false);
	}

	private void CompleteWindowCardAction(IReadOnlyList<IWindow> windows, bool activate)
	{
		if (activate)
		{
			var activationTarget = windows.FirstOrDefault(window =>
				window.IsFocused && NativeMethods.IsWindow(window.Handle))
				?? windows.FirstOrDefault(window => NativeMethods.IsWindow(window.Handle));
			if (activationTarget is not null)
				ActivateSelectedWindow(activationTarget);
		}
		if (!_closing && IsHandleCreated)
			BeginInvoke(new Action(RefreshStages));
	}

	private bool CanShowHoverHint => !_closing && _sidebarVisible && _renderer is not null &&
		!_renderer.IsScrolling && !_notificationAreaDragActive && !_notificationAreaRefreshPressActive &&
		_cardClick.Pending is null && !IsPointerButtonDown(VirtualKeyLeftButton) &&
		!_contextMenu.Visible && !_cardContextMenu.Visible;

	private void HideHoverHint()
	{
		_hoverHintTimer.Stop();
		_hoverHintTarget = null;
		_toolTipKey = null;
		_toolTip.Hide(this);
	}

	private void UpdateHoverHint(CardHitTarget? target)
	{
		if (!CanShowHoverHint || target is null)
		{
			if (_toolTipKey is not null)
				HideHoverHint();
			return;
		}
		var key = CardInteraction.Key(target) + GetToolTipText(target);
		if (_toolTipKey == key)
			return;
		HideHoverHint();
		_toolTipKey = key;
		_hoverHintTarget = target;
		_hoverHintTimer.Start();
	}

	private void ShowHoverHint()
	{
		_hoverHintTimer.Stop();
		var current = _renderer?.HitTest(PointToClient(Cursor.Position));
		if (!CanShowHoverHint || !CardInteraction.SameTarget(current, _hoverHintTarget))
		{
			HideHoverHint();
			return;
		}
		var text = GetToolTipText(current!);
		if (_toolTipKey != CardInteraction.Key(current!) + text)
		{
			UpdateHoverHint(current);
			return;
		}
		var size = TextRenderer.MeasureText(text, SystemFonts.MessageBoxFont);
		var padding = (int)Math.Ceiling(16f * DeviceDpi / 96f);
		var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
		var translation = _renderer!.TargetTranslation(current!);
		var cardRight = current!.Polygon.Max(point => point.X) + translation.X;
		var cardTop = current.Polygon.Min(point => point.Y) + translation.Y;
		var anchor = PointToScreen(new Point((int)Math.Ceiling(cardRight) + padding, (int)cardTop));
		anchor.X = Math.Clamp(anchor.X, workArea.Left + padding, Math.Max(workArea.Left + padding, workArea.Right - size.Width - padding * 2));
		anchor.Y = Math.Clamp(anchor.Y, workArea.Top + padding, Math.Max(workArea.Top + padding, workArea.Bottom - size.Height - padding * 2));
		var client = PointToClient(anchor);
		_toolTip.Show(text, this, client.X, client.Y, 4500);
	}

	private string GetToolTipText(CardHitTarget target)
	{
		if (target.IsAiUsageCard)
			return string.Empty; // The usage popup replaces the application-window tooltip.
		if (target.IsNotificationAreaDragHandle)
			return L("Drag vertically to move this hidden-icons card", "上下拖动以移动这张隐藏图标卡片");
		if (target.IsNotificationAreaRefreshButton)
			return L("Refresh hidden icons now", "立即刷新隐藏图标");
		if (target.QuickLaunchApp is { } quickLaunchApp)
			return L($"Open {quickLaunchApp.DisplayName}", $"打开 {quickLaunchApp.DisplayName}");
		if (target.IsNotificationAreaCard)
			return string.IsNullOrWhiteSpace(target.NotificationIconName)
				? L("Windows hidden icons\nClick to open · Right-click for options", "Windows 隐藏图标\n单击打开 · 右键查看更多选项")
				: $"{CardHoverText.CompactTitle(target.NotificationIconName)}\n{L("Click to open this tray item", "单击打开此托盘项目")}";
		if (target.IsExplorerButton)
			return L("Open File Explorer", "打开文件资源管理器");
		if (target.IsExpandAllButton)
			return _renderer?.AreAllStagesExpanded == true
				? L("FIXED · Click to release all groups\nIndividually fixed groups stay open", "FIXED · 单击解除全部展开\n单独固定的分组仍保持展开")
				: L("FIX · Expand and keep all groups open", "FIX · 展开并固定全部多窗口分组");
		if (target.IsPinButton)
			return _renderer?.IsStagePinned(target.StageKey) == true
				? L("FIXED · Click to release", "FIXED · 点击解除固定")
				: L("FIX · Keep expanded", "FIX · 保持展开");
		if (target.IsSidebarPinButton)
			return _renderer?.IsSidebarPinned == true
				? L("Unpin sidebar · Allow automatic hiding", "取消侧栏固定 · 恢复自动隐藏")
				: L("Pin sidebar open", "图钉固定侧栏保持展开");
		if (target.IsSidebarCollapseButton)
			return L("Hide sidebar", "隐藏侧栏");
		if (target.IsDesktopButton)
			return _desktopToggle.IsDesktopShown
				? L("Restore the windows hidden by this button", "恢复由此按钮隐藏的窗口")
				: L("Show desktop · Minimize all windows", "显示桌面 · 最小化所有窗口");
		if (target.IsDesktopIconsButton)
			return _desktopIcons.IconsVisible
				? L("Desktop icons are visible · Click to hide", "桌面图标已显示 · 单击隐藏")
				: L("Desktop icons are hidden · Click to show", "桌面图标已隐藏 · 单击显示");
		if (target.PageDelta < 0)
			return L("Previous windows", "上一组窗口");
		if (target.PageDelta > 0)
			return L("Next windows", "下一组窗口");
		if (target.Window is { } window)
		{
			if (!_permissionRefresh.TryGetValue(window.Handle, out var queried) || Environment.TickCount64 - queried >= 5000)
			{
				if (_permissionRefresh.Count >= 512) _permissionRefresh.Clear();
				_permissionRefresh[window.Handle] = Environment.TickCount64;
				_ = Task.Run(() => { try { StageManager.Native.WindowAccessCache.Shared.Refresh(window.Handle); } catch { } });
			}
			var access = StageManager.Native.WindowAccess.Describe(StageManager.Native.WindowAccessCache.Shared.GetCached(window.Handle),
				CurrentLanguage == UiLanguage.SimplifiedChinese);
			var hint = CardHoverText.Window(CurrentLanguage, window.Title, window.ProcessName,
				NativeMethods.IsIconic(window.Handle), NativeMethods.GetForegroundWindow() == window.Handle,
				NativeMethods.IsZoomed(window.Handle));
			return string.IsNullOrEmpty(access.Badge) ? hint : hint + "\n" + access.Description;
		}

		var stage = _renderer?.GetStageSnapshot(target.StageKey);
		return stage is null
			? L("Application group", "应用组")
			: CardHoverText.Group(CurrentLanguage, stage.Title, stage.Windows.Count,
				_renderer!.IsStageExpanded(stage.Key), _renderer.IsStagePinned(stage.Key),
				_renderer.AreAllStagesExpanded, _renderer.CanExpandOnHover(target), _catalog?.Settings.Current.ShowExpandedPinButton == true);
	}

	private void RegisterHotkeys()
	{
		UnregisterHotkeys();
		if (_catalog is null || !_catalog.Settings.Current.HotkeysEnabled)
			return;
		var settings = _catalog.Settings.Current;
		RegisterHotkey(ToggleSidebarHotkeyId, settings.ToggleSidebarHotkey);
		RegisterHotkey(PreviousStageHotkeyId, settings.PreviousStageHotkey);
		RegisterHotkey(NextStageHotkeyId, settings.NextStageHotkey);
	}

	private void RegisterHotkey(int id, string gesture)
	{
		if (!HotkeyManager.TryParse(gesture, out var modifiers, out var virtualKey) ||
			!NativeMethods.RegisterHotKey(Handle, id, modifiers | ModNoRepeat, virtualKey))
		{
			_trayIcon?.ShowBalloonTip(
				3500,
				"Stage_Manager_Lai",
				L($"The shortcut {gesture} is already in use or invalid.", $"快捷键 {gesture} 已被占用或无效。"),
				ToolTipIcon.Warning);
			return;
		}
		_registeredHotkeys.Add(id);
	}

	private void UnregisterHotkeys()
	{
		foreach (var id in _registeredHotkeys)
			NativeMethods.UnregisterHotKey(Handle, id);
		_registeredHotkeys.Clear();
	}

	private void ToggleSidebarVisibility() => SetSidebarVisible(!_sidebarVisible);

	private void SetSidebarVisible(bool visible, bool manualCollapse = false)
	{
		if (!visible && IsFocusEnhanced)
		{
			var exclusiveFullScreenActive = UsesTransientSidebar(NativeMethods.GetForegroundWindow());
			if (!FocusEnhancedBehavior.CanHideSidebar(StageMode.Focus, exclusiveFullScreenActive,
				manualCollapse || _focusManuallyCollapsed))
				return;
		}
		if (!visible)
		{
			_aiUsagePopup.Dismiss(); CloseAiUsageDetails(immediate: true);
			CancelPendingSingleCardClick();
			HideHoverHint();
		}
		if (_renderer is null)
			return;
		if (visible && !(_edgeRevealSession && IsFocusEnhanced) && !_renderer.IsSidebarPinned)
			_focusManuallyCollapsed = false;
		else if (!visible && manualCollapse && IsFocusEnhanced)
			_focusManuallyCollapsed = true;
		_renderer.SetSidebarPinButtonEnabled(FocusEnhancedBehavior.ShouldShowSidebarPinButton(
			_catalog?.Settings.Current.StageMode ?? StageMode.Coexist,
			_focusManuallyCollapsed, _renderer.IsSidebarPinned));
		if (_sidebarVisible == visible)
		{
			UpdateFocusReservation();
			return;
		}
		_sidebarVisible = visible;
		UpdateFocusReservation();
		if (visible)
		{
			_hiddenEdgeTimer.Change(Timeout.Infinite, Timeout.Infinite);
			_pointerTimer.Start();
		}
		else
		{
			_edgeRevealSession = false;
			_pointerTimer.Stop();
			var largeWindowActive = UsesTransientSidebar(NativeMethods.GetForegroundWindow());
			_hiddenEdgeTimer.Change(
				0,
				SidebarIdleBehavior.GetHiddenEdgePollingInterval(largeWindowActive));
		}
		_regionCollapseTimer.Stop();
		if (visible)
		{
			_previewReleaseTimer.Stop();
			_demoteOverlayAfterHide = false;
			_lastSidebarInteractionUtc = DateTime.UtcNow;
			UpdateWindowRegion(true);
			NativeMethods.SetWindowPos(
				Handle,
				_transientOverlayRaised ? NativeMethods.HwndTopmost : NativeMethods.HwndTop,
				0,
				0,
				0,
				0,
				NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
			_renderer.SetSidebarVisible(true, animate: true);
			UpdateFocusReservation();
			return;
		}

		_renderer.CollapseExpandedStage();
		_renderer.SetSidebarVisible(false, animate: true);
		_previewReleaseTimer.Stop();
		_previewReleaseTimer.Start();
		var animate = _catalog?.Settings.Current.AnimationsEnabled == true;
		_demoteOverlayAfterHide = _transientOverlayRaised;
		if (animate)
			_regionCollapseTimer.Start();
		else
		{
			UpdateWindowRegion(false);
			if (_demoteOverlayAfterHide)
			{
				_demoteOverlayAfterHide = false;
				SetTransientOverlayRaised(false);
			}
		}
	}

	private void ActivateRelativeStage(int delta)
	{
		if (_catalog is null)
			return;
		var stages = _catalog.GetStages();
		if (stages.Count == 0)
			return;
		var foreground = NativeMethods.GetForegroundWindow();
		var currentIndex = -1;
		for (var index = 0; index < stages.Count; index++)
		{
			if (stages[index].Windows.Any(window => window.Handle == foreground))
			{
				currentIndex = index;
				break;
			}
		}
		var targetIndex = currentIndex < 0
			? (delta > 0 ? 0 : stages.Count - 1)
			: (currentIndex + delta + stages.Count) % stages.Count;
		var window = stages[targetIndex].Windows.FirstOrDefault(candidate => !candidate.IsMinimized)
			?? stages[targetIndex].Windows.FirstOrDefault();
		if (window is null)
			return;
		SetSidebarVisible(true);
		ActivateSelectedWindow(window);
	}

	private void ActivateSelectedWindow(IWindow window, bool allowMinimize = false,
		WindowClickAction? requestedAction = null)
	{
		var exists = NativeMethods.IsWindow(window.Handle);
		var action = requestedAction ?? WindowClickBehavior.Decide(window.Handle,
			NativeMethods.GetForegroundWindow(), exists && NativeMethods.IsIconic(window.Handle), exists, allowMinimize);
		if (!exists || action == WindowClickAction.Ignore) return;
		if (action == WindowClickAction.Minimize && !allowMinimize) return;
		CancelActivationVerification();
		_activationVerification = CancellationTokenSource.CreateLinkedTokenSource(_notificationAreaCancellation.Token);
		_ = ExecuteWindowActionAsync(window, action, _activationVerification);
	}

	private async Task ExecuteWindowActionAsync(IWindow window, WindowClickAction action, CancellationTokenSource source)
	{
		try
		{
			StageManager.Native.WindowOperationResult result;
			if (action == WindowClickAction.Minimize)
				result = await StageManager.Native.WindowActivation.MinimizeAsync(window.Handle, source.Token);
			else
			{
				_desktopToggle.MarkDesktopDismissed();
				_renderer?.SetDesktopShown(false);
				var trayFirst = window.ProcessName.Equals("VpnManager", StringComparison.OrdinalIgnoreCase) ||
					window.ProcessName.Equals("Clash for Windows", StringComparison.OrdinalIgnoreCase);
				result = await StageManager.Native.WindowActivation.ActivateAsync(window.Handle, source.Token,
					token => RestoreFromTrayAsync(window, token), trayFirst);
				if (result.Succeeded && !_closing) QueueFocusWindowPlacement(window);
			}
			TraceCardClick($"RESULT selected={window.Handle} action={action} outcome={result.Outcome} error={result.NativeError}");
			if (_closing || source.IsCancellationRequested) return;
			if (!result.Succeeded && result.Outcome is StageManager.Native.WindowOperationOutcome.NotConfirmed or
				StageManager.Native.WindowOperationOutcome.RequestRejected or StageManager.Native.WindowOperationOutcome.TimedOut)
			{
				Win32Helper.FlashTaskbar(window.Handle);
				var access = StageManager.Native.WindowAccess.Describe(StageManager.Native.WindowAccess.Query(window.Handle),
					CurrentLanguage == UiLanguage.SimplifiedChinese);
				var message = result.NativeError == 5 || !string.IsNullOrEmpty(access.Badge)
					? access.Description
					: L("Windows did not confirm the window action. Use its taskbar button or Alt+Tab.", "Windows 未确认窗口操作。请使用该应用的任务栏按钮或 Alt+Tab。");
				_trayIcon?.ShowBalloonTip(3500, "Stage_Manager_Lai", message, ToolTipIcon.Info);
			}
			RefreshStages();
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { TraceCardClick($"ACTION failed={exception.GetType().Name}"); }
		finally
		{
			if (ReferenceEquals(_activationVerification, source)) _activationVerification = null;
			source.Dispose();
		}
	}

	private async Task<bool> RestoreFromTrayAsync(IWindow window, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		if (window.ProcessName.Equals("VpnManager", StringComparison.OrdinalIgnoreCase))
		{
			try { using var signal = EventWaitHandle.OpenExisting("Local\\VpnManager.Activate"); if (signal.Set()) return true; }
			catch (WaitHandleCannotBeOpenedException) { }
			catch (UnauthorizedAccessException) { }
			return await _notificationAreaClient.ActivateApplicationAsync("VPN 管理器", token);
		}
		if (window.ProcessName.Equals("Clash for Windows", StringComparison.OrdinalIgnoreCase))
			return await _notificationAreaClient.ActivateApplicationAsync("Clash for Windows", token);
		return await TryActivateFromNotificationAreaAsync(window, token);
	}

	private void CancelActivationVerification()
	{
		_activationVerification?.Cancel();
		_activationVerification = null;
	}

	private static void TraceCardClick(string message)
	{
		try
		{
			var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "Logs");
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, "card-click.log");
			if (File.Exists(path) && new FileInfo(path).Length > 262144) File.WriteAllText(path, string.Empty);
			File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private async Task<bool> TryActivateFromNotificationAreaAsync(IWindow window, CancellationToken cancellationToken)
	{
		var candidates = new[] { window.Title, window.ProcessName, window.ProcessFileName };
		var activation = NotificationIconMatcher.FindBest(_notificationActivations, candidates);
		if (activation is null)
		{
			var icons = await _notificationAreaClient.CaptureAsync(cancellationToken);
			try
			{
				_notificationActivations.Clear();
				_notificationActivations.AddRange(icons.Select(icon => new NotificationIconActivation(icon.Ordinal, icon.Name)));
				activation = NotificationIconMatcher.FindBest(_notificationActivations, candidates);
			}
			finally { foreach (var icon in icons) icon.Dispose(); }
		}
		return activation is { } match && await _notificationAreaClient.InvokeAsync(match, cancellationToken);
	}

	private bool UsesTransientSidebar(IntPtr foregroundWindow)
	{
		var mode = _catalog?.Settings.Current.StageMode ?? StageMode.Coexist;
		if (mode == StageMode.Focus)
		{
			var managedForeground = _catalog?.IsManagedWindow(foregroundWindow) == true;
			var isExclusiveFullScreen = FullScreenService.IsExclusiveFullScreenOn(
				foregroundWindow,
				_sidebarDisplay);
			return FocusEnhancedBehavior.UsesTransientSidebar(
				mode,
				maximizedOrFullScreen: isExclusiveFullScreen,
				exclusiveFullScreen: isExclusiveFullScreen,
				managedForeground: managedForeground);
		}

		var isMaximizedOrFullScreen = FullScreenService.UsesTransientSidebarOn(
			foregroundWindow,
			_sidebarDisplay);
		return FocusEnhancedBehavior.UsesTransientSidebar(
			mode,
			isMaximizedOrFullScreen,
			exclusiveFullScreen: false);
	}

	private void QueueFocusWindowPlacement(StageManager.Native.Window.IWindow window)
	{
		if (!IsFocusEnhanced ||
			!_focusAppBarReservation.IsRegistered ||
			_transientSession ||
			_edgeRevealSession ||
			_closing)
		{
			return;
		}

		try
		{
			BeginInvoke(new Action(() =>
			{
				if (!IsFocusEnhanced ||
					!_focusAppBarReservation.IsRegistered ||
					_transientSession ||
					FullScreenService.IsExclusiveFullScreenOn(window.Handle, _sidebarDisplay))
				{
					return;
				}

				FocusWindowPlacement.TryKeepOutOfReservedColumn(
					window,
					_sidebarDisplay,
					_focusAppBarReservation.ReservedBounds, DockEdge);
			}));
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void KeepForegroundWindowOutOfFocusColumn(IntPtr foregroundWindow, DateTime nowUtc)
	{
		if (!IsFocusEnhanced ||
			!_focusAppBarReservation.IsRegistered ||
			_transientSession ||
			foregroundWindow == IntPtr.Zero ||
			foregroundWindow == Handle ||
			nowUtc < _nextFocusConstraintUtc)
		{
			return;
		}

		_nextFocusConstraintUtc = nowUtc.AddMilliseconds(150);
		if ((NativeMethods.GetAsyncKeyState(VirtualKeyLeftButton) & 0x8000) != 0 ||
			FullScreenService.IsExclusiveFullScreenOn(foregroundWindow, _sidebarDisplay))
		{
			return;
		}

		var window = _catalog?.GetStages()
			.SelectMany(stage => stage.Windows)
			.FirstOrDefault(candidate => candidate.Handle == foregroundWindow);
		if (window is null)
			return;

		FocusWindowPlacement.TryKeepOutOfReservedColumn(
			window,
			_sidebarDisplay,
			_focusAppBarReservation.ReservedBounds, DockEdge);
	}

	private void PollPointer()
	{
		if (_closing || _settingsOpen) _renderer?.StopEdgeScroll();
		if (_closing || _settingsOpen)
			return;
		PublishWheelRegion();
		var screenPoint = Cursor.Position;
		DismissContextMenusOnOutsideClick(screenPoint);
		if (_renderer is null || _catalog is null)
			return;
		_renderer.UpdateEdgeScroll(PointToClient(screenPoint), !_sidebarVisible || _notificationAreaDragActive ||
			Control.MouseButtons != MouseButtons.None || _cardClick.Pending is not null ||
			_contextMenu.Visible || _cardContextMenu.Visible || _aiUsageDetails is { IsDisposed: false });
		var nowUtc = DateTime.UtcNow;
		if (_aiUsageDetails is { IsDisposed: false } activePanel && activePanel.IsPointerInside(screenPoint))
		{
			UpdateAiUsagePointer(null, screenPoint, nowUtc);
			HideHoverHint();
			_lastSidebarInteractionUtc = nowUtc;
			return; // Never hover-expand or scroll a card underneath the popup.
		}
		var pointerAtLeftEdge = IsNearLeftEdge(screenPoint);
		var foreground = NativeMethods.GetForegroundWindow();
		var largeWindowActive = UsesTransientSidebar(foreground);
		UpdateTransientSession(largeWindowActive, nowUtc);
		EnsureFocusSidebarAnchored(largeWindowActive, nowUtc);
		if (!_sidebarVisible)
		{
			var aiPanelVisible = _aiUsageDetails is { IsDisposed: false } hiddenAiPanel && hiddenAiPanel.IsPointerInside(screenPoint);
			UpdateAiUsagePointer(null, screenPoint, nowUtc);
			if (aiPanelVisible)
			{
				_lastSidebarInteractionUtc = nowUtc;
				SetSidebarVisible(true);
				return;
			}
			if (pointerAtLeftEdge)
			{
				_edgeRevealSession = true;
				_transientRevealUtc = nowUtc;
				if (!_focusManuallyCollapsed)
					SetTransientOverlayRaised(true);
				SetSidebarVisible(true);
			}
			return;
		}

		var client = PointToClient(screenPoint);
		var previousLayoutRevision = _renderer.LayoutRevision;
		if (!_renderer.IsScrolling && !_notificationAreaDragActive && _cardClick.Pending is null)
			_renderer.PollPointer(client);
		var hit = _renderer.HitTest(client);
		UpdateAiUsagePointer(hit, screenPoint, nowUtc);
		if (!_renderer.IsScrolling && !_notificationAreaDragActive && _cardClick.Pending is null)
			UpdateHoverExpandCandidate(hit, client);
		UpdateHoverHint(hit);
		var pointerNearSidebar = _renderer.InteractionBounds.Contains(client);
		var pointerInsideAiPanel = _aiUsageDetails is { IsDisposed: false } aiPanel && aiPanel.IsPointerInside(screenPoint);
		pointerNearSidebar |= pointerInsideAiPanel;
		var pointerWithinTransientSidebar = hit is not null || pointerNearSidebar || pointerAtLeftEdge;
		if (hit is not null || pointerNearSidebar)
			_lastSidebarInteractionUtc = nowUtc;
		else
		{
			HideHoverHint();
		}
		if (previousLayoutRevision != _renderer.LayoutRevision)
			UpdateWindowRegion(true);

		var transientOverlayActive = largeWindowActive || _edgeRevealSession;
		var transientAction = TransientSidebarBehavior.Decide(
			transientOverlayActive,
			true,
			pointerAtLeftEdge,
			pointerWithinTransientSidebar,
			_transientRevealUtc,
			nowUtc);
		if (FocusEnhancedBehavior.ShouldApplyTransientHide(
			transientAction, _renderer.IsSidebarPinned, largeWindowActive))
		{
			_edgeRevealSession = false;
			SetSidebarVisible(false, manualCollapse: _focusManuallyCollapsed);
			return;
		}
		if (transientOverlayActive)
		{
			if (pointerWithinTransientSidebar && !_transientOverlayRaised && !_focusManuallyCollapsed)
			{
				_transientRevealUtc = nowUtc;
				SetTransientOverlayRaised(true);
			}
			return;
		}

		var settings = _catalog.Settings.Current;
		KeepForegroundWindowOutOfFocusColumn(foreground, nowUtc);
		if (SidebarIdleBehavior.ShouldHide(
			FocusEnhancedBehavior.ShouldIdleHide(settings.StageMode, settings.IdleAutoHideEnabled),
			settings.IdleAutoHideSeconds,
			_lastSidebarInteractionUtc,
			nowUtc))
		{
			SetSidebarVisible(false);
		}
	}

	private void UpdateHoverExpandCandidate(CardHitTarget? target, Point clientPoint)
	{
		if (_cardClick.Pending is not null || _renderer is null || target is null || !_renderer.CanExpandOnHover(target))
			return;
		if (!_sidebarVisible || !_renderer.TryExpandHoveredPrimaryCard(clientPoint, target.StageKey))
			return;
		NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0,
			NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		UpdateWindowRegion(true);
	}

	private void PollHiddenEdgeFromBackground()
	{
		if (_closing || _sidebarVisible || !NativeMethods.GetCursorPos(out var nativePoint))
			return;
		var screenPoint = new Point(nativePoint.X, nativePoint.Y);
		var pointerAtLeftEdge = IsNearLeftEdge(screenPoint);
		var mode = _catalog?.Settings.Current.StageMode ?? StageMode.Coexist;
		if (!FocusEnhancedBehavior.ShouldPollHiddenSidebar(mode, pointerAtLeftEdge) ||
			Interlocked.Exchange(ref _hiddenEdgeUiRequestPending, 1) != 0)
			return;

		try
		{
			BeginInvoke(new Action(() =>
			{
				try
				{
					if (!_closing && !_sidebarVisible)
						PollPointer();
				}
				finally
				{
					Interlocked.Exchange(ref _hiddenEdgeUiRequestPending, 0);
				}
			}));
		}
		catch (InvalidOperationException)
		{
			Interlocked.Exchange(ref _hiddenEdgeUiRequestPending, 0);
		}
	}

	private void UpdateTransientSession(bool largeWindowActive, DateTime nowUtc)
	{
		if (largeWindowActive)
		{
			if (_transientSession)
				return;
			_transientSession = true;
			_sidebarWasVisibleBeforeTransientSession = _sidebarVisible;
			_transientRevealUtc = nowUtc - TimeSpan.FromSeconds(1);
			UpdateFocusReservation();
			return;
		}

		if (!_transientSession)
			return;
		_transientSession = false;
		SetTransientOverlayRaised(false);
		var mode = _catalog?.Settings.Current.StageMode ?? StageMode.Coexist;
		if (FocusEnhancedBehavior.ShouldRestoreAfterTransientSession(mode, _sidebarWasVisibleBeforeTransientSession,
			_focusManuallyCollapsed && _renderer?.IsSidebarPinned != true))
		{
			_edgeRevealSession = false;
			if (!_sidebarVisible)
				SetSidebarVisible(true);
		}
		_sidebarWasVisibleBeforeTransientSession = false;
		UpdateFocusReservation();
	}

	private bool _topPanelOverlayRaised;
	private void UpdateTopPanelOverlay(bool includeCards)
	{
		var raised = includeCards && DockEdge == SidebarEdge.Top && _renderer is not null && !_renderer.TopPanelBounds.IsEmpty;
		if (_topPanelOverlayRaised == raised || !IsHandleCreated) return;
		_topPanelOverlayRaised = raised;
		NativeMethods.SetWindowPos(Handle, raised || _transientOverlayRaised ? NativeMethods.HwndTopmost : NativeMethods.HwndNotTopmost,
			0,0,0,0,NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
	}

	private void SetTransientOverlayRaised(bool raised)
	{
		if (_transientOverlayRaised == raised || !IsHandleCreated)
			return;
		_transientOverlayRaised = raised;
		NativeMethods.SetWindowPos(
			Handle,
			raised || _topPanelOverlayRaised ? NativeMethods.HwndTopmost : NativeMethods.HwndNotTopmost,
			0,
			0,
			0,
			0,
			NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
	}

	private SidebarEdge DockEdge => _catalog?.Settings.Current.SidebarEdge ?? SidebarEdge.Left;
	private bool IsNearLeftEdge(Point screenPoint)
	{
		return SidebarDockLayout.IsNearEdge(screenPoint, _sidebarScreenBounds, DockEdge, EdgeActivationWidth) ||
			SidebarDockLayout.IsNearEdge(screenPoint, _sidebarDisplay.Bounds, DockEdge, EdgeActivationWidth);
	}

	private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
	{
		if (_closing || !IsHandleCreated)
			return;
		try
		{
			BeginInvoke(new Action(() =>
			{
				_displayChangeTimer.Stop();
				_displayChangeTimer.Start();
			}));
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void UpdateFocusReservation(bool force = false)
	{
		if (_catalog is null || _renderer is null || !IsHandleCreated || _closing)
			return;

		var shouldReserve = FocusEnhancedBehavior.ShouldReserveSidebar(
			_catalog.Settings.Current.StageMode,
			_sidebarVisible,
			_transientSession,
			_edgeRevealSession,
			_focusManuallyCollapsed);
		var wasRegistered = _focusAppBarReservation.IsRegistered;
		var previousBounds = _focusAppBarReservation.ReservedBounds;
		if (!shouldReserve)
		{
			_focusAppBarReservation.Remove();
			if (wasRegistered)
				UpdateSidebarDisplay(force: true);
			return;
		}

		var reservedWidth = FocusEnhancedBehavior.CalculateReservedWidth(
			_renderer.SidebarThickness,
			DeviceDpi / 96f,
			SidebarDockLayout.IsHorizontal(DockEdge) ? _sidebarDisplay.Bounds.Height : _sidebarDisplay.Bounds.Width);
		if (!_focusAppBarReservation.SetReservation(Handle, _sidebarDisplay, reservedWidth, force, DockEdge))
			return;

		if (!wasRegistered || previousBounds != _focusAppBarReservation.ReservedBounds)
			UpdateSidebarDisplay(force: true);
	}

	private void QueueFocusReservationReapply()
	{
		if (_closing || _appBarReapplyPending || !IsHandleCreated)
			return;

		_appBarReapplyPending = true;
		try
		{
			BeginInvoke(new Action(() =>
			{
				try
				{
					UpdateFocusReservation(force: true);
				}
				finally
				{
					_appBarReapplyPending = false;
				}
			}));
		}
		catch (InvalidOperationException)
		{
			_appBarReapplyPending = false;
		}
	}

	private Rectangle GetSidebarArea(Screen display)
	{
		var mode = _catalog?.Settings.Current.StageMode ?? StageMode.Coexist;
		// Restore only our own reservation to the host area; keep the Windows taskbar clear.
		if (mode == StageMode.Focus && _focusAppBarReservation.IsRegistered &&
			string.Equals(display.DeviceName, _focusAppBarReservation.DisplayDeviceName, StringComparison.OrdinalIgnoreCase))
			return Rectangle.Intersect(display.Bounds, Rectangle.Union(display.WorkingArea, _focusAppBarReservation.ReservedBounds));
		return display.WorkingArea;
	}

	private void EnsureFocusSidebarAnchored(bool largeWindowActive, DateTime nowUtc)
	{
		if (!IsFocusEnhanced || largeWindowActive || nowUtc < _nextFocusAnchorCheckUtc)
			return;
		_nextFocusAnchorCheckUtc = nowUtc.AddSeconds(1);
		var expectedBounds = GetDockHostBounds(GetSidebarArea(_sidebarDisplay));
		if (Bounds != expectedBounds)
			UpdateSidebarDisplay(force: true);
		if (!_sidebarVisible && (!_focusManuallyCollapsed || _renderer?.IsSidebarPinned == true))
			SetSidebarVisible(true);
	}

	private void UpdateSidebarDisplay(bool force = false)
	{
		if (_closing || Screen.AllScreens.Length == 0)
			return;
		var selected = SidebarDisplayPolicy.SelectLeftmost(Screen.AllScreens, screen => screen.Bounds);
		var sidebarArea = GetSidebarArea(selected);
		if (string.Equals(selected.DeviceName, _sidebarDisplay.DeviceName, StringComparison.OrdinalIgnoreCase) &&
			sidebarArea == _sidebarScreenBounds &&
			!force)
			return;

		_sidebarDisplay = selected;
		_sidebarScreenBounds = sidebarArea;
		var targetBounds = GetDockHostBounds(sidebarArea);
		if (Bounds != targetBounds)
			Bounds = targetBounds;
		UpdateWindowRegion(_sidebarVisible);
	}

	private Rectangle GetDockHostBounds(Rectangle area)
	{
		var width = Math.Min(900, area.Width);
		return DockEdge switch
		{
			SidebarEdge.Left => new(area.Left, area.Top, width, area.Height),
			SidebarEdge.Right => new(area.Right - width, area.Top, width, area.Height),
			_ => area
		};
	}

	private void PublishWheelRegion()
	{
		var pointerInPopup = _aiUsageDetails is { IsDisposed: false } panel && panel.IsPointerInside(Cursor.Position);
		var bounds = !_settingsOpen && !pointerInPopup && _sidebarVisible && !_notificationAreaDragActive && _renderer is not null && IsHandleCreated
			? Rectangle.Intersect(_sidebarScreenBounds, RectangleToScreen(_renderer.InteractionBounds))
			: Rectangle.Empty;
		_mouseWheelHook?.PublishRegion(IsHandleCreated ? Handle : IntPtr.Zero, bounds);
	}

	private void UpdateWindowRegion(bool includeCards)
	{
		PublishWheelRegion();
		if (!IsHandleCreated)
			return;
		if (_notificationAreaDragActive)
			return;
		using var combined = new Region();
		combined.MakeEmpty();
		combined.Union(new Rectangle(0, 0, 1, 1));
		if (includeCards && _renderer is not null)
		{
			if (_regionLayoutRevision != _renderer.LayoutRevision || _mainCardRegion is null || _fixedCardRegion is null)
			{
				var mainRegion = BuildCardRegion(_renderer.GetInteractivePolygons(fixedLayer: false));
				var fixedRegion = BuildCardRegion(_renderer.GetInteractivePolygons(fixedLayer: true), pad: _renderer.TopPanelBounds.IsEmpty);
				if (!_renderer.TopPanelBounds.IsEmpty) fixedRegion.Intersect(_renderer.TopPanelBounds);
				_mainCardRegion?.Dispose();
				_fixedCardRegion?.Dispose();
				_mainCardRegion = mainRegion;
				_fixedCardRegion = fixedRegion;
				_regionLayoutRevision = _renderer.LayoutRevision;
			}
			using var movingRegion = _mainCardRegion.Clone();
			movingRegion.Translate(_renderer.ScrollTranslationX, _renderer.ScrollTranslationY);
			combined.Union(movingRegion);
			combined.Union(_fixedCardRegion);
		}

		var replacement = combined.Clone();
		var previous = Region;
		Region = replacement;
		previous?.Dispose();
		UpdateTopPanelOverlay(includeCards);
	}

	private Region BuildCardRegion(IReadOnlyList<PointF[]> polygons, bool pad = true)
	{
		var result = new Region();
		result.MakeEmpty();
		var margin = Math.Max(12f, 16f * DeviceDpi / 96f);
		using var marginPen = new Pen(Color.Black, margin * 2f) { LineJoin = LineJoin.Round };
		foreach (var polygon in polygons)
		{
			if (polygon.Length < 3)
				continue;
			using var path = new GraphicsPath();
			path.AddPolygon(polygon);
			result.Union(path);
			if (pad) { path.Widen(marginPen); result.Union(path); }
		}
		return result;
	}

	private void CreateTrayIcon()
	{
		var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
		_trayIcon = new NotifyIcon
		{
			Text = AppVersion.Label,
			Icon = icon,
			ContextMenuStrip = _contextMenu,
			Visible = true
		};
		_trayIcon.MouseClick += (_, eventArgs) =>
		{
			if (eventArgs.Button == MouseButtons.Left)
				ToggleSidebarVisibility();
		};
	}
}
