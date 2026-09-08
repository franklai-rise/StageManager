using System.Numerics;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

/// <summary>A compact desktop-icons toggle aligned with the other footer cards.</summary>
internal sealed class SidebarDesktopIconsButtonVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly List<CompositionObject> _resources = new();
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionColorBrush _iconBrush;
	private readonly ShapeVisual _icons;
	private readonly ShapeVisual _hiddenSlash;
	private readonly ShapeVisual _activeIndicator;
	private bool _hovered;
	private bool _pressed;
	private bool _iconsVisible;
	private bool _disposed;

	public SidebarDesktopIconsButtonVisual(Compositor compositor)
	{
		_compositor = compositor;
		Root = Own(compositor.CreateContainerVisual());
		_backgroundBrush = Brush(112, 22, 27, 36);
		_geometry = Own(compositor.CreateRoundedRectangleGeometry());
		var clip = Own(compositor.CreateGeometricClip(_geometry));
		_background = Own(compositor.CreateSpriteVisual());
		_background.Brush = _backgroundBrush;
		_background.Clip = clip;
		Root.Children.InsertAtTop(_background);

		_iconBrush = Brush(238, 139, 196, 235);
		_icons = Own(compositor.CreateShapeVisual());
		_icons.Size = new Vector2(34, 26);
		AddRectangle(_icons, new Vector2(3, 3), new Vector2(8, 8), 1.8f, _iconBrush);
		AddRectangle(_icons, new Vector2(23, 3), new Vector2(8, 8), 1.8f, _iconBrush);
		AddRectangle(_icons, new Vector2(3, 16), new Vector2(8, 8), 1.8f, _iconBrush);
		AddRectangle(_icons, new Vector2(23, 16), new Vector2(8, 8), 1.8f, _iconBrush);
		Root.Children.InsertAtTop(_icons);

		_hiddenSlash = Own(compositor.CreateShapeVisual());
		_hiddenSlash.Size = new Vector2(34, 26);
		AddStroke(_hiddenSlash, new Vector2(5, 23), new Vector2(29, 3), 3f, _iconBrush);
		Root.Children.InsertAtTop(_hiddenSlash);

		_activeIndicator = Own(compositor.CreateShapeVisual());
		_activeIndicator.Size = new Vector2(12, 2);
		AddRectangle(_activeIndicator, Vector2.Zero, new Vector2(12, 2), 1f, _iconBrush);
		Root.Children.InsertAtTop(_activeIndicator);
		SetLayout(1f, 64f);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle => -7.5f;

	public void SetLayout(float dpiScale, float cardWidth)
	{
		var scale = Math.Max(0.75f, dpiScale);
		Size = new Vector2(Math.Max(64f * scale, cardWidth), 36f * scale);
		Root.Size = Size;
		Root.CenterPoint = new Vector3(Size.X * 0.88f, Size.Y / 2f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.RotationAngleInDegrees = Angle;
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		_background.Size = Size;
		_geometry.Size = Size;
		_geometry.CornerRadius = new Vector2(8f * scale);
		var iconOffset = new Vector3((Size.X - 34f * scale) / 2f + 2f * scale,
			(Size.Y - 26f * scale) / 2f, 0);
		_icons.Scale = new Vector3(scale, scale, 1);
		_icons.Offset = iconOffset;
		_hiddenSlash.Scale = new Vector3(scale, scale, 1);
		_hiddenSlash.Offset = iconOffset;
		_activeIndicator.Scale = new Vector3(scale, scale, 1);
		_activeIndicator.Offset = new Vector3((Size.X - 12f * scale) / 2f + 2f * scale,
			Size.Y - 3f * scale, 0);
		ApplyState();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;
	public void SetVisible(bool visible) => Root.IsVisible = visible;
	public void SetHovered(bool hovered) { if (_hovered != hovered) { _hovered = hovered; ApplyState(); } }
	public void SetPressed(bool pressed) { if (_pressed != pressed) { _pressed = pressed; ApplyState(); } }
	public void SetIconsVisible(bool visible) { if (_iconsVisible != visible) { _iconsVisible = visible; ApplyState(); } }

	private void ApplyState()
	{
		if (_disposed) return;
		_backgroundBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_pressed ? 190 : _hovered ? 160 : _iconsVisible ? 140 : 112), 22, 27, 36);
		_iconBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_pressed || _hovered || _iconsVisible ? 255 : 205),
			_iconsVisible ? (byte)139 : (byte)164,
			_iconsVisible ? (byte)196 : (byte)171,
			_iconsVisible ? (byte)235 : (byte)184);
		_hiddenSlash.IsVisible = !_iconsVisible;
		_activeIndicator.IsVisible = _iconsVisible;
		var scale = _pressed ? 0.95f : _hovered ? 1.025f : 1f;
		Root.Scale = new Vector3(scale, scale, 1);
	}

	private T Own<T>(T resource) where T : CompositionObject { _resources.Add(resource); return resource; }
	private CompositionColorBrush Brush(byte a, byte r, byte g, byte b) =>
		Own(_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(a, r, g, b)));
	private void AddRectangle(ShapeVisual visual, Vector2 offset, Vector2 size, float radius, CompositionBrush brush)
	{
		var geometry = Own(_compositor.CreateRoundedRectangleGeometry());
		geometry.Offset = offset; geometry.Size = size; geometry.CornerRadius = new Vector2(radius);
		var shape = Own(_compositor.CreateSpriteShape(geometry)); shape.FillBrush = brush; visual.Shapes.Add(shape);
	}
	private void AddStroke(ShapeVisual visual, Vector2 from, Vector2 to, float thickness, CompositionBrush brush)
	{
		var delta = to - from;
		var geometry = Own(_compositor.CreateRoundedRectangleGeometry());
		geometry.Offset = new Vector2(0, -thickness / 2f); geometry.Size = new Vector2(delta.Length(), thickness);
		geometry.CornerRadius = new Vector2(thickness / 2f);
		var shape = Own(_compositor.CreateSpriteShape(geometry)); shape.FillBrush = brush;
		shape.TransformMatrix = Matrix3x2.CreateRotation(MathF.Atan2(delta.Y, delta.X)) * Matrix3x2.CreateTranslation(from);
		visual.Shapes.Add(shape);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Root.Children.RemoveAll();
		_icons.Shapes.Clear(); _hiddenSlash.Shapes.Clear(); _activeIndicator.Shapes.Clear();
		for (var index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
		_resources.Clear();
	}
}
