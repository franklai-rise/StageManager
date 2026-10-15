using System.Numerics;
using StageManager.Card3DPrototype;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Settings;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

internal sealed class NativeAiPreview : Form
{
	private readonly DispatcherQueueHelper _queue = new();
	private readonly AiUsageService _service = new(dataDirectory: Path.Combine(AppContext.BaseDirectory, "preview-data"));
	private readonly System.Windows.Forms.Timer _pointer = new() { Interval = 50 };
	private readonly AiUsagePopupController _hover = new();
	private Compositor? _compositor;
	private D3DCompositionDevice? _device;
	private DesktopWindowTarget? _target;
	private ContainerVisual? _root, _camera;
	private AiUsageCardVisual? _card;
	private SpriteVisual? _background;
	private CompositionColorBrush? _brush;
	private AiUsageDetailsForm? _popup;
	private Rectangle _cardBounds;
	public NativeAiPreview()
	{
		Text = "StageManager — native 3D card verification";
		StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(650, 650);
		_pointer.Tick += (_, _) => Poll();
	}
	protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x00200000; return p; } }
	protected override async void OnShown(EventArgs e)
	{
		base.OnShown(e);
		_queue.EnsureDispatcherQueue();
		_compositor = new Compositor(); _device = new D3DCompositionDevice(true);
		_target = CompositionInterop.CreateDesktopWindowTarget(_compositor, Handle, false);
		_root = _compositor.CreateContainerVisual(); _root.Size = new Vector2(ClientSize.Width, ClientSize.Height); _target.Root = _root;
		_background = _compositor.CreateSpriteVisual(); _background.Size = _root.Size;
		_brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 38, 92, 129)); _background.Brush = _brush; _root.Children.InsertAtBottom(_background);
		_camera = _compositor.CreateContainerVisual(); _camera.Size = _root.Size;
		var matrix = Matrix4x4.Identity; matrix.M34 = -1f / 1200; _camera.TransformMatrix = matrix;
		_root.Children.InsertAtTop(_camera);
		_card = new AiUsageCardVisual(_compositor, _device); _card.SetLanguage(UiLanguage.SimplifiedChinese);
		_card.SetLayout(DeviceDpi / 96f, 196 * .59f * DeviceDpi / 96f); _card.SetOffset(new Vector3(28, 90, 0));
		_cardBounds = new Rectangle(28, 90, (int)_card.Size.X, (int)_card.Size.Y);
		_camera.Children.InsertAtTop(_card.Root);
		var config = new AppSettings { AiUsageKimiEnabled = true };
		_service.Configure(config); _card.SetSnapshots(_service.Current); _pointer.Start();
		await _service.RefreshAsync(config, false);
		if (!IsDisposed) { _card?.SetSnapshots(_service.Current); _popup?.SetSnapshots(_service.Current); }
	}
	private void Poll()
	{
		if (_card is null) return;
		var point = PointToClient(Cursor.Position); var onCard = _cardBounds.Contains(point);
		_card.SetHovered(onCard);
		var open = _popup is { IsDisposed: false };
		if (open && (NativeMethods.GetAsyncKeyState(0x1b) & 0x8001) != 0)
		{ _hover.Dismiss(); _popup!.Close(); _popup = null; return; }
		switch (_hover.Update(onCard, _popup?.IsPointerInside(Cursor.Position) == true, open, DateTime.UtcNow))
		{
			case AiPopupAction.Open: OpenPanel(); break;
			case AiPopupAction.KeepOpen: _popup?.KeepOpen(); break;
			case AiPopupAction.FadeOut: _popup?.BeginFadeOut(); break;
		}
	}
	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		if (_cardBounds.Contains(e.Location)) { _hover.Click(); OpenPanel(); }
		else if (_popup is not null) { _hover.Dismiss(); _popup.Close(); _popup = null; }
	}
	private void OpenPanel()
	{
		if (_popup is { IsDisposed: false }) return;
		_popup = new AiUsageDetailsForm(_service, UiLanguage.SimplifiedChinese);
		_ = NativeMethods.GetAsyncKeyState(0x1b);
		_popup.SetSnapshots(_service.Current);
		_popup.Location = PointToScreen(new Point(_cardBounds.Right + 10, _cardBounds.Top));
		_popup.CloseRequested += (_, _) => { _hover.Dismiss(); _popup.Close(); _popup = null; };
		_popup.Show(this);
	}
	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_pointer.Stop(); _pointer.Dispose(); _popup?.Dispose(); _service.Dispose();
		_card?.Dispose(); _background?.Dispose(); _brush?.Dispose(); _camera?.Dispose(); _root?.Dispose();
		_target?.Dispose(); _device?.Dispose(); _compositor?.Dispose(); _queue.Dispose();
		base.OnFormClosed(e);
	}
}

