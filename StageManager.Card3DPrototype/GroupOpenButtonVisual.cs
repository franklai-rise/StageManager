using System.Numerics;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

// Vector-only companion to FIX. As a child of GroupCard it inherits the exact
// perspective, scale and rotation of the card, with no separate raster texture.
internal sealed class GroupOpenButtonVisual : IDisposable
{
    private readonly SpriteVisual _background;
    private readonly CompositionColorBrush _backgroundBrush, _ink;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionGeometricClip _clip;
    private readonly ContainerVisual _label;
    private readonly List<SpriteVisual> _strokes = new();
    private bool _hovered, _pressed, _busy;
    public ContainerVisual Root { get; }
    public Vector2 Size { get; private set; }
    public bool Busy => _busy;

    public GroupOpenButtonVisual(Compositor compositor)
    {
        Root = compositor.CreateContainerVisual(); Root.IsVisible = false;
        _backgroundBrush = compositor.CreateColorBrush();
        _ink = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 241, 245, 252));
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _clip = compositor.CreateGeometricClip(_geometry);
        _background = compositor.CreateSpriteVisual(); _background.Brush = _backgroundBrush; _background.Clip = _clip;
        _label = compositor.CreateContainerVisual();
        Root.Children.InsertAtTop(_background); Root.Children.InsertAtTop(_label);
        void Stroke(float x, float y, float width, float height, float angle = 0)
        {
            var line = compositor.CreateSpriteVisual(); line.Brush = _ink;
            line.Size = new(width, height); line.Offset = new(x, y, 0);
            line.CenterPoint = new(width / 2, height / 2, 0); line.RotationAngleInDegrees = angle;
            _label.Children.InsertAtTop(line); _strokes.Add(line);
        }
        const float t = 1.5f;
        // O P E N, matching the thin vector lettering of FIX.
        Stroke(0, 0, 7, t); Stroke(0, 10-t, 7, t); Stroke(0, 0, t, 10); Stroke(7-t, 0, t, 10);
        Stroke(10, 0, t, 10); Stroke(10, 0, 7, t); Stroke(10, 4.5f, 7, t); Stroke(15.5f, 0, t, 6);
        Stroke(20, 0, t, 10); Stroke(20, 0, 7, t); Stroke(20, 4.5f, 6, t); Stroke(20, 8.5f, 7, t);
        Stroke(30, 0, t, 10); Stroke(36, 0, t, 10); Stroke(33, -.5f, t, 11, -32);
        ApplyState();
    }
    public void SetLayout(float dpi, float width)
    {
        Size = new(width, 24 * dpi); Root.Size = Size;
        Root.CenterPoint = new(Size.X / 2, Size.Y / 2, 0);
        _background.Size = Size; _geometry.Size = Size; _geometry.CornerRadius = new(8 * dpi);
        _label.Scale = new(dpi, dpi, 1);
        _label.Offset = new((Size.X - 37.5f * dpi) / 2, (Size.Y - 10 * dpi) / 2, 0);
    }
    public void SetVisible(bool value) { Root.IsVisible = value; if (!value) _pressed = false; ApplyState(); }
    public void SetHovered(bool value) { if (_hovered == value) return; _hovered = value; ApplyState(); }
    public void SetPressed(bool value) { _pressed = value; ApplyState(); }
    public void SetBusy(bool value) { _busy = value; ApplyState(); }
    private void ApplyState()
    {
        _backgroundBrush.Color = _busy || _pressed ? Windows.UI.Color.FromArgb(255, 35, 110, 167)
            : _hovered ? Windows.UI.Color.FromArgb(245, 49, 83, 110) : Windows.UI.Color.FromArgb(224, 25, 37, 49);
        Root.Scale = new Vector3(_pressed ? .96f : 1f);
    }
    public void Dispose()
    {
        Root.Dispose(); foreach (var stroke in _strokes) stroke.Dispose();
        _label.Dispose(); _background.Dispose(); _clip.Dispose(); _geometry.Dispose(); _ink.Dispose(); _backgroundBrush.Dispose();
    }
}
