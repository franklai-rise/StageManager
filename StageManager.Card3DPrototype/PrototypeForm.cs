using StageManager.Services;
using StageManager.Settings;
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
	private readonly ToolTip _toolTip = new() { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 3000, ShowAlways = true };
	private readonly ContextMenuStrip _contextMenu = new();
	private readonly HashSet<int> _registeredHotkeys = new();
	private readonly Rectangle _sidebarScreenBounds;
	private Compositor? _compositor;
	private DesktopWindowTarget? _target;
	private ContainerVisual? _root;
	private PrototypeStageCatalog? _catalog;
	private CompositionStageRenderer? _renderer;
	private NotifyIcon? _trayIcon;
	private IntPtr _toolTipHandle;
	private DateTime _lastSidebarInteractionUtc = DateTime.UtcNow;
	private bool _sidebarVisible = true;
	private bool _closing;

	public PrototypeForm()
	{
		Text = "Stage_Manager_Lai";
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		TopMost = false;
		var screen = Screen.AllScreens.OrderBy(item => item.WorkingArea.Left).First();
		_sidebarScreenBounds = screen.WorkingArea;
		Bounds = new Rectangle(screen.WorkingArea.Left, screen.WorkingArea.Top, Math.Min(900, screen.WorkingArea.Width), screen.WorkingArea.Height);
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

		var toggleItem = new ToolStripMenuItem("Show / Hide sidebar");
		toggleItem.Click += (_, _) => ToggleSidebarVisibility();
		var settingsItem = new ToolStripMenuItem("Settings...");
		settingsItem.Click += (_, _) => ShowSettings();
		var exitItem = new ToolStripMenuItem("Exit Stage_Manager_Lai");
		exitItem.Click += (_, _) => Close();
		_contextMenu.Items.Add(new ToolStripMenuItem("Stage_Manager_Lai v2.2.1") { Enabled = false });
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(toggleItem);
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
		};
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
				_catalog.Settings.Current.AnimationsEnabled);
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
		if (target.Window is not null && target.Window.Handle != _toolTipHandle)
		{
			_toolTipHandle = target.Window.Handle;
			_toolTip.Show(target.Window.Title, this, e.X + 16, e.Y + 14, 2600);
		}
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		_lastSidebarInteractionUtc = DateTime.UtcNow;
		if (e.Button == MouseButtons.Right)
		{
			_contextMenu.Show(Cursor.Position);
			return;
		}
		if (e.Button != MouseButtons.Left || _renderer is null)
			return;
		var window = _renderer.ActivateAt(e.Location);
		if (window is null)
			return;
		ActivateSelectedWindow(window, allowMinimize: true);
		BeginInvoke(new Action(RefreshStages));
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
		_regionCollapseTimer.Stop();
		_stageTimer.Dispose();
		_pointerTimer.Dispose();
		_regionCollapseTimer.Dispose();
		_toolTip.Dispose();
		_contextMenu.Dispose();
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
		_renderer.Synchronize(_catalog.GetStages());
		if (_sidebarVisible)
			UpdateWindowRegion(true);
	}

	private void Settings_SettingsChanged(object? sender, EventArgs e) => ApplyRuntimeSettings(updateStartup: true);

	private void ApplyRuntimeSettings(bool updateStartup)
	{
		if (_catalog is null || _renderer is null)
			return;
		var settings = _catalog.Settings.Current;
		_renderer.SetAnimationsEnabled(settings.AnimationsEnabled);
		_renderer.SetCardScale(settings.CardScale);
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
		using var dialog = new SettingsForm(_catalog.Settings.CloneCurrent());
		if (dialog.ShowDialog() == DialogResult.OK)
			_catalog.Settings.Apply(dialog.Draft);
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
		_regionCollapseTimer.Stop();
		if (visible)
		{
			_lastSidebarInteractionUtc = DateTime.UtcNow;
			UpdateWindowRegion(true);
			NativeMethods.SetWindowPos(
				Handle,
				NativeMethods.HwndTop,
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
		var animate = _catalog?.Settings.Current.AnimationsEnabled == true;
		if (animate)
			_regionCollapseTimer.Start();
		else
			UpdateWindowRegion(false);
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
		NativeMethods.BringWindowToTop(window.Handle);
		NativeMethods.SetForegroundWindow(window.Handle);
		if (NativeMethods.GetForegroundWindow() != window.Handle)
			window.Focus();
	}

	private void PollPointer()
	{
		if (_closing || _renderer is null || _catalog is null)
			return;
		var screenPoint = Cursor.Position;
		if (!_sidebarVisible)
		{
			if (IsNearLeftEdge(screenPoint))
				SetSidebarVisible(true);
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
		if (hit is not null || pointerNearSidebar)
			_lastSidebarInteractionUtc = DateTime.UtcNow;
		else
		{
			_toolTipHandle = IntPtr.Zero;
			_toolTip.Hide(this);
		}
		if (wasExpanded != _renderer.HasExpandedStage)
			UpdateWindowRegion(true);

		var settings = _catalog.Settings.Current;
		if (SidebarIdleBehavior.ShouldHide(
			settings.IdleAutoHideEnabled,
			settings.IdleAutoHideSeconds,
			_lastSidebarInteractionUtc,
			DateTime.UtcNow))
		{
			SetSidebarVisible(false);
		}
	}

	private bool IsNearLeftEdge(Point screenPoint)
	{
		return SidebarIdleBehavior.IsNearLeftEdge(screenPoint, _sidebarScreenBounds, EdgeActivationWidth);
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
			Text = "Stage_Manager_Lai v2.2.1",
			Icon = icon,
			ContextMenuStrip = _contextMenu,
			Visible = true
		};
	}
}
