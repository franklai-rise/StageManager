using StageManager.Services;
using StageManager.Settings;
using StageManager.Native.Window;
using StageManager.Native.PInvoke;
using StageManager.Card3DPrototype.NotificationArea;
using StageManager.Card3DPrototype.QuickLaunch;
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
	private int _mouseWheelUiRequestPending;
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
	_contextMenu.Items.Add(new ToolStripMenuItem("Stage_Manager_Lai v4.4.16") { Enabled = false });
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(toggleItem);
		_contextMenu.Items.Add(refreshItem);
		_contextMenu.Items.Add(settingsItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(exitItem);
		_stageTimer.Tick += (_, _) =>
		{
			RefreshStages();
			RefreshDesktopIconState();
		};
		_pointerTimer.Tick += (_, _) => PollPointer();
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
			_mouseWheelHook = new SidebarMouseWheelHook(HandleGlobalMouseWheel);
			CreateTrayIcon();
			ApplyRuntimeSettings(updateStartup: false);
			RefreshStages();
			UpdateWindowRegion(true);
			_ = RefreshNotificationAreaAsync(initialDelay: true);
			_stageTimer.Start();
			_pointerTimer.Start();
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
		HideHoverHint();
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		if (e.Button == MouseButtons.Right)
		{
			CancelPendingSingleCardClick();
			var target = _renderer?.HitTest(e.Location);
			if (target?.IsNotificationAreaCard == true)
				ShowNotificationAreaContextMenu(target);
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
		if (wasExpanded != _renderer.HasExpandedStage)
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
		// The global hook handles wheel input over cards and transparent gaps.
		// Keep this fallback for systems where Windows declined the hook.
		if (_mouseWheelHook?.IsActive == true &&
			_mouseWheelHook.ReceivedWheelRecently(TimeSpan.FromMilliseconds(150)))
			return;
		if (_renderer?.CanScrollAt(e.Location) != true || _notificationAreaDragActive)
			return;
		CancelPendingSingleCardClick();
		HideHoverHint();
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		_renderer.Scroll(e.Delta);
	}

	private bool HandleGlobalMouseWheel(Point screenPoint, int delta)
	{
		if (_closing || !_sidebarVisible || _renderer is null || !IsHandleCreated || _notificationAreaDragActive ||
			!_sidebarScreenBounds.Contains(screenPoint))
			return false;
		var clientPoint = PointToClient(screenPoint);
		if (!_renderer.CanScrollAt(clientPoint))
			return false;
		Interlocked.Add(ref _pendingMouseWheelDelta, delta);
		if (Interlocked.Exchange(ref _mouseWheelUiRequestPending, 1) == 0)
		{
			try
			{
				BeginInvoke(new Action(ProcessPendingMouseWheel));
			}
			catch (InvalidOperationException)
			{
				Interlocked.Exchange(ref _mouseWheelUiRequestPending, 0);
				Interlocked.Exchange(ref _pendingMouseWheelDelta, 0);
				return false;
			}
		}
		return true;
	}

	private void ProcessPendingMouseWheel()
	{
		while (!_closing)
		{
			var delta = Interlocked.Exchange(ref _pendingMouseWheelDelta, 0);
			if (delta != 0 && _sidebarVisible && _renderer is not null)
			{
				CancelPendingSingleCardClick();
				HideHoverHint();
				_lastSidebarInteractionUtc = DateTime.UtcNow;
				_renderer.Scroll(delta);
			}
			Interlocked.Exchange(ref _mouseWheelUiRequestPending, 0);
			if (Volatile.Read(ref _pendingMouseWheelDelta) == 0 ||
				Interlocked.Exchange(ref _mouseWheelUiRequestPending, 1) != 0)
				break;
		}
	}

	protected override void WndProc(ref Message message)
	{
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
		_stageTimer.Dispose();
		_pointerTimer.Dispose();
		_hoverHintTimer.Dispose();
		_hiddenEdgeTimer.Dispose();
		_regionCollapseTimer.Dispose();
		_displayChangeTimer.Dispose();
		_previewReleaseTimer.Dispose();
		_toolTip.Dispose();
		_contextMenu.Dispose();
		_cardContextMenu.Dispose();
		_initialWindowLayouts.Clear();
		_notificationAreaClient.Dispose();
		_notificationAreaCancellation.Dispose();
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

	internal Task WaitForBackgroundShutdownAsync() => _notificationAreaClient.ShutdownAsync();

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
		_renderer.SetAnimationsEnabled(settings.AnimationsEnabled);
		_renderer.SetCardScale(settings.CardScale);
		var layoutRegionChanged = _renderer.SetSidebarVerticalOffset(settings.SidebarVerticalOffset);
		if (_renderer.SetExplorerButtonEnabled(settings.ShowExplorerButton))
			layoutRegionChanged = true;
		if (_renderer.SetQuickLaunchButtonsEnabled(settings.ShowChromeQuickLaunch, settings.ShowEdgeQuickLaunch))
			layoutRegionChanged = true;
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

	private void ShowSettings()
	{
		if (_catalog is null)
			return;
		SetSidebarVisible(true);
		using var dialog = new SettingsForm(_catalog.Settings.CloneCurrent(), _catalog.GetApplicationChoices());
		if (dialog.ShowDialog() == DialogResult.OK)
			_catalog.Settings.Apply(dialog.Draft);
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
		var width = Math.Min(ClientSize.Width, (int)Math.Ceiling(_renderer.SidebarInteractionWidth + 16f * DeviceDpi / 96f));
		var replacement = new Region(new Rectangle(0, 0, Math.Max(1, width), Math.Max(1, ClientSize.Height)));
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

	private void OpenQuickLaunchApp(QuickLaunchApp app)
	{
		if (QuickLaunchAppResolver.Launch(app))
			return;
		MessageBox.Show(
			this,
			L($"{QuickLaunchAppResolver.DisplayName(app)} could not be opened.", $"无法打开 {QuickLaunchAppResolver.DisplayName(app)}。"),
			L("Quick launch", "快捷启动"),
			MessageBoxButtons.OK,
			MessageBoxIcon.Warning);
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
		var cardRight = current!.Polygon.Max(point => point.X);
		var cardTop = current.Polygon.Min(point => point.Y) + _renderer!.ScrollTranslationY;
		var anchor = PointToScreen(new Point((int)Math.Ceiling(cardRight) + padding, (int)cardTop));
		anchor.X = Math.Clamp(anchor.X, workArea.Left + padding, Math.Max(workArea.Left + padding, workArea.Right - size.Width - padding * 2));
		anchor.Y = Math.Clamp(anchor.Y, workArea.Top + padding, Math.Max(workArea.Top + padding, workArea.Bottom - size.Height - padding * 2));
		var client = PointToClient(anchor);
		_toolTip.Show(text, this, client.X, client.Y, 4500);
	}

	private string GetToolTipText(CardHitTarget target)
	{
		if (target.IsNotificationAreaDragHandle)
			return L("Drag vertically to move this hidden-icons card", "上下拖动以移动这张隐藏图标卡片");
		if (target.IsNotificationAreaRefreshButton)
			return L("Refresh hidden icons now", "立即刷新隐藏图标");
		if (target.QuickLaunchApp is { } quickLaunchApp)
			return L($"Open {QuickLaunchAppResolver.DisplayName(quickLaunchApp)}", $"打开 {QuickLaunchAppResolver.DisplayName(quickLaunchApp)}");
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
			return _renderer?.IsExpandedStagePinned == true
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
			return CardHoverText.Window(CurrentLanguage, window.Title, window.ProcessName,
				NativeMethods.IsIconic(window.Handle), NativeMethods.GetForegroundWindow() == window.Handle,
				NativeMethods.IsZoomed(window.Handle));
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

	private void ActivateSelectedWindow(StageManager.Native.Window.IWindow window, bool allowMinimize = false,
		WindowClickAction? requestedAction = null)
	{
		var exists = NativeMethods.IsWindow(window.Handle);
		var action = requestedAction ?? WindowClickBehavior.Decide(
			window.Handle, NativeMethods.GetForegroundWindow(),
			exists && NativeMethods.IsIconic(window.Handle), exists, allowMinimize);
		if (!exists || action == WindowClickAction.Ignore) return;
		TraceCardClick($"EXEC selected={window.Handle} foreground={NativeMethods.GetForegroundWindow()} action={action} allowMinimize={allowMinimize}");
		// Honor the pointer-down intent before any tray-restore special case.
		// WM_SYSCOMMAND follows the application's own minimize handling, including
		// elevated windows where ShowWindowAsync can fail to change the state.
		if (action == WindowClickAction.Minimize)
		{
			CancelActivationVerification();
			if (allowMinimize && !NativeMethods.IsIconic(window.Handle))
			{
				var sent = NativeMethods.PostMessage(window.Handle, 0x0112, (IntPtr)0xF020, IntPtr.Zero);
				TraceCardClick($"MINIMIZE selected={window.Handle} sent={sent} error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
			}
			return;
		}
		var trayApplication = window.ProcessName.Equals("VpnManager", StringComparison.OrdinalIgnoreCase)
			? "VPN 管理器"
			: window.ProcessName.Equals("Clash for Windows", StringComparison.OrdinalIgnoreCase)
				? "Clash for Windows" : null;
		if (trayApplication is not null && NativeMethods.IsWindow(window.Handle) &&
			!Win32Helper.IsForegroundForWindow(window.Handle))
		{
			CancelActivationVerification();
			_activationVerification = CancellationTokenSource.CreateLinkedTokenSource(_notificationAreaCancellation.Token);
			_ = ActivateTrayApplicationAsync(window, trayApplication, _activationVerification);
			return;
		}
		if (!ManagedWindowPresence.ShouldDisplay(
			NativeMethods.IsWindowVisible(window.Handle),
			NativeMethods.IsIconic(window.Handle)))
		{
			if (!_closing && IsHandleCreated) BeginInvoke(new Action(RefreshStages));
			return;
		}
		// Card clicks retain the intent from pointer-down until pointer-up.
		// Explicit menu/keyboard activation never toggles a foreground window off.
		_desktopToggle.MarkDesktopDismissed();
		_renderer?.SetDesktopShown(false);

		// FocusStealer performs exactly one native restore when the window is
		// minimized. Avoid issuing a second asynchronous restore here: that race can
		// discard Windows' restore-to-maximized state on some applications.
		var previousForeground = NativeMethods.GetForegroundWindow();
		window.Focus();
		QueueFocusWindowPlacement(window);
		BeginActivationVerification(window, previousForeground);
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

	private async Task ActivateTrayApplicationAsync(IWindow window, string applicationName, CancellationTokenSource source)
	{
		try
		{
			var invoked = false;
			if (window.ProcessName.Equals("VpnManager", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					using var signal = EventWaitHandle.OpenExisting("Local\\VpnManager.Activate");
					invoked = signal.Set();
				}
				catch (WaitHandleCannotBeOpenedException) { }
				catch (UnauthorizedAccessException) { }
			}
			if (!invoked)
				invoked = await _notificationAreaClient.ActivateApplicationAsync(applicationName, source.Token);
			await Task.Delay(250, source.Token);
			var foreground = Win32Helper.IsForegroundForWindow(window.Handle);
			var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "Logs");
			Directory.CreateDirectory(directory);
			File.AppendAllText(Path.Combine(directory, "activation.log"),
				$"{DateTimeOffset.Now:O} {applicationName}: invoked={invoked}, foreground={foreground}, iconic={NativeMethods.IsIconic(window.Handle)}{Environment.NewLine}");
			if (!foreground && NativeMethods.IsWindow(window.Handle)) window.Focus();
			if (!_closing) RefreshStages();
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { Debug.WriteLine(exception); }
		finally
		{
			if (ReferenceEquals(_activationVerification, source)) _activationVerification = null;
			source.Dispose();
		}
	}

	private void BeginActivationVerification(IWindow window, IntPtr previousForeground)
	{
		CancelActivationVerification();
		_activationVerification = CancellationTokenSource.CreateLinkedTokenSource(_notificationAreaCancellation.Token);
		var source = _activationVerification;
		_ = VerifyWindowActivationAsync(window, previousForeground, source);
	}

	private void CancelActivationVerification()
	{
		_activationVerification?.Cancel();
		_activationVerification = null;
	}

	private async Task VerifyWindowActivationAsync(IWindow window, IntPtr previousForeground, CancellationTokenSource source)
	{
		try
		{
			await Task.Delay(150, source.Token);
			if (_closing || !NativeMethods.IsWindow(window.Handle) || Win32Helper.IsForegroundForWindow(window.Handle)) return;
			var foreground = NativeMethods.GetForegroundWindow();
			if (foreground != IntPtr.Zero && foreground != previousForeground) return;

			window.Focus();
			await Task.Delay(180, source.Token);
			if (_closing || !NativeMethods.IsWindow(window.Handle) || Win32Helper.IsForegroundForWindow(window.Handle)) return;
			foreground = NativeMethods.GetForegroundWindow();
			if (foreground != IntPtr.Zero && foreground != previousForeground) return;

			// A restore request can clear WS_MINIMIZE before Windows actually grants
			// foreground activation. Keep the notification-area fallback available
			// whenever both verified foreground attempts failed, not only while the
			// original HWND still reports itself as iconic.
			if (await TryActivateFromNotificationAreaAsync(window, source.Token))
			{
				await Task.Delay(220, source.Token);
				RefreshStages();
				if (!NativeMethods.IsIconic(window.Handle) || Win32Helper.IsForegroundForWindow(window.Handle)) return;
			}
			Win32Helper.FlashTaskbar(window.Handle);
		}
		catch (OperationCanceledException) { }
		finally
		{
			if (ReferenceEquals(_activationVerification, source)) _activationVerification = null;
			source.Dispose();
		}
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
					_focusAppBarReservation.ReservedBounds.Right);
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
			_focusAppBarReservation.ReservedBounds.Right);
	}

	private void PollPointer()
	{
		if (_closing)
			return;
		var screenPoint = Cursor.Position;
		DismissContextMenusOnOutsideClick(screenPoint);
		if (_renderer is null || _catalog is null)
			return;
		var nowUtc = DateTime.UtcNow;
		var pointerAtLeftEdge = IsNearLeftEdge(screenPoint);
		var foreground = NativeMethods.GetForegroundWindow();
		var largeWindowActive = UsesTransientSidebar(foreground);
		UpdateTransientSession(largeWindowActive, nowUtc);
		EnsureFocusSidebarAnchored(largeWindowActive, nowUtc);
		if (!_sidebarVisible)
		{
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
		var wasExpanded = _renderer.HasExpandedStage;
		if (!_renderer.IsScrolling && !_notificationAreaDragActive && _cardClick.Pending is null)
			_renderer.PollPointer(client);
		var hit = _renderer.HitTest(client);
		if (!_renderer.IsScrolling && !_notificationAreaDragActive && _cardClick.Pending is null)
			UpdateHoverExpandCandidate(hit, client);
		UpdateHoverHint(hit);
		var pointerNearSidebar = client.X >= 0 &&
			client.X <= _renderer.SidebarInteractionWidth &&
			client.Y >= 0 &&
			client.Y < ClientSize.Height;
		var pointerWithinTransientSidebar = hit is not null || pointerNearSidebar || pointerAtLeftEdge;
		if (hit is not null || pointerNearSidebar)
			_lastSidebarInteractionUtc = nowUtc;
		else
		{
			HideHoverHint();
		}
		if (wasExpanded != _renderer.HasExpandedStage)
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
		var pointerAtLeftEdge = SidebarIdleBehavior.ShouldRequestHiddenEdgePoll(
			_sidebarVisible,
			screenPoint,
			_sidebarScreenBounds,
			EdgeActivationWidth);
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

	private void SetTransientOverlayRaised(bool raised)
	{
		if (_transientOverlayRaised == raised || !IsHandleCreated)
			return;
		_transientOverlayRaised = raised;
		NativeMethods.SetWindowPos(
			Handle,
			raised ? NativeMethods.HwndTopmost : NativeMethods.HwndNotTopmost,
			0,
			0,
			0,
			0,
			NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
	}

	private bool IsNearLeftEdge(Point screenPoint)
	{
		return SidebarIdleBehavior.IsNearLeftEdge(screenPoint, _sidebarScreenBounds, EdgeActivationWidth);
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
			_renderer.SidebarInteractionWidth,
			DeviceDpi / 96f,
			_sidebarDisplay.Bounds.Width);
		if (!_focusAppBarReservation.SetReservation(Handle, _sidebarDisplay, reservedWidth, force))
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
		return FocusEnhancedBehavior.GetSidebarHostArea(mode, display.Bounds, display.WorkingArea);
	}

	private void EnsureFocusSidebarAnchored(bool largeWindowActive, DateTime nowUtc)
	{
		if (!IsFocusEnhanced || largeWindowActive || nowUtc < _nextFocusAnchorCheckUtc)
			return;
		_nextFocusAnchorCheckUtc = nowUtc.AddSeconds(1);
		var physicalBounds = _sidebarDisplay.Bounds;
		if (Left != physicalBounds.Left || Top != physicalBounds.Top || Height != physicalBounds.Height)
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
		var targetBounds = new Rectangle(
			sidebarArea.Left,
			sidebarArea.Top,
			Math.Min(900, sidebarArea.Width),
			sidebarArea.Height);
		if (Bounds != targetBounds)
			Bounds = targetBounds;
		UpdateWindowRegion(_sidebarVisible);
	}

	private void UpdateWindowRegion(bool includeCards)
	{
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
				var fixedRegion = BuildCardRegion(_renderer.GetInteractivePolygons(fixedLayer: true));
				_mainCardRegion?.Dispose();
				_fixedCardRegion?.Dispose();
				_mainCardRegion = mainRegion;
				_fixedCardRegion = fixedRegion;
				_regionLayoutRevision = _renderer.LayoutRevision;
			}
			using var movingRegion = _mainCardRegion.Clone();
			movingRegion.Translate(0f, _renderer.ScrollTranslationY);
			combined.Union(movingRegion);
			combined.Union(_fixedCardRegion);
		}

		var replacement = combined.Clone();
		var previous = Region;
		Region = replacement;
		previous?.Dispose();
	}

	private Region BuildCardRegion(IReadOnlyList<PointF[]> polygons)
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
			path.Widen(marginPen);
			result.Union(path);
		}
		return result;
	}

	private void CreateTrayIcon()
	{
		var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
		_trayIcon = new NotifyIcon
		{
			Text = "Stage_Manager_Lai v4.4.16",
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
