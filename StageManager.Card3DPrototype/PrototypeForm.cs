using System.Numerics;
using System.Windows.Forms;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace StageManager.Card3DPrototype;

internal sealed class PrototypeForm : Form
{
	private const int WsExToolWindow = 0x00000080;
	private const int WsExTransparent = 0x00000020;
	private const int WsExNoActivate = 0x08000000;
	private const int WsExNoRedirectionBitmap = 0x00200000;
	private const int GwlExStyle = -20;
	private readonly DispatcherQueueHelper _dispatcherQueue = new();
	private readonly System.Windows.Forms.Timer _stageTimer = new() { Interval = 500 };
	private readonly System.Windows.Forms.Timer _pointerTimer = new() { Interval = 50 };
	private readonly ToolTip _toolTip = new() { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 3000, ShowAlways = true };
	private readonly ContextMenuStrip _contextMenu = new();
	private Compositor? _compositor;
	private DesktopWindowTarget? _target;
	private ContainerVisual? _root;
	private PrototypeStageCatalog? _catalog;
	private CompositionStageRenderer? _renderer;
	private NotifyIcon? _trayIcon;
	private IntPtr _toolTipHandle;
	private bool _clickThroughEnabled = true;
	private bool _closing;

	public PrototypeForm()
	{
		Text = "Stage_Manager_Lai";
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		TopMost = false;
		var screen = Screen.AllScreens.OrderBy(item => item.WorkingArea.Left).First();
		Bounds = new Rectangle(screen.WorkingArea.Left, screen.WorkingArea.Top, Math.Min(900, screen.WorkingArea.Width), screen.WorkingArea.Height);
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

		var settingsItem = new ToolStripMenuItem("Card size...");
		settingsItem.Click += (_, _) => ShowCardSizeSettings();
		var exitItem = new ToolStripMenuItem("Exit Stage_Manager_Lai");
		exitItem.Click += (_, _) => Close();
		_contextMenu.Items.Add(new ToolStripMenuItem("Stage_Manager_Lai v2.2.0") { Enabled = false });
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(settingsItem);
		_contextMenu.Items.Add(new ToolStripSeparator());
		_contextMenu.Items.Add(exitItem);
		_stageTimer.Tick += (_, _) => RefreshStages();
		_pointerTimer.Tick += (_, _) => PollPointer();
	}

	protected override bool ShowWithoutActivation => true;

	protected override CreateParams CreateParams
	{
		get
		{
			var parameters = base.CreateParams;
			parameters.ExStyle |= WsExToolWindow | WsExTransparent | WsExNoActivate | WsExNoRedirectionBitmap;
			return parameters;
		}
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
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
			_renderer = new CompositionStageRenderer(this, _compositor, _root, _catalog.Settings.Current.CardScale);
			_renderer.Resize(ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
			RefreshStages();
			CreateTrayIcon();
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
		// The Composition visual tree owns every visible pixel. Keeping the HWND background untouched preserves transparency.
	}

	protected override void OnResize(EventArgs e)
	{
		base.OnResize(e);
		_renderer?.Resize(ClientSize.Width, ClientSize.Height, DeviceDpi / 96f);
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if (_renderer is null)
			return;
		var initialTarget = _renderer.HitTest(e.Location);
		if (initialTarget is null)
		{
			SetClickThrough(true);
			return;
		}
		SetClickThrough(false);
		var wasExpanded = _renderer.HasExpandedStage;
		_renderer.UpdatePointer(e.Location);
		if (!wasExpanded && _renderer.HasExpandedStage)
			NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTop, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

		var target = _renderer.HitTest(e.Location) ?? initialTarget;
		if (target?.Window is not null && target.Window.Handle != _toolTipHandle)
		{
			_toolTipHandle = target.Window.Handle;
			_toolTip.Show(target.Window.Title, this, e.X + 16, e.Y + 14, 2600);
		}
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
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
		ToggleSelectedWindow(window);
		BeginInvoke(new Action(RefreshStages));
	}

	protected override void OnMouseWheel(MouseEventArgs e)
	{
		base.OnMouseWheel(e);
		_renderer?.Scroll(e.Delta);
	}

	protected override void WndProc(ref Message message)
	{
		const int wmNcHitTest = 0x0084;
		const int htClient = 1;
		const int htTransparent = -1;
		const int wmMouseActivate = 0x0021;
		const int maNoActivate = 3;
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
			message.Result = (IntPtr)(_renderer.HitTest(client) is null ? htTransparent : htClient);
			return;
		}
		base.WndProc(ref message);
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_closing = true;
		_stageTimer.Stop();
		_pointerTimer.Stop();
		_stageTimer.Dispose();
		_pointerTimer.Dispose();
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
		_dispatcherQueue.Dispose();
		base.OnFormClosed(e);
	}

	private void RefreshStages()
	{
		if (_closing || _catalog is null || _renderer is null)
			return;
		_renderer.Synchronize(_catalog.GetStages());
	}

	private void Settings_SettingsChanged(object? sender, EventArgs e)
	{
		if (_catalog is null || _renderer is null)
			return;
		_renderer.SetCardScale(_catalog.Settings.Current.CardScale);
	}

	private void ShowCardSizeSettings()
	{
		if (_catalog is null)
			return;
		using var dialog = new CardSizeSettingsForm(_catalog.Settings.Current.CardScale);
		if (dialog.ShowDialog(this) != DialogResult.OK)
			return;
		var settings = _catalog.Settings.CloneCurrent();
		settings.CardScale = dialog.CardScale;
		_catalog.Settings.Apply(settings);
	}

	private static void ToggleSelectedWindow(StageManager.Native.Window.IWindow window)
	{
		var action = WindowClickBehavior.Decide(
			window.Handle,
			NativeMethods.GetForegroundWindow(),
			window.IsMinimized,
			NativeMethods.IsWindow(window.Handle));
		if (action == WindowClickAction.Ignore)
			return;
		if (action == WindowClickAction.Minimize)
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
		if (_closing || _renderer is null)
			return;
		var client = PointToClient(Cursor.Position);
		_renderer.PollPointer(client);
		var hit = _renderer.HitTest(client);
		SetClickThrough(hit is null);
		if (hit is null)
		{
			_toolTipHandle = IntPtr.Zero;
			_toolTip.Hide(this);
		}
	}

	private void SetClickThrough(bool enabled)
	{
		if (!IsHandleCreated || _clickThroughEnabled == enabled)
			return;
		_clickThroughEnabled = enabled;
		var extendedStyle = NativeMethods.GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
		var updatedStyle = enabled
			? extendedStyle | WsExTransparent
			: extendedStyle & ~((long)WsExTransparent);
		if (updatedStyle == extendedStyle)
			return;
		NativeMethods.SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(updatedStyle));
		NativeMethods.SetWindowPos(
			Handle,
			IntPtr.Zero,
			0,
			0,
			0,
			0,
			NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
			NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
	}

	private void CreateTrayIcon()
	{
		var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
		_trayIcon = new NotifyIcon
		{
			Text = "Stage_Manager_Lai v2.2.0",
			Icon = icon,
			ContextMenuStrip = _contextMenu,
			Visible = true
		};
	}
}
