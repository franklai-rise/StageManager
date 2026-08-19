using StageManager.Services;
using StageManager.Settings;
using StageManager.Desktop.Commands;
using StageManager.Desktop.Lifecycle;
using StageManager.Infrastructure;
using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Windows.Forms;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace StageManager.Desktop;

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
	private const int ToggleWindowInStageHotkeyId = 0x4C44;
	private const int WindowSwitcherHotkeyId = 0x4C45;
	private const int EdgeActivationWidth = 8;
	private readonly DispatcherQueueHelper _dispatcherQueue = new();
	private readonly System.Windows.Forms.Timer _stageTimer = new() { Interval = 15000 };
	private readonly System.Windows.Forms.Timer _catalogRefreshTimer = new() { Interval = 33 };
	private readonly System.Windows.Forms.Timer _pointerTimer = new() { Interval = 250 };
	private readonly System.Windows.Forms.Timer _regionCollapseTimer = new() { Interval = 260 };
	private readonly System.Windows.Forms.Timer _displayChangeTimer = new() { Interval = 250 };
	private readonly System.Windows.Forms.Timer _previewReleaseTimer = new() { Interval = 15000 };
	private readonly ToolTip _toolTip = new() { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 3000, ShowAlways = true };
	private readonly ContextMenuStrip _contextMenu = new();
	private readonly ContextMenuStrip _cardContextMenu = new();
	private readonly AppCommandDispatcher _commands;
	private readonly DisplayIdentityService _displayIdentities = new();
	private readonly HashSet<int> _registeredHotkeys = new();
	private Screen _sidebarDisplay;
	private Rectangle _sidebarScreenBounds;
	private Compositor? _compositor;
	private DesktopWindowTarget? _target;
	private ContainerVisual? _root;
	private PrototypeStageCatalog? _catalog;
	private CompositionStageRenderer? _renderer;
	private NotifyIcon? _trayIcon;
	private WindowSwitcherForm? _windowSwitcher;
	private string? _toolTipKey;
	private DateTime _lastSidebarInteractionUtc = DateTime.UtcNow;
	private DateTime _transientRevealUtc = DateTime.MinValue;
	private bool _sidebarVisible = true;
	private bool _transientSession;
	private bool _sidebarWasVisibleBeforeTransientSession;
	private bool _transientOverlayRaised;
	private bool _demoteOverlayAfterHide;
	private bool _closing;
	private CardClickContext? _lastCardClick;
	private CardHitTarget? _pressedCardTarget;
	private Point _cardPressPoint;
	private bool _cardDragActive;
	private bool _suppressNextCardMouseUp;
	private readonly DiagnosticBundleExporter _diagnostics = new();

	public PrototypeForm()
	{
		_commands = new AppCommandDispatcher(this);
		RegisterCommands();
		Text = "Stage_Manager_Lai";
		AccessibleName = "Stage Manager sidebar";
		AccessibleDescription = "A list of application stages and windows. Use the configured keyboard shortcuts or the searchable switcher for keyboard access.";
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		TopMost = false;
		_sidebarDisplay = SidebarDisplayPolicy.SelectLeftmost(Screen.AllScreens, screen => screen.WorkingArea);
		_sidebarScreenBounds = _sidebarDisplay.WorkingArea;
		Bounds = new Rectangle(_sidebarDisplay.WorkingArea.Left, _sidebarDisplay.WorkingArea.Top, Math.Min(900, _sidebarDisplay.WorkingArea.Width), _sidebarDisplay.WorkingArea.Height);
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

		var toggleItem = new ToolStripMenuItem("Show / Hide sidebar");
		toggleItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.ToggleSidebar));
		var settingsItem = new ToolStripMenuItem("Settings...");
		settingsItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.OpenSettings));
		var switcherItem = new ToolStripMenuItem("Search applications / windows...");
		switcherItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.ShowWindowSwitcher));
		var refreshItem = new ToolStripMenuItem("Refresh all previews now");
		refreshItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.RefreshAllPreviews));
		var undoItem = new ToolStripMenuItem("Undo last stage change");
		undoItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.UndoStageChange));
		_contextMenu.Opening += (_, _) => undoItem.Enabled = _catalog?.CanUndo == true;
		var diagnosticsItem = new ToolStripMenuItem("Export diagnostics...");
		diagnosticsItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.ExportDiagnostics));
		var exitItem = new ToolStripMenuItem("Exit Stage_Manager_Lai");
		exitItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.Exit));
		_contextMenu.Items.Add(new ToolStripMenuItem(AppVersionInfo.DisplayName) { Enabled = false });
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(toggleItem);
		_contextMenu.Items.Add(switcherItem);
		_contextMenu.Items.Add(refreshItem);
		_contextMenu.Items.Add(undoItem);
		_contextMenu.Items.Add(settingsItem);
		_contextMenu.Items.Add(diagnosticsItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(exitItem);
		_stageTimer.Tick += (_, _) =>
			_catalog?.CalibrateWindows();
		_catalogRefreshTimer.Tick += (_, _) =>
		{
			_catalogRefreshTimer.Stop();
			RefreshStages();
			PollPointer();
		};
		_pointerTimer.Tick += (_, _) => PollPointer();
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
			UpdateSidebarDisplay();
			RecoverOffscreenWindowsAfterDisplayChange();
		};
		_previewReleaseTimer.Tick += (_, _) =>
		{
			_previewReleaseTimer.Stop();
			if (_sidebarVisible)
				return;
			_renderer?.ReleasePreviewSurfaces();
		};
		SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
		SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
	}

	protected override bool ShowWithoutActivation => true;

	protected override AccessibleObject CreateAccessibilityInstance() => new SidebarAccessibleObject(this);

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
			_catalog.Changed += Catalog_Changed;
			_catalog.Settings.SettingsChanged += Settings_SettingsChanged;
			_renderer = new CompositionStageRenderer(
				this,
				_compositor,
				_root,
				_catalog.Settings.Current.CardScale,
				_catalog.Settings.Current.AnimationsEnabled,
				_catalog.Settings.Current.RenderProfile);
			_renderer.Resize(ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
			CreateTrayIcon();
			ApplyRuntimeSettings(updateStartup: false);
			RefreshStages();
			UpdateWindowRegion(true);
			_stageTimer.Start();
		}
		catch (Exception exception)
		{
			AppLogger.Error("The main sidebar could not finish starting.", exception);
			throw;
		}
	}

	internal void ShowSidebarFromExternalCommand()
	{
		if (!_closing)
			_commands.Execute(new(AppCommandKind.ShowSidebar));
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

	protected override void OnDpiChanged(DpiChangedEventArgs e)
	{
		base.OnDpiChanged(e);
		_renderer?.Resize(ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
		UpdateWindowRegion(_sidebarVisible);
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if ((e.Button & MouseButtons.Left) != 0 && _pressedCardTarget is not null && !_cardDragActive)
		{
			var dragSize = SystemInformation.DragSize;
			if (Math.Abs(e.X - _cardPressPoint.X) >= dragSize.Width / 2 ||
				Math.Abs(e.Y - _cardPressPoint.Y) >= dragSize.Height / 2)
			{
				_cardDragActive = true;
				Cursor = Cursors.SizeAll;
				_toolTip.Hide(this);
			}
		}
		if (_renderer is null || !_sidebarVisible)
			return;
		var initialTarget = _renderer.HitTest(e.Location);
		if (initialTarget is null)
			return;

		RecordSidebarInteraction();
		var wasExpanded = _renderer.HasExpandedStage;
		_renderer.UpdatePointer(e.Location);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		UpdateWindowRegion(true);

		var target = _renderer.HitTest(e.Location) ?? initialTarget;
		var toolTipKey = target.Window is { } pointedWindow
			? $"window:{pointedWindow.Handle}"
			: target.IsSidebarCollapseButton
				? "sidebar:collapse"
				: target.IsStageDragHandle
					? $"stage-handle:{target.StageKey}"
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
		RecordSidebarInteraction();
		if (e.Button == MouseButtons.Right)
		{
			var target = _renderer?.HitTest(e.Location);
			if (target is not null && !target.IsSidebarCollapseButton && target.PageDelta == 0)
				ShowCardContextMenu(target);
			else
				_contextMenu.Show(Cursor.Position);
			return;
		}
		if (e.Button != MouseButtons.Left || _renderer is null)
			return;
		var clickTarget = _renderer.HitTest(e.Location);
		if (e.Clicks >= 2)
		{
			_suppressNextCardMouseUp = true;
			_pressedCardTarget = null;
			Capture = false;
			HandleCardDoubleClick(clickTarget);
			return;
		}

		_pressedCardTarget = clickTarget;
		_cardPressPoint = e.Location;
		_cardDragActive = false;
		Capture = clickTarget is not null;
	}

	protected override void OnMouseUp(MouseEventArgs e)
	{
		base.OnMouseUp(e);
		if (e.Button != MouseButtons.Left)
			return;
		if (_suppressNextCardMouseUp)
		{
			_suppressNextCardMouseUp = false;
			return;
		}

		var source = _pressedCardTarget;
		var wasDragging = _cardDragActive;
		_pressedCardTarget = null;
		_cardDragActive = false;
		Capture = false;
		Cursor = Cursors.Default;
		if (source is null || _renderer is null)
			return;

		if (wasDragging)
		{
			HandleCardDrop(source, _renderer.HitTest(e.Location), e.Location);
			return;
		}
		HandleCardClick(source, e.Location);
	}

	private void HandleCardClick(CardHitTarget clickTarget, Point clientPoint)
	{
		if (_renderer is null)
			return;
		_lastCardClick = clickTarget.Window is { } clickedWindow
			? new CardClickContext(
				clickedWindow.Handle,
				OffscreenWindowRecovery.IsOffscreen(clickedWindow),
				clickedWindow.Handle == NativeMethods.GetForegroundWindow(),
				NativeMethods.IsIconic(clickedWindow.Handle),
				clickedWindow.IsMaximized)
			: null;
		var wasExpanded = _renderer.HasExpandedStage;
		var window = _renderer.ActivateAt(clientPoint);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		if (wasExpanded != _renderer.HasExpandedStage)
			UpdateWindowRegion(true);
		if (_renderer.ConsumeSidebarCollapseRequest())
		{
			_commands.Execute(new(AppCommandKind.HideSidebar));
			return;
		}
		if (window is null)
			return;
		_commands.Execute(new(AppCommandKind.ActivateWindow, Window: window, StageKey: clickTarget.StageKey, AllowMinimize: true));
		BeginInvoke(new Action(RefreshStages));
	}

	private void HandleCardDrop(CardHitTarget source, CardHitTarget? target, Point clientPoint)
	{
		if (_catalog is null || _renderer is null || source.IsSidebarCollapseButton || source.PageDelta != 0)
			return;
		var sourceStage = _catalog.GetStages().FirstOrDefault(stage =>
			string.Equals(stage.Key, source.StageKey, StringComparison.OrdinalIgnoreCase));
		if (sourceStage is null)
			return;

		if (target is not null && !target.IsSidebarCollapseButton && target.PageDelta == 0 &&
			!string.Equals(source.StageKey, target.StageKey, StringComparison.OrdinalIgnoreCase))
		{
			var moveSingleWindow = source.Window is not null && sourceStage.Windows.Count > 1;
			_commands.Execute(moveSingleWindow
				? new(AppCommandKind.MoveWindowToStage, Window: source.Window, StageKey: source.StageKey, TargetStageKey: target.StageKey)
				: new(AppCommandKind.MergeStages, StageKey: source.StageKey, TargetStageKey: target.StageKey));
			return;
		}

		var draggedOutside = clientPoint.X > _renderer.SidebarInteractionWidth + 24 || clientPoint.X < -24;
		if (draggedOutside && source.Window is not null && sourceStage.Windows.Count > 1)
			_commands.Execute(new(AppCommandKind.ExtractWindow, Window: source.Window, StageKey: source.StageKey));
	}

	private void HandleCardDoubleClick(CardHitTarget? target)
	{
		if (target?.Window is not { } window)
		{
			_lastCardClick = null;
			return;
		}

		var context = _lastCardClick;
		_lastCardClick = null;
		var sameWindow = context is { } previous && previous.Handle == window.Handle;
		var wasOffscreen = sameWindow ? context!.Value.WasOffscreen : OffscreenWindowRecovery.IsOffscreen(window);
		if (wasOffscreen)
		{
			var targetDisplay = Screen.FromPoint(Cursor.Position);
			if (OffscreenWindowRecovery.TryCenterIfOffscreen(
				window,
				targetDisplay,
				restoreMaximized: sameWindow && context!.Value.WasMaximized))
			{
				ActivateSelectedWindow(window, allowMinimize: false);
				BeginInvoke(new Action(RefreshStages));
			}
			return;
		}

		if (sameWindow && context!.Value.WasForeground && !context.Value.WasMinimized && NativeMethods.IsIconic(window.Handle))
		{
			NativeMethods.ShowWindowAsync(
				window.Handle,
				context.Value.WasMaximized ? NativeMethods.SwShowMaximized : NativeMethods.SwRestore);
			window.Focus();
			BeginInvoke(new Action(RefreshStages));
		}
	}

	protected override void OnMouseWheel(MouseEventArgs e)
	{
		base.OnMouseWheel(e);
		RecordSidebarInteraction();
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
		if (message.Msg == WmHotkey)
		{
			switch (message.WParam.ToInt32())
			{
				case ToggleSidebarHotkeyId:
					_commands.Execute(new(AppCommandKind.ToggleSidebar));
					break;
				case PreviousStageHotkeyId:
					_commands.Execute(new(AppCommandKind.PreviousStage));
					break;
				case NextStageHotkeyId:
					_commands.Execute(new(AppCommandKind.NextStage));
					break;
				case ToggleWindowInStageHotkeyId:
					_commands.Execute(new(AppCommandKind.ToggleWindowInCurrentStage));
					break;
				case WindowSwitcherHotkeyId:
					_commands.Execute(new(AppCommandKind.ShowWindowSwitcher));
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
		UnregisterHotkeys();
		_stageTimer.Stop();
		_catalogRefreshTimer.Stop();
		_pointerTimer.Stop();
		_regionCollapseTimer.Stop();
		_displayChangeTimer.Stop();
		_previewReleaseTimer.Stop();
		_stageTimer.Dispose();
		_catalogRefreshTimer.Dispose();
		_pointerTimer.Dispose();
		_regionCollapseTimer.Dispose();
		_displayChangeTimer.Dispose();
		_previewReleaseTimer.Dispose();
		_commands.Dispose();
		_toolTip.Dispose();
		_contextMenu.Dispose();
		_cardContextMenu.Dispose();
		SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
		SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
		if (_trayIcon is not null)
		{
			_trayIcon.Visible = false;
			_trayIcon.Icon?.Dispose();
			_trayIcon.Dispose();
			_trayIcon = null;
		}
		if (_windowSwitcher is not null)
		{
			_windowSwitcher.Close();
			_windowSwitcher.Dispose();
			_windowSwitcher = null;
		}
		_renderer?.Dispose();
		_renderer = null;
		if (_catalog is not null)
			_catalog.Changed -= Catalog_Changed;
		if (_catalog is not null)
			_catalog.Settings.SettingsChanged -= Settings_SettingsChanged;
		_catalog?.Dispose();
		_catalog = null;
		_root?.Dispose();
		_root = null;
		_target?.Dispose();
		_target = null;
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
		_renderer.SetDesktopSession(_catalog.CurrentDesktopId);
		var previousRevision = _renderer.LayoutRevision;
		_renderer.Synchronize(stages);
		if (_sidebarVisible && previousRevision != _renderer.LayoutRevision)
		{
			UpdateWindowRegion(true);
			AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
		}
	}

	private void Catalog_Changed(object? sender, EventArgs e)
	{
		if (_closing || !IsHandleCreated)
			return;
		try
		{
			BeginInvoke(new Action(() =>
			{
				_catalogRefreshTimer.Stop();
				_catalogRefreshTimer.Start();
			}));
		}
		catch (InvalidOperationException)
		{
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
		_renderer.SetRenderProfile(settings.RenderProfile);
		var highContrast = SystemInformation.HighContrast;
		_renderer.SetHighContrast(highContrast);
		_renderer.SetAnimationsEnabled(AccessibilityPreferences.ShouldAnimate(
			settings.AnimationsEnabled,
			highContrast,
			AccessibilityPreferences.SystemAnimationsEnabled));
		_renderer.SetCardScale(settings.CardScale);
		_renderer.SetPreviewPolicy(
			settings.PreviewRefreshMinutes,
			settings.PausePreviewRefreshWhenHidden,
			window => settings.FindApplicationRule(window.ProcessName)?.PreviewMode ?? PreviewMode.Auto);
		_renderer.SetIdleHint(settings.IdleAutoHideEnabled, settings.IdleAutoHideSeconds);
		UpdateSidebarDisplay();
		_renderer.SetSidebarDisplay(_sidebarDisplay.DeviceName);
		RegisterHotkeys();
		if (!settings.IdleAutoHideEnabled && !_sidebarVisible)
			SetSidebarVisible(true);
		ConfigurePointerPolling(IsLargeWindowActive());
		if (updateStartup)
		{
			try
			{
				StageManager.AutoStart.SetStartup(StageManager.AutoStart.DefaultAppName, settings.StartWithWindows);
			}
			catch (Exception exception)
			{
				MessageBox.Show(this, $"Settings were saved, but the startup entry could not be updated.\n\n{exception.Message}", "Startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}
		RefreshStages();
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

	private void ShowWindowSwitcher()
	{
		if (_catalog is null || _closing)
			return;
		if (_windowSwitcher is { IsDisposed: false })
		{
			_windowSwitcher.Show();
			_windowSwitcher.Activate();
			return;
		}

		var entries = _catalog.GetStages()
			.SelectMany(stage => stage.Windows.Select(window => new WindowSwitcherEntry(
				stage.Key,
				stage.Title,
				window,
				DescribeWindowState(window))))
			.ToArray();
		_windowSwitcher = new WindowSwitcherForm(entries, entry =>
			_commands.Execute(new(
				AppCommandKind.ActivateWindow,
				Window: entry.Window,
				StageKey: entry.StageKey,
				AllowMinimize: false)));
		_windowSwitcher.FormClosed += (_, _) => _windowSwitcher = null;
		_windowSwitcher.Show();
		_windowSwitcher.Activate();
	}

	private string DescribeWindowState(StageManager.Native.Window.IWindow window)
	{
		var states = new List<string>();
		if (window.IsMinimized)
			states.Add("minimized");
		if (OffscreenWindowRecovery.IsOffscreen(window))
			states.Add("off-screen");
		else
		{
			var location = window.Location;
			var display = Screen.FromRectangle(new Rectangle(
				location.X,
				location.Y,
				Math.Max(1, location.Width),
				Math.Max(1, location.Height)));
			if (!string.Equals(display.DeviceName, _sidebarDisplay.DeviceName, StringComparison.OrdinalIgnoreCase))
				states.Add($"on {display.DeviceName}");
		}
		return string.Join(", ", states);
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
		var title = target.Window?.Title ?? stage?.Title ?? "Application";
		_cardContextMenu.Items.Add(new ToolStripMenuItem(title) { Enabled = false });
		_cardContextMenu.Items.Add(new ToolStripSeparator());
		if (stage is not null)
		{
			var activateStageItem = new ToolStripMenuItem("Bring this stage to front")
			{
				Enabled = !stage.IsCurrent
			};
			activateStageItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.ActivateStage, StageKey: stage.Key));
			_cardContextMenu.Items.Add(activateStageItem);
		}
		if (target.Window is { } window)
		{
			var activateItem = new ToolStripMenuItem("Bring this window to front");
			activateItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.ActivateWindow, Window: window, StageKey: stage?.Key));
			_cardContextMenu.Items.Add(activateItem);

			var recoverItem = new ToolStripMenuItem("Recover to this display")
			{
				Enabled = OffscreenWindowRecovery.IsOffscreen(window)
			};
			recoverItem.Click += (_, _) =>
			{
				var display = Screen.FromPoint(Cursor.Position);
				if (OffscreenWindowRecovery.TryCenterIfOffscreen(window, display, window.IsMaximized))
					ActivateSelectedWindow(window, allowMinimize: false);
			};
			_cardContextMenu.Items.Add(recoverItem);

			if (stage is not null && stage.Windows.Count > 1)
			{
				var extractItem = new ToolStripMenuItem("Move this window to its own stage");
				extractItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.ExtractWindow, Window: window, StageKey: stage.Key));
				_cardContextMenu.Items.Add(extractItem);
			}

			var pinItem = new ToolStripMenuItem("Pin to all stages")
			{
				Checked = _catalog.IsPinned(window.Handle),
				CheckOnClick = false
			};
			pinItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.TogglePinToAllStages, Window: window, StageKey: stage?.Key));
			_cardContextMenu.Items.Add(pinItem);
		}

		var currentStage = _catalog.GetStages().FirstOrDefault(candidate => candidate.IsCurrent);
		if (stage is not null && currentStage is not null && !stage.IsCurrent)
		{
			var mergeItem = new ToolStripMenuItem("Merge into current stage");
			mergeItem.Click += (_, _) => _commands.Execute(new(
				AppCommandKind.MergeStages,
				StageKey: stage.Key,
				TargetStageKey: currentStage.Key));
			_cardContextMenu.Items.Add(mergeItem);
		}

		var refreshItem = new ToolStripMenuItem("Refresh preview now");
		refreshItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.RefreshStagePreviews, StageKey: target.StageKey));
		_cardContextMenu.Items.Add(refreshItem);
		var undoItem = new ToolStripMenuItem("Undo last stage change")
		{
			Enabled = _catalog.CanUndo
		};
		undoItem.Click += (_, _) => _commands.Execute(new(AppCommandKind.UndoStageChange));
		_cardContextMenu.Items.Add(undoItem);
		var processNames = (stage?.Windows ?? Array.Empty<StageManager.Native.Window.IWindow>())
			.Select(window => window.ProcessName)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (processNames.Length > 0)
		{
			_cardContextMenu.Items.Add(new ToolStripSeparator());
			var ignoreItem = new ToolStripMenuItem(processNames.Length == 1
				? $"Ignore {processNames[0]}"
				: "Ignore applications in this card");
			ignoreItem.Click += (_, _) => _catalog.Settings.AddIgnoredProcesses(processNames);
			_cardContextMenu.Items.Add(ignoreItem);
		}
		_cardContextMenu.Show(Cursor.Position);
	}

	private string GetToolTipText(CardHitTarget target)
	{
		if (target.IsSidebarCollapseButton)
			return "Hide sidebar";
		if (target.IsStageDragHandle)
			return "Click to expand or collapse · Drag to move the whole stage";
		if (target.PageDelta < 0)
			return "Previous windows";
		if (target.PageDelta > 0)
			return "Next windows";
		if (target.Window is { } window)
		{
			var state = DescribeCardState(_renderer?.GetWindowStatus(window.Handle) ?? WindowCardState.None);
			var stateLine = string.IsNullOrWhiteSpace(state) ? string.Empty : $"\n{state}";
			return $"{window.Title}{stateLine}\nDouble-click to recover an off-screen window · Right-click for options";
		}

		var stage = _catalog?.GetStages().FirstOrDefault(snapshot =>
			string.Equals(snapshot.Key, target.StageKey, StringComparison.OrdinalIgnoreCase));
		return stage is null
			? "Application group"
			: $"{stage.Title} · {stage.Windows.Count} windows\nClick to expand or collapse · Right-click for options";
	}

	private static string DescribeCardState(WindowCardState state)
	{
		var labels = new List<string>();
		if (state.HasFlag(WindowCardState.Minimized))
			labels.Add("Minimized");
		if (state.HasFlag(WindowCardState.Offscreen))
			labels.Add("Off-screen — double-click to recover");
		if (state.HasFlag(WindowCardState.OtherDisplay))
			labels.Add("On another display");
		if (state.HasFlag(WindowCardState.CaptureFailed))
			labels.Add("Preview unavailable");
		if (state.HasFlag(WindowCardState.Pinned))
			labels.Add("Pinned to all stages");
		return string.Join(" · ", labels);
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
		RegisterHotkey(ToggleWindowInStageHotkeyId, settings.ToggleWindowInStageHotkey);
		RegisterHotkey(WindowSwitcherHotkeyId, settings.WindowSwitcherHotkey);
	}

	private void RegisterCommands()
	{
		_commands.Register(AppCommandKind.ToggleSidebar, _ => ToggleSidebarVisibility());
		_commands.Register(AppCommandKind.ShowSidebar, _ => SetSidebarVisible(true));
		_commands.Register(AppCommandKind.HideSidebar, _ => SetSidebarVisible(false));
		_commands.Register(AppCommandKind.OpenSettings, _ => ShowSettings());
		_commands.Register(AppCommandKind.ShowWindowSwitcher, _ => ShowWindowSwitcher());
		_commands.Register(AppCommandKind.RefreshAllPreviews, _ => _renderer?.RefreshAllPreviews());
		_commands.Register(AppCommandKind.RefreshStagePreviews, request =>
		{
			if (!string.IsNullOrWhiteSpace(request.StageKey))
				_renderer?.RefreshStagePreviews(request.StageKey);
		});
		_commands.Register(AppCommandKind.ActivateStage, request =>
		{
			if (_catalog is not null && !string.IsNullOrWhiteSpace(request.StageKey))
				_ = ExecuteStageCommandAsync(() => _catalog.SwitchToAsync(request.StageKey));
		});
		_commands.Register(AppCommandKind.ActivateWindow, request =>
		{
			if (request.Window is not null)
				_ = ActivateWindowFromCommandAsync(request);
		});
		_commands.Register(AppCommandKind.PreviousStage, _ => ActivateRelativeStage(-1));
		_commands.Register(AppCommandKind.NextStage, _ => ActivateRelativeStage(1));
		_commands.Register(AppCommandKind.ToggleWindowInCurrentStage, request =>
		{
			if (_catalog is not null)
				_ = ExecuteStageCommandAsync(_catalog.ToggleForegroundWindowAsync);
		});
		_commands.Register(AppCommandKind.MergeStages, request =>
		{
			if (_catalog is not null && !string.IsNullOrWhiteSpace(request.StageKey) && !string.IsNullOrWhiteSpace(request.TargetStageKey))
				_ = ExecuteStageCommandAsync(() => _catalog.MergeStagesAsync(request.StageKey, request.TargetStageKey));
		});
		_commands.Register(AppCommandKind.MoveWindowToStage, request =>
		{
			if (_catalog is not null && request.Window is not null && !string.IsNullOrWhiteSpace(request.TargetStageKey))
				_ = ExecuteStageCommandAsync(() => _catalog.MoveWindowAsync(request.Window.Handle, request.TargetStageKey));
		});
		_commands.Register(AppCommandKind.ExtractWindow, request =>
		{
			if (_catalog is not null && request.Window is not null)
				_ = ExecuteStageCommandAsync(() => _catalog.ExtractWindowAsync(request.Window.Handle));
		});
		_commands.Register(AppCommandKind.UndoStageChange, request =>
		{
			if (_catalog is not null && _catalog.CanUndo)
				_ = ExecuteStageCommandAsync(_catalog.UndoAsync);
		});
		_commands.Register(AppCommandKind.TogglePinToAllStages, request =>
		{
			if (_catalog is not null && request.Window is not null)
				_ = ExecuteStageCommandAsync(() => _catalog.TogglePinAsync(request.Window.Handle));
		});
		_commands.Register(AppCommandKind.ExportDiagnostics, request =>
		{
			_ = ExportDiagnosticsAsync();
		});
		_commands.Register(AppCommandKind.Exit, _ => Close());
	}

	private async Task ExportDiagnosticsAsync()
	{
		using var dialog = new SaveFileDialog
		{
			Title = "Export Stage_Manager_Lai diagnostics",
			Filter = "ZIP archive (*.zip)|*.zip",
			AddExtension = true,
			DefaultExt = "zip",
			FileName = $"Stage_Manager_Lai-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
			return;

		try
		{
			var result = await _diagnostics.ExportAsync(dialog.FileName);
			_trayIcon?.ShowBalloonTip(4000, "Diagnostics exported", result.ArchivePath, ToolTipIcon.Info);
		}
		catch (Exception exception)
		{
			AppLogger.Error("The diagnostic bundle could not be exported.", exception);
			MessageBox.Show(this, exception.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private void RegisterHotkey(int id, string gesture)
	{
		if (!HotkeyGestureParser.TryParse(gesture, out var modifiers, out var virtualKey) ||
			!NativeMethods.RegisterHotKey(Handle, id, modifiers | ModNoRepeat, virtualKey))
		{
			_trayIcon?.ShowBalloonTip(3500, "Stage_Manager_Lai", $"The shortcut {gesture} is already in use or invalid.", ToolTipIcon.Warning);
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
		if (_renderer is null || _sidebarVisible == visible)
			return;
		_sidebarVisible = visible;
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
			ConfigurePointerPolling(IsLargeWindowActive());
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
		ConfigurePointerPolling(IsLargeWindowActive());
	}

	private void ActivateRelativeStage(int delta)
	{
		if (_catalog is null)
			return;
		SetSidebarVisible(true);
		_ = ExecuteStageCommandAsync(() => _catalog.SwitchRelativeAsync(delta));
	}

	private async Task ActivateWindowFromCommandAsync(AppCommandRequest request)
	{
		var window = request.Window;
		if (window is null || _catalog is null || _closing)
			return;

		var action = WindowClickBehavior.Decide(
			window.Handle,
			NativeMethods.GetForegroundWindow(),
			window.IsMinimized,
			NativeMethods.IsWindow(window.Handle));
		if (action == WindowClickAction.Ignore)
			return;
		if (request.AllowMinimize && action == WindowClickAction.Minimize)
		{
			NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwMinimize);
			RefreshStages();
			return;
		}

		if (!string.IsNullOrWhiteSpace(request.StageKey))
		{
			await _catalog.ActivateWindowAsync(request.StageKey, window.Handle).ConfigureAwait(true);
			if (!_closing)
				RefreshStages();
			return;
		}
		if (_closing)
			return;
		ActivateSelectedWindow(window, allowMinimize: false);
		RefreshStages();
	}

	private async Task ExecuteStageCommandAsync(Func<Task> action)
	{
		try
		{
			await action().ConfigureAwait(true);
			if (!_closing)
				RefreshStages();
		}
		catch (ObjectDisposedException) when (_closing)
		{
		}
		catch (Exception exception)
		{
			AppLogger.Error("A stage command failed.", exception);
		}
	}

	private static void ActivateSelectedWindow(StageManager.Native.Window.IWindow window, bool allowMinimize)
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
	}

	private void PollPointer()
	{
		if (_closing || _renderer is null || _catalog is null)
			return;
		var screenPoint = Cursor.Position;
		var nowUtc = DateTime.UtcNow;
		var pointerAtLeftEdge = IsNearLeftEdge(screenPoint);
		var largeWindowActive = IsLargeWindowActive();
		UpdateTransientSession(largeWindowActive, nowUtc);
		if (!_sidebarVisible)
		{
			var hiddenAction = TransientSidebarBehavior.Decide(
				largeWindowActive,
				false,
				pointerAtLeftEdge,
				false,
				_transientRevealUtc,
				nowUtc);
			if (hiddenAction == TransientSidebarAction.Reveal)
			{
				_transientRevealUtc = nowUtc;
				SetTransientOverlayRaised(true);
				SetSidebarVisible(true);
			}
			else if (!largeWindowActive && pointerAtLeftEdge)
				SetSidebarVisible(true);
			ConfigurePointerPolling(largeWindowActive);
			return;
		}

		var client = PointToClient(screenPoint);
		var wasExpanded = _renderer.HasExpandedStage;
		_renderer.PollPointer(client);
		var hit = _renderer.HitTest(client);
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

		var transientAction = TransientSidebarBehavior.Decide(
			largeWindowActive,
			true,
			pointerAtLeftEdge,
			pointerWithinTransientSidebar,
			_transientRevealUtc,
			nowUtc);
		if (transientAction == TransientSidebarAction.Hide)
		{
			SetSidebarVisible(false);
			ConfigurePointerPolling(largeWindowActive);
			return;
		}
		if (largeWindowActive)
		{
			if (pointerWithinTransientSidebar && !_transientOverlayRaised)
			{
				_transientRevealUtc = nowUtc;
				SetTransientOverlayRaised(true);
			}
			ConfigurePointerPolling(true);
			return;
		}

		var settings = _catalog.Settings.Current;
		if (SidebarIdleBehavior.ShouldHide(
			settings.IdleAutoHideEnabled,
			settings.IdleAutoHideSeconds,
			_lastSidebarInteractionUtc,
			nowUtc))
		{
			SetSidebarVisible(false);
			return;
		}
		ConfigurePointerPolling(false);
	}

	private bool IsLargeWindowActive() =>
		_catalog?.Settings.Current.FullScreenSidebarMode == FullScreenSidebarMode.EdgeReveal &&
		FullScreenService.UsesTransientSidebarOn(
			NativeMethods.GetForegroundWindow(),
			_sidebarDisplay);

	private void ConfigurePointerPolling(bool largeWindowActive)
	{
		if (_closing || _catalog is null)
			return;
		int? interval;
		if (!_sidebarVisible)
			interval = largeWindowActive ? 50 : 100;
		else if (largeWindowActive)
			interval = 50;
		else if (_catalog.Settings.Current.IdleAutoHideEnabled)
		{
			var dueUtc = _lastSidebarInteractionUtc +
				TimeSpan.FromSeconds(_catalog.Settings.Current.IdleAutoHideSeconds);
			interval = (int)Math.Clamp(
				Math.Ceiling((dueUtc - DateTime.UtcNow).TotalMilliseconds),
				50d,
				int.MaxValue);
		}
		else
			interval = null;

		_pointerTimer.Stop();
		if (interval is null)
			return;
		_pointerTimer.Interval = interval.Value;
		_pointerTimer.Start();
	}

	private void RecordSidebarInteraction()
	{
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		if (_catalog is not null)
			ConfigurePointerPolling(IsLargeWindowActive());
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
			return;
		}

		if (!_transientSession)
			return;
		_transientSession = false;
		SetTransientOverlayRaised(false);
		if (_sidebarWasVisibleBeforeTransientSession && !_sidebarVisible)
			SetSidebarVisible(true);
		_sidebarWasVisibleBeforeTransientSession = false;
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

	private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
	{
		if (_closing || !IsHandleCreated)
			return;
		try
		{
			BeginInvoke(new Action(() => ApplyRuntimeSettings(updateStartup: false)));
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void UpdateSidebarDisplay()
	{
		if (_closing || Screen.AllScreens.Length == 0)
			return;
		var settings = _catalog?.Settings.Current;
		var selected = settings is null
			? SidebarDisplayPolicy.SelectLeftmost(Screen.AllScreens, screen => screen.WorkingArea)
			: SidebarDisplayPolicy.Select(
				Screen.AllScreens,
				screen => screen.WorkingArea,
				screen => _displayIdentities.GetStableId(screen),
				screen => screen.Primary,
				settings.SidebarDisplayMode,
				settings.SidebarDisplayId);
		if (string.Equals(selected.DeviceName, _sidebarDisplay.DeviceName, StringComparison.OrdinalIgnoreCase) &&
			selected.WorkingArea == _sidebarScreenBounds)
			return;

		_sidebarDisplay = selected;
		_sidebarScreenBounds = selected.WorkingArea;
		_renderer?.SetSidebarDisplay(selected.DeviceName);
		Bounds = new Rectangle(
			selected.WorkingArea.Left,
			selected.WorkingArea.Top,
			Math.Min(900, selected.WorkingArea.Width),
			selected.WorkingArea.Height);
		UpdateWindowRegion(_sidebarVisible);
	}

	private void RecoverOffscreenWindowsAfterDisplayChange()
	{
		if (_catalog is null)
			return;
		var recovered = false;
		foreach (var window in _catalog.GetStages()
			.SelectMany(stage => stage.Windows)
			.GroupBy(window => window.Handle)
			.Select(group => group.First()))
		{
			recovered |= OffscreenWindowRecovery.TryRecoverToNearestDisplay(window);
		}
		if (recovered)
			BeginInvoke(new Action(RefreshStages));
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
			Text = AppVersionInfo.DisplayName,
			Icon = icon,
			ContextMenuStrip = _contextMenu,
			Visible = true
		};
		_trayIcon.MouseClick += (_, eventArgs) =>
		{
			if (eventArgs.Button == MouseButtons.Left)
				_commands.Execute(new(AppCommandKind.ToggleSidebar));
		};
	}

	private IReadOnlyList<PrototypeStageSnapshot> GetAccessibleStages() =>
		_catalog?.GetStages() ?? Array.Empty<PrototypeStageSnapshot>();

	private Rectangle GetAccessibleStageBounds(string stageKey)
	{
		var clientBounds = _renderer?.GetStageClientBounds(stageKey) ?? Rectangle.Empty;
		return clientBounds.IsEmpty ? Rectangle.Empty : RectangleToScreen(clientBounds);
	}

	private Rectangle GetAccessibleWindowBounds(IntPtr handle, string stageKey)
	{
		var clientBounds = _renderer?.GetWindowClientBounds(handle) ?? Rectangle.Empty;
		if (clientBounds.IsEmpty)
			return GetAccessibleStageBounds(stageKey);
		return RectangleToScreen(clientBounds);
	}

	private sealed class SidebarAccessibleObject : Control.ControlAccessibleObject
	{
		private readonly PrototypeForm _owner;

		public SidebarAccessibleObject(PrototypeForm owner) : base(owner) => _owner = owner;

		public override string? Name { get => "Stage Manager sidebar"; set { } }
		public override string? Description => "Application stages and their running windows.";
		public override AccessibleRole Role => AccessibleRole.List;
		public override int GetChildCount() => _owner.GetAccessibleStages().Count;

		public override AccessibleObject? GetChild(int index)
		{
			var stages = _owner.GetAccessibleStages();
			return index >= 0 && index < stages.Count
				? new StageAccessibleObject(_owner, this, stages[index].Key)
				: null;
		}
	}

	private sealed class StageAccessibleObject : AccessibleObject
	{
		private readonly PrototypeForm _owner;
		private readonly AccessibleObject _parent;
		private readonly string _stageKey;

		public StageAccessibleObject(PrototypeForm owner, AccessibleObject parent, string stageKey)
		{
			_owner = owner;
			_parent = parent;
			_stageKey = stageKey;
		}

		private PrototypeStageSnapshot? Stage => _owner.GetAccessibleStages().FirstOrDefault(stage =>
			string.Equals(stage.Key, _stageKey, StringComparison.OrdinalIgnoreCase));

		public override AccessibleObject? Parent => _parent;
		public override string? Name { get => Stage?.Title ?? "Stage"; set { } }
		public override string? Description
		{
			get
			{
				var stage = Stage;
				if (stage is null)
					return "Stage is no longer available.";
				var current = stage.IsCurrent ? " Current stage." : string.Empty;
				var pinned = stage.IsPinned ? " Contains a window pinned to all stages." : string.Empty;
				return $"{stage.Windows.Count} window{(stage.Windows.Count == 1 ? string.Empty : "s")}.{current}{pinned}";
			}
		}
		public override AccessibleRole Role => AccessibleRole.ListItem;
		public override AccessibleStates State => AccessibleStates.Selectable |
			AccessibleStates.Focusable |
			(Stage?.IsCurrent == true ? AccessibleStates.Selected : AccessibleStates.None);
		public override Rectangle Bounds => _owner.GetAccessibleStageBounds(_stageKey);
		public override string? DefaultAction => "Activate stage";
		public override int GetChildCount() => Stage?.Windows.Count ?? 0;

		public override AccessibleObject? GetChild(int index)
		{
			var stage = Stage;
			return stage is not null && index >= 0 && index < stage.Windows.Count
				? new WindowAccessibleObject(_owner, this, _stageKey, stage.Windows[index].Handle)
				: null;
		}

		public override void DoDefaultAction()
		{
			_owner._commands.Execute(new(AppCommandKind.ActivateStage, StageKey: _stageKey));
		}
	}

	private sealed class WindowAccessibleObject : AccessibleObject
	{
		private readonly PrototypeForm _owner;
		private readonly AccessibleObject _parent;
		private readonly string _stageKey;
		private readonly IntPtr _handle;

		public WindowAccessibleObject(PrototypeForm owner, AccessibleObject parent, string stageKey, IntPtr handle)
		{
			_owner = owner;
			_parent = parent;
			_stageKey = stageKey;
			_handle = handle;
		}

		private StageManager.Native.Window.IWindow? Window => _owner.GetAccessibleStages()
			.FirstOrDefault(stage => string.Equals(stage.Key, _stageKey, StringComparison.OrdinalIgnoreCase))?
			.Windows.FirstOrDefault(window => window.Handle == _handle);

		public override AccessibleObject? Parent => _parent;
		public override string? Name
		{
			get
			{
				var window = Window;
				return window is null ? "Window" : $"{window.ProcessName}: {window.Title}";
			}
			set { }
		}
		public override string? Description => DescribeCardState(_owner._renderer?.GetWindowStatus(_handle) ?? WindowCardState.None);
		public override AccessibleRole Role => AccessibleRole.PushButton;
		public override AccessibleStates State
		{
			get
			{
				var window = Window;
				var state = AccessibleStates.Selectable | AccessibleStates.Focusable;
				if (window?.IsFocused == true)
					state |= AccessibleStates.Focused;
				if (window?.IsMinimized == true || _owner.GetAccessibleWindowBounds(_handle, _stageKey).IsEmpty)
					state |= AccessibleStates.Offscreen;
				return state;
			}
		}
		public override Rectangle Bounds => _owner.GetAccessibleWindowBounds(_handle, _stageKey);
		public override string? DefaultAction => "Activate window";

		public override void DoDefaultAction()
		{
			if (Window is { } window)
				_owner._commands.Execute(new(AppCommandKind.ActivateWindow, Window: window, StageKey: _stageKey));
		}
	}
}

internal readonly record struct CardClickContext(
	IntPtr Handle,
	bool WasOffscreen,
	bool WasForeground,
	bool WasMinimized,
	bool WasMaximized);
