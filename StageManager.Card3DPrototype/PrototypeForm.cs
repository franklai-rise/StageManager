using StageManager.Services;
using StageManager.Settings;
using Microsoft.Win32;
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
	private readonly DispatcherQueueHelper _dispatcherQueue = new();
	private readonly System.Windows.Forms.Timer _stageTimer = new() { Interval = 500 };
	private readonly System.Windows.Forms.Timer _pointerTimer = new() { Interval = 50 };
	private readonly System.Windows.Forms.Timer _regionCollapseTimer = new() { Interval = 260 };
	private readonly System.Windows.Forms.Timer _displayChangeTimer = new() { Interval = 250 };
	private readonly System.Windows.Forms.Timer _previewReleaseTimer = new() { Interval = 30000 };
	private readonly System.Threading.Timer _hiddenEdgeTimer;
	private readonly ToolTip _toolTip = new() { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 3000, ShowAlways = true };
	private readonly ContextMenuStrip _contextMenu = new();
	private readonly ContextMenuStrip _cardContextMenu = new();
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
	private volatile bool _sidebarVisible = true;
	private bool _transientSession;
	private bool _edgeRevealSession;
	private bool _sidebarWasVisibleBeforeTransientSession;
	private bool _transientOverlayRaised;
	private bool _demoteOverlayAfterHide;
	private volatile bool _closing;
	private int _hiddenEdgeUiRequestPending;
	private CardClickContext? _lastCardClick;

	public PrototypeForm()
	{
		_hiddenEdgeTimer = new(_ => PollHiddenEdgeFromBackground(), null, Timeout.Infinite, Timeout.Infinite);
		Text = "Stage_Manager_Lai";
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		TopMost = false;
		_sidebarDisplay = SidebarDisplayPolicy.SelectLeftmost(Screen.AllScreens, screen => screen.WorkingArea);
		_sidebarScreenBounds = _sidebarDisplay.WorkingArea;
		Bounds = new Rectangle(_sidebarDisplay.WorkingArea.Left, _sidebarDisplay.WorkingArea.Top, Math.Min(900, _sidebarDisplay.WorkingArea.Width), _sidebarDisplay.WorkingArea.Height);
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

		var toggleItem = new ToolStripMenuItem("Show / Hide sidebar");
		toggleItem.Click += (_, _) => ToggleSidebarVisibility();
		var settingsItem = new ToolStripMenuItem("Settings...");
		settingsItem.Click += (_, _) => ShowSettings();
		var refreshItem = new ToolStripMenuItem("Refresh all previews now");
		refreshItem.Click += (_, _) => _renderer?.RefreshAllPreviews();
		var exitItem = new ToolStripMenuItem("Exit Stage_Manager_Lai");
		exitItem.Click += (_, _) => Close();
		_contextMenu.Items.Add(new ToolStripMenuItem("Stage_Manager_Lai v2.5.1") { Enabled = false });
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(toggleItem);
		_contextMenu.Items.Add(refreshItem);
		_contextMenu.Items.Add(settingsItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(exitItem);
		_stageTimer.Tick += (_, _) => RefreshStages();
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
			return;

		_lastSidebarInteractionUtc = DateTime.UtcNow;
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
		_lastSidebarInteractionUtc = DateTime.UtcNow;
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
			HandleCardDoubleClick(clickTarget);
			return;
		}
		_lastCardClick = clickTarget?.Window is { } clickedWindow
			? new CardClickContext(
				clickedWindow.Handle,
				OffscreenWindowRecovery.IsOffscreen(clickedWindow),
				clickedWindow.Handle == NativeMethods.GetForegroundWindow(),
				NativeMethods.IsIconic(clickedWindow.Handle),
				clickedWindow.IsMaximized)
			: null;
		var wasExpanded = _renderer.HasExpandedStage;
		var window = _renderer.ActivateAt(e.Location);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
		if (wasExpanded != _renderer.HasExpandedStage)
			UpdateWindowRegion(true);
		if (_renderer.ConsumeSidebarCollapseRequest())
		{
			SetSidebarVisible(false);
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
		UnregisterHotkeys();
		_stageTimer.Stop();
		_pointerTimer.Stop();
		_hiddenEdgeTimer.Change(Timeout.Infinite, Timeout.Infinite);
		_regionCollapseTimer.Stop();
		_displayChangeTimer.Stop();
		_previewReleaseTimer.Stop();
		_stageTimer.Dispose();
		_pointerTimer.Dispose();
		_hiddenEdgeTimer.Dispose();
		_regionCollapseTimer.Dispose();
		_displayChangeTimer.Dispose();
		_previewReleaseTimer.Dispose();
		_toolTip.Dispose();
		_contextMenu.Dispose();
		_cardContextMenu.Dispose();
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
		var previousRevision = _renderer.LayoutRevision;
		_renderer.Synchronize(_catalog.GetStages());
		if (_sidebarVisible && previousRevision != _renderer.LayoutRevision)
			UpdateWindowRegion(true);
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
		_renderer.SetPreviewPolicy(settings.PreviewRefreshMinutes, settings.PausePreviewRefreshWhenHidden);
		_renderer.SetIdleHint(settings.IdleAutoHideEnabled, settings.IdleAutoHideSeconds);
		RegisterHotkeys();
		if (!settings.IdleAutoHideEnabled && !_sidebarVisible)
			SetSidebarVisible(true);
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
		if (target.Window is { } window)
		{
			var activateItem = new ToolStripMenuItem("Bring this window to front");
			activateItem.Click += (_, _) => ActivateSelectedWindow(window, allowMinimize: false);
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
		}

		var refreshItem = new ToolStripMenuItem("Refresh preview now");
		refreshItem.Click += (_, _) => _renderer.RefreshStagePreviews(target.StageKey);
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
		if (target.PageDelta < 0)
			return "Previous windows";
		if (target.PageDelta > 0)
			return "Next windows";
		if (target.Window is { } window)
		{
			var state = window.IsMinimized ? " (minimized)" : string.Empty;
			return $"{window.Title}{state}\nDouble-click to recover an off-screen window · Right-click for options";
		}

		var stage = _catalog?.GetStages().FirstOrDefault(snapshot =>
			string.Equals(snapshot.Key, target.StageKey, StringComparison.OrdinalIgnoreCase));
		return stage is null
			? "Application group"
			: $"{stage.Title} · {stage.Windows.Count} windows\nClick to expand or collapse · Right-click for options";
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
		if (visible)
		{
			_hiddenEdgeTimer.Change(Timeout.Infinite, Timeout.Infinite);
			_pointerTimer.Start();
		}
		else
		{
			_edgeRevealSession = false;
			_pointerTimer.Stop();
			var largeWindowActive = FullScreenService.UsesTransientSidebarOn(
				NativeMethods.GetForegroundWindow(),
				_sidebarDisplay);
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
		var largeWindowActive = FullScreenService.UsesTransientSidebarOn(
			NativeMethods.GetForegroundWindow(),
			_sidebarDisplay);
		UpdateTransientSession(largeWindowActive, nowUtc);
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
		if (SidebarIdleBehavior.ShouldHide(
			settings.IdleAutoHideEnabled,
			settings.IdleAutoHideSeconds,
			_lastSidebarInteractionUtc,
			nowUtc))
		{
			SetSidebarVisible(false);
		}
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

	private void UpdateSidebarDisplay()
	{
		if (_closing || Screen.AllScreens.Length == 0)
			return;
		var selected = SidebarDisplayPolicy.SelectLeftmost(Screen.AllScreens, screen => screen.WorkingArea);
		if (string.Equals(selected.DeviceName, _sidebarDisplay.DeviceName, StringComparison.OrdinalIgnoreCase) &&
			selected.WorkingArea == _sidebarScreenBounds)
			return;

		_sidebarDisplay = selected;
		_sidebarScreenBounds = selected.WorkingArea;
		Bounds = new Rectangle(
			selected.WorkingArea.Left,
			selected.WorkingArea.Top,
			Math.Min(900, selected.WorkingArea.Width),
			selected.WorkingArea.Height);
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
			Text = "Stage_Manager_Lai v2.5.0",
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

internal readonly record struct CardClickContext(
	IntPtr Handle,
	bool WasOffscreen,
	bool WasForeground,
	bool WasMinimized,
	bool WasMaximized);
