using StageManager.Services;
using StageManager.Settings;
using StageManager.Native.Window;
using StageManager.Card3DPrototype.NotificationArea;
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
	private const int HoverExpandDelayMilliseconds = 350;
	private readonly DispatcherQueueHelper _dispatcherQueue = new();
	private readonly System.Windows.Forms.Timer _stageTimer = new() { Interval = 500 };
	private readonly System.Windows.Forms.Timer _pointerTimer = new() { Interval = 50 };
	private readonly System.Windows.Forms.Timer _hoverExpandTimer = new() { Interval = HoverExpandDelayMilliseconds };
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
	private readonly CancellationTokenSource _notificationAreaCancellation = new();
	private readonly HashSet<int> _registeredHotkeys = new();
	private Screen _sidebarDisplay;
	private Rectangle _sidebarScreenBounds;
	private Compositor? _compositor;
	private DesktopWindowTarget? _target;
	private ContainerVisual? _root;
	private PrototypeStageCatalog? _catalog;
	private CompositionStageRenderer? _renderer;
	private NotifyIcon? _trayIcon;
	private string? _toolTipKey;
	private DateTime _lastSidebarInteractionUtc = DateTime.UtcNow;
	private DateTime _transientRevealUtc = DateTime.MinValue;
	private DateTime _nextFocusConstraintUtc = DateTime.MinValue;
	private DateTime _nextFocusAnchorCheckUtc = DateTime.MinValue;
	private volatile bool _sidebarVisible = true;
	private bool _transientSession;
	private bool _edgeRevealSession;
	private bool _sidebarWasVisibleBeforeTransientSession;
	private bool _transientOverlayRaised;
	private bool _demoteOverlayAfterHide;
	private volatile bool _closing;
	private int _hiddenEdgeUiRequestPending;
	private bool _appBarReapplyPending;
	private bool _leftPointerButtonDown;
	private bool _rightPointerButtonDown;
	private bool _middlePointerButtonDown;
	private string? _hoverExpandStageKey;
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
		_contextMenu.Items.Add(new ToolStripMenuItem("Stage_Manager_Lai v4.3.1") { Enabled = false });
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(toggleItem);
		_contextMenu.Items.Add(refreshItem);
		_contextMenu.Items.Add(settingsItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(exitItem);
		_stageTimer.Tick += (_, _) => RefreshStages();
		_pointerTimer.Tick += (_, _) => PollPointer();
		_hoverExpandTimer.Tick += (_, _) => ExpandHoveredMultiWindowCard();
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
		var initialTarget = _renderer.HitTest(e.Location);
		if (initialTarget is null)
		{
			Cursor = Cursors.Default;
			CancelHoverExpand();
			return;
		}

		_lastSidebarInteractionUtc = DateTime.UtcNow;
		var wasExpanded = _renderer.HasExpandedStage;
		_renderer.UpdatePointer(e.Location);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		UpdateWindowRegion(true);

		var target = _renderer.HitTest(e.Location) ?? initialTarget;
		Cursor = target.IsPinButton || target.IsExplorerButton || target.IsExpandAllButton || target.IsSidebarCollapseButton || target.IsNotificationAreaCard || target.PageDelta != 0
			? Cursors.Hand
			: Cursors.Default;
		UpdateHoverExpandCandidate(target);
		var toolTipKey = target.IsExpandAllButton
			? "sidebar:expand-all"
			: target.IsExplorerButton
			? "sidebar:explorer"
			: target.IsNotificationAreaCard
				? $"notification:{target.NotificationIcon?.Ordinal}:{target.NotificationIconName}"
			: target.IsPinButton
				? $"pin:{target.StageKey}:{_renderer.IsExpandedStagePinned}"
				: target.Window is { } pointedWindow
					? $"window:{pointedWindow.Handle}"
					: target.IsSidebarCollapseButton
						? "sidebar:collapse"
						: target.PageDelta != 0
							? $"page:{target.StageKey}:{target.PageDelta}"
							: $"stage:{target.StageKey}";
		if (!string.Equals(toolTipKey, _toolTipKey, StringComparison.Ordinal))
		{
			_toolTipKey = toolTipKey;
			_toolTip.Show(
				GetToolTipText(target),
				this,
				e.X + 16,
				e.Y + 14,
				3200);
		}
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		CancelHoverExpand();
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		if (e.Button == MouseButtons.Right)
		{
			var target = _renderer?.HitTest(e.Location);
			if (target?.IsNotificationAreaCard == true)
				ShowNotificationAreaContextMenu(target);
			else if (target is not null && !target.IsExplorerButton && !target.IsExpandAllButton && !target.IsPinButton && !target.IsSidebarCollapseButton && target.PageDelta == 0)
				ShowCardContextMenu(target);
			else
				ShowOwnedContextMenu(_contextMenu);
			return;
		}
		if (e.Button != MouseButtons.Left || _renderer is null)
			return;
		var clickTarget = _renderer.HitTest(e.Location);
		if (e.Clicks >= 2 && clickTarget?.Window is not null && clickTarget.IsPinButton != true)
		{
			HandleCardDoubleClick(clickTarget);
			return;
		}
		var wasExpanded = _renderer.HasExpandedStage;
		var window = _renderer.ActivateAt(e.Location);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		if (wasExpanded != _renderer.HasExpandedStage)
			UpdateWindowRegion(true);
		if (_renderer.ConsumeExplorerLaunchRequest())
		{
			OpenFileExplorer();
			return;
		}
		if (_renderer.ConsumeSidebarCollapseRequest())
		{
			SetSidebarVisible(false);
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
		ActivateSelectedWindow(window, allowMinimize: true);
		BeginInvoke(new Action(RefreshStages));
	}

	private void HandleCardDoubleClick(CardHitTarget? target)
	{
		if (target?.Window is not { } window)
			return;

		MaximizeWindows([window]);
	}

	protected override void OnMouseWheel(MouseEventArgs e)
	{
		base.OnMouseWheel(e);
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		_renderer?.Scroll(e.Delta);
		UpdateWindowRegion(_sidebarVisible);
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
		_notificationAreaCancellation.Cancel();
		_focusAppBarReservation.Dispose();
		UnregisterHotkeys();
		_stageTimer.Stop();
		_pointerTimer.Stop();
		_hoverExpandTimer.Stop();
		_hiddenEdgeTimer.Change(Timeout.Infinite, Timeout.Infinite);
		_regionCollapseTimer.Stop();
		_displayChangeTimer.Stop();
		_previewReleaseTimer.Stop();
		_stageTimer.Dispose();
		_pointerTimer.Dispose();
		_hoverExpandTimer.Dispose();
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
		_dispatcherQueue.Dispose();
		base.OnFormClosed(e);
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
		if (_catalog is null || _renderer is null)
			return;
		var settings = _catalog.Settings.Current;
		_renderer.SetAnimationsEnabled(settings.AnimationsEnabled);
		_renderer.SetCardScale(settings.CardScale);
		var layoutRegionChanged = _renderer.SetSidebarVerticalOffset(settings.SidebarVerticalOffset);
		if (_renderer.SetExplorerButtonEnabled(settings.ShowExplorerButton))
			layoutRegionChanged = true;
		if (_renderer.SetPinButtonEnabled(settings.ShowExpandedPinButton))
			layoutRegionChanged = true;
		if (_renderer.SetCollapseButtonEnabled(FocusEnhancedBehavior.ShouldShowCollapseButton(settings.StageMode)))
			layoutRegionChanged = true;
		if (_renderer.SetNotificationAreaCardEnabled(settings.ShowNotificationAreaCard))
		{
			layoutRegionChanged = true;
			if (settings.ShowNotificationAreaCard)
				_ = RefreshNotificationAreaAsync(initialDelay: false);
		}
		if (layoutRegionChanged)
			UpdateWindowRegion(_sidebarVisible);
		_renderer.SetPreviewPolicy(settings.PreviewRefreshMinutes, settings.PausePreviewRefreshWhenHidden);
		UiText.Apply(_contextMenu.Items, settings.UiLanguage);
		RegisterHotkeys();
		if (settings.StageMode != StageMode.Focus)
			_focusAppBarReservation.Remove();
		if ((settings.StageMode == StageMode.Focus || !settings.IdleAutoHideEnabled) && !_sidebarVisible)
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
		_renderer?.ReleasePointerPress();
		Cursor = Cursors.Default;
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		base.OnMouseUp(e);
		_renderer?.ReleasePointerPress();
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
				() => ActivateSelectedWindow(window, allowMinimize: false));
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
				ActivateSelectedWindow(activationTarget, allowMinimize: false);
		}
		if (!_closing && IsHandleCreated)
			BeginInvoke(new Action(RefreshStages));
	}

	private string GetToolTipText(CardHitTarget target)
	{
		if (target.IsNotificationAreaCard)
			return string.IsNullOrWhiteSpace(target.NotificationIconName)
				? L("Windows hidden icons · Right-click to refresh", "Windows 隐藏图标 · 右键刷新")
				: target.NotificationIconName;
		if (target.IsExplorerButton)
			return L("Open File Explorer", "打开文件资源管理器");
		if (target.IsExpandAllButton)
			return L("Expand or collapse all multi-window cards", "展开或收起全部多窗口卡片");
		if (target.IsPinButton)
			return _renderer?.IsExpandedStagePinned == true
				? L("FIXED · Click to release", "FIXED · 点击解除固定")
				: L("FIX · Keep expanded", "FIX · 保持展开");
		if (target.IsSidebarCollapseButton)
			return L("Hide sidebar", "隐藏侧栏");
		if (target.PageDelta < 0)
			return L("Previous windows", "上一组窗口");
		if (target.PageDelta > 0)
			return L("Next windows", "下一组窗口");
		if (target.Window is { } window)
		{
			var state = window.IsMinimized ? L(" (minimized)", "（已最小化）") : string.Empty;
			return L(
				$"{window.Title}{state}\nDouble-click to maximize · Right-click for more options",
				$"{window.Title}{state}\n双击最大化 · 右键查看更多选项");
		}

		var stage = _catalog?.GetStages().FirstOrDefault(snapshot =>
			string.Equals(snapshot.Key, target.StageKey, StringComparison.OrdinalIgnoreCase));
		return stage is null
			? L("Application group", "应用组")
			: L(
				$"{stage.Title} · {stage.Windows.Count} windows\nClick to expand or collapse · Right-click for options",
				$"{stage.Title} · {stage.Windows.Count} 个窗口\n点击展开或收起 · 右键查看更多选项");
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

	private void SetSidebarVisible(bool visible)
	{
		if (_renderer is null)
			return;
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
			CancelHoverExpand();
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
		ActivateSelectedWindow(window, allowMinimize: false);
	}

	private void ActivateSelectedWindow(StageManager.Native.Window.IWindow window, bool allowMinimize)
	{
		if (!ManagedWindowPresence.ShouldDisplay(
			NativeMethods.IsWindowVisible(window.Handle),
			NativeMethods.IsIconic(window.Handle)))
			return;
		var action = WindowClickBehavior.Decide(
			window.Handle,
			NativeMethods.GetForegroundWindow(),
			window.IsMinimized,
			NativeMethods.IsWindow(window.Handle));
		if (action == WindowClickAction.Ignore)
			return;
		if (allowMinimize && action == WindowClickAction.Minimize)
		{
			NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwMinimize);
			return;
		}

		if (window.IsMinimized)
			NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwRestore);
		window.Focus();
		QueueFocusWindowPlacement(window);
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
				SetTransientOverlayRaised(true);
				SetSidebarVisible(true);
			}
			return;
		}

		var client = PointToClient(screenPoint);
		var wasExpanded = _renderer.HasExpandedStage;
		_renderer.PollPointer(client);
		var hit = _renderer.HitTest(client);
		UpdateHoverExpandCandidate(hit);
		var pointerNearSidebar = client.X >= 0 &&
			client.X <= _renderer.SidebarInteractionWidth &&
			client.Y >= 0 &&
			client.Y < ClientSize.Height;
		var pointerWithinTransientSidebar = hit is not null || pointerNearSidebar || pointerAtLeftEdge;
		if (hit is not null || pointerNearSidebar)
			_lastSidebarInteractionUtc = nowUtc;
		else
		{
			_toolTipKey = null;
			_toolTip.Hide(this);
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
		if (transientAction == TransientSidebarAction.Hide)
		{
			_edgeRevealSession = false;
			SetSidebarVisible(false);
			return;
		}
		if (transientOverlayActive)
		{
			if (pointerWithinTransientSidebar && !_transientOverlayRaised)
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

	private void UpdateHoverExpandCandidate(CardHitTarget? target)
	{
		if (_renderer is null || target is null || !_renderer.CanExpandOnHover(target))
		{
			CancelHoverExpand();
			return;
		}

		if (string.Equals(_hoverExpandStageKey, target.StageKey, StringComparison.OrdinalIgnoreCase) &&
			_hoverExpandTimer.Enabled)
			return;

		_hoverExpandStageKey = target.StageKey;
		_hoverExpandTimer.Stop();
		_hoverExpandTimer.Start();
	}

	private void CancelHoverExpand()
	{
		_hoverExpandTimer.Stop();
		_hoverExpandStageKey = null;
	}

	private void ExpandHoveredMultiWindowCard()
	{
		_hoverExpandTimer.Stop();
		var stageKey = _hoverExpandStageKey;
		_hoverExpandStageKey = null;
		if (_renderer is null || !_sidebarVisible || string.IsNullOrEmpty(stageKey))
			return;

		var wasExpanded = _renderer.HasExpandedStage;
		if (!_renderer.TryExpandHoveredPrimaryCard(PointToClient(Cursor.Position), stageKey))
			return;

		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		UpdateWindowRegion(true);
	}

	private void PollHiddenEdgeFromBackground()
	{
		if (_closing || _sidebarVisible || !NativeMethods.GetCursorPos(out var nativePoint))
			return;
		var screenPoint = new Point(nativePoint.X, nativePoint.Y);
		if (!SidebarIdleBehavior.ShouldRequestHiddenEdgePoll(
			_sidebarVisible,
			screenPoint,
			_sidebarScreenBounds,
			EdgeActivationWidth) ||
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
		if (FocusEnhancedBehavior.ShouldRestoreAfterTransientSession(mode, _sidebarWasVisibleBeforeTransientSession))
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
			_edgeRevealSession);
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
		if (!_sidebarVisible)
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
		using var combined = new Region();
		combined.MakeEmpty();
		combined.Union(new Rectangle(0, 0, 1, 1));
		if (includeCards && _renderer is not null)
		{
			var margin = Math.Max(12f, 16f * DeviceDpi / 96f);
			foreach (var polygon in _renderer.GetInteractivePolygons())
			{
				if (polygon.Length < 3)
					continue;
				using var cardPath = new GraphicsPath();
				cardPath.AddPolygon(polygon);
				combined.Union(cardPath);
				using var shadowPath = (GraphicsPath)cardPath.Clone();
				using var marginPen = new Pen(Color.Black, margin * 2f) { LineJoin = LineJoin.Round };
				shadowPath.Widen(marginPen);
				combined.Union(shadowPath);
			}
		}

		var replacement = combined.Clone();
		var previous = Region;
		Region = replacement;
		previous?.Dispose();
	}

	private void CreateTrayIcon()
	{
		var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
		_trayIcon = new NotifyIcon
		{
			Text = "Stage_Manager_Lai v4.3.1",
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
