using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using StageManager.Settings;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype.Calendar;

internal sealed class CalendarCardVisual : IDisposable
{
    private readonly Compositor _compositor;
    private readonly D3DCompositionDevice _graphics;
    private readonly SpriteVisual _content;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionGeometricClip _clip;
    private CardSwapChain? _surface;
    private CompositionSurfaceBrush? _brush;
    private UiLanguage _language;
    private bool _enabled, _disposed;
    private float _dpi = 1;
    private string _paintKey = "";

    public CalendarCardVisual(Compositor compositor, D3DCompositionDevice graphics)
    {
        _compositor = compositor; _graphics = graphics;
        Root = compositor.CreateContainerVisual();
        _content = compositor.CreateSpriteVisual();
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _clip = compositor.CreateGeometricClip(_geometry);
        _content.Clip = _clip;
        Root.Children.InsertAtTop(_content);
        SetLayout(1, 196);
        Root.IsVisible = false;
    }

    public ContainerVisual Root { get; }
    public Vector2 Size { get; private set; }
    public Vector2 Pivot { get; private set; }
    public float Angle { get; private set; }
    public void SetLayout(float dpiScale, float width, float height = 0)
    {
        var dpi = Math.Max(.75f, dpiScale);
        var size = new Vector2(Math.Max(64 * dpi, width), Math.Max(36 * dpi, height));
        var changed = size != Size || dpi != _dpi;
        Size = size; _dpi = dpi; Angle = height > 0 ? -4 : -7.5f;
        Root.Size = size; Pivot = new(size.X * .88f, size.Y / 2);
        Root.CenterPoint = new(Pivot, 0); Root.RotationAxis = Vector3.UnitY;
        Root.RotationAngleInDegrees = Angle;
        _content.Size = size; _geometry.Size = size; _geometry.CornerRadius = new(8 * dpi);
        if (changed) ReleaseSurface();
        Redraw();
    }
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled) Redraw(); else { Root.IsVisible = false; ReleaseSurface(); }
    }
    public void RefreshDate() => Redraw();
    public void SetLanguage(UiLanguage language) { _language = language; Redraw(); }
    public void SetVisible(bool visible) => Root.IsVisible = visible && _enabled;
    public void SetOffset(Vector3 offset) => Root.Offset = offset;
    public void SetHovered(bool hovered) => _content.Opacity = hovered ? 1 : .95f;
    private void Redraw()
    {
        if (!_enabled || _disposed) return;
        var now = DateTimeOffset.Now;
        var textureSize = CalendarPresentation.DateTextureSize(new((int)Math.Ceiling(Size.X), (int)Math.Ceiling(Size.Y)));
        var key = CalendarPresentation.RenderKey(textureSize, _language, now);
        if (_surface is not null && _paintKey == key) return;
        var width = textureSize.Width;
        var height = textureSize.Height;
        if (_surface is null)
        {
            _surface = _graphics.CreateSurface(_compositor, width, height);
            _brush = _compositor.CreateSurfaceBrush(_surface.CompositionSurface);
            _brush.Stretch = CompositionStretch.Fill; _content.Brush = _brush;
        }
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap)) CalendarPresentation.PaintCard(g, bitmap.Size, _language, now);
        var data = bitmap.LockBits(new(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[checked(width * height * 4)];
            for (var row = 0; row < height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * width * 4, width * 4);
            _surface.Upload(pixels); _paintKey = key;
        }
        finally { bitmap.UnlockBits(data); }
    }
    private void ReleaseSurface()
    {
        _content.Brush = null; _brush?.Dispose(); _brush = null;
        _surface?.Dispose(); _surface = null; _paintKey = "";
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ReleaseSurface(); _content.Dispose(); _clip.Dispose(); _geometry.Dispose(); Root.Dispose();
    }
}
