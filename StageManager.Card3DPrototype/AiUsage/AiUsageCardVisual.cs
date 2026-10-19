using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using StageManager.Settings;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype.AiUsage;

internal sealed class AiUsageCardVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly D3DCompositionDevice _graphics;
	private readonly SpriteVisual _background, _content;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionGeometricClip _clip;
	private readonly AiProviderIcons _icons = new();
	private CardSwapChain? _surface;
	private CompositionSurfaceBrush? _surfaceBrush;
	private IReadOnlyList<AiUsageSnapshot> _snapshots = Array.Empty<AiUsageSnapshot>();
	private string _paintKey = "";
	private bool _hovered, _disposed;
	private bool _enabled = true;
	private float _dpi = 1;
	private float _minimumHeight;
	private UiLanguage _language;

	public AiUsageCardVisual(Compositor compositor, D3DCompositionDevice graphics)
	{
		_compositor = compositor; _graphics = graphics;
		Root = compositor.CreateContainerVisual();
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(155, 22, 27, 36));
		_background = compositor.CreateSpriteVisual(); _background.Brush = _backgroundBrush;
		_geometry = compositor.CreateRoundedRectangleGeometry();
		_clip = compositor.CreateGeometricClip(_geometry); _background.Clip = _clip;
		_content = compositor.CreateSpriteVisual();
		Root.Children.InsertAtBottom(_background); Root.Children.InsertAtTop(_content);
		SetLayout(1, 196);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle { get; private set; } = -7.5f;
	public float NaturalHeight => AiUsagePresentation.Height(_snapshots);
	public float ContentTop => Math.Max(0, (Size.Y - NaturalHeight * _dpi) / 2f);
	public float ContentHeight => NaturalHeight * _dpi;
	public void SetLanguage(UiLanguage language)
	{
		if (_language == language) return;
		_language = language; _paintKey = ""; Redraw();
	}
	public void SetLayout(float dpiScale, float cardWidth, float cardHeight = 0)
	{
		Angle = cardHeight > 0 ? -4f : -7.5f;
		var dpi = Math.Max(.75f, dpiScale);
		_minimumHeight = cardHeight;
		var size = new Vector2(Math.Max(64 * dpi, cardWidth), Math.Max(cardHeight, NaturalHeight * dpi));
		var changed = Size != size || _dpi != dpi;
		_dpi = dpi; Size = size; Root.Size = size;
		Root.CenterPoint = new Vector3(size.X * .88f, size.Y / 2, 0);
		Root.RotationAxis = Vector3.UnitY; Root.RotationAngleInDegrees = Angle;
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		_background.Size = size; _geometry.Size = size; _geometry.CornerRadius = new Vector2(8 * dpi);
		if (changed) { ReleaseSurface(); _paintKey = ""; }
		Redraw();
	}
	public void SetSnapshots(IReadOnlyList<AiUsageSnapshot> snapshots)
	{
		_snapshots = snapshots.ToArray(); SetLayout(_dpi, Size.X, _minimumHeight);
	}
	public void SetEnabled(bool enabled)
	{
		if (_enabled == enabled) return;
		_enabled = enabled;
		if (enabled) Redraw(); else ReleaseSurface();
	}
	public void SetOffset(Vector3 offset) => Root.Offset = offset;
	public void SetVisible(bool visible) => Root.IsVisible = visible;
	public void SetHovered(bool hovered)
	{
		if (_hovered == hovered) return;
		_hovered = hovered;
		_backgroundBrush.Color = Windows.UI.Color.FromArgb((byte)(hovered ? 190 : 155), 22, 27, 36);
	}
	private void Redraw()
	{
		if (_disposed || !_enabled) return;
		var key = _language + "|" + string.Join("|", _snapshots.Select(s => $"{s.ProviderId}:{AiUsagePresentation.Compact(s, _language)}:{s.Status}:" + string.Join(",", s.Windows?.Select(w => $"{w.LimitId}:{w.RemainingPercent}") ?? Array.Empty<string>())));
		if (_surface is not null && key == _paintKey) return;
		var width = Math.Max(1, (int)Math.Ceiling(Size.X));
		var height = Math.Max(1, (int)Math.Ceiling(Size.Y));
		if (_surface is null)
		{
			_surface = _graphics.CreateSurface(_compositor, width, height);
			_surfaceBrush = _compositor.CreateSurfaceBrush(_surface.CompositionSurface);
			_surfaceBrush.Stretch = CompositionStretch.None;
			_content.Brush = _surfaceBrush; _content.Size = Size;
		}
		using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
		using (var g = Graphics.FromImage(bitmap))
		{
			g.Clear(Color.Transparent);
			g.TranslateTransform(0, ContentTop);
			AiUsagePresentation.PaintCard(g, new Size(width, (int)Math.Ceiling(ContentHeight)), _dpi, _snapshots, _language, _icons);
		}
		var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
		try
		{
			var pixels = new byte[width * height * 4];
			for (var row = 0; row < height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * width * 4, width * 4);
			_surface.Upload(pixels); _paintKey = key;
		}
		finally { bitmap.UnlockBits(data); }
	}
	private void ReleaseSurface()
	{
		_content.Brush = null;
		_surfaceBrush?.Dispose(); _surfaceBrush = null;
		_surface?.Dispose(); _surface = null;
	}
	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		ReleaseSurface(); _icons.Dispose(); _content.Dispose(); _clip.Dispose(); _geometry.Dispose();
		_background.Dispose(); _backgroundBrush.Dispose(); Root.Dispose();
	}
}
