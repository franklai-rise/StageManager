using System.Numerics;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

/// <summary>A desktop action styled to match the Explorer shortcut card.</summary>
internal sealed class SidebarDesktopButtonVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly List<CompositionObject> _resources = new();
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionColorBrush _iconBrush;
	private readonly ContainerVisual _glyph;
	private readonly ShapeVisual _minimizeIcon;
	private readonly ShapeVisual _restoreIcon;
	private readonly ShapeVisual _activeIndicator;
	private bool _hovered;
	private bool _pressed;
	private bool _active;
	private bool _disposed;

	public SidebarDesktopButtonVisual(Compositor compositor)
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

		_glyph = Own(compositor.CreateContainerVisual());
		_glyph.Size = new Vector2(34, 26);
		Root.Children.InsertAtTop(_glyph);
		_iconBrush = Brush(238, 139, 196, 235);
		var screenBrush = Brush(235, 49, 103, 146);
		var whiteBrush = Brush(250, 237, 248, 255);
		var rearBrush = Brush(225, 91, 145, 189);

		_minimizeIcon = CreateIcon();
		AddRectangle(_minimizeIcon, new Vector2(2, 1), new Vector2(30, 19), 2.5f, _iconBrush);
		AddRectangle(_minimizeIcon, new Vector2(4, 3), new Vector2(26, 14), 1.5f, screenBrush);
		AddStroke(_minimizeIcon, new Vector2(17, 6), new Vector2(17, 13), 1.8f, whiteBrush);
		AddStroke(_minimizeIcon, new Vector2(13.5f, 9.5f), new Vector2(17, 13), 1.8f, whiteBrush);
		AddStroke(_minimizeIcon, new Vector2(17, 13), new Vector2(20.5f, 9.5f), 1.8f, whiteBrush);
		AddRectangle(_minimizeIcon, new Vector2(16, 19), new Vector2(2, 4), 0.5f, _iconBrush);
		AddRectangle(_minimizeIcon, new Vector2(10, 23), new Vector2(14, 2), 1f, _iconBrush);

		_restoreIcon = CreateIcon();
		AddRectangle(_restoreIcon, new Vector2(1, 1), new Vector2(25, 18), 2.5f, rearBrush);
		AddRectangle(_restoreIcon, new Vector2(3, 3), new Vector2(21, 2), 0.5f, _iconBrush);
		AddRectangle(_restoreIcon, new Vector2(8, 7), new Vector2(25, 18), 2.5f, _iconBrush);
		AddRectangle(_restoreIcon, new Vector2(10, 12), new Vector2(21, 11), 1f, screenBrush);
		AddStroke(_restoreIcon, new Vector2(13, 9.5f), new Vector2(18, 9.5f), 1.4f, whiteBrush);

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
	public bool Active => _active;

	public void SetLayout(float dpiScale, float cardWidth)
	{
		// Keep the same dimensions, pivot, corner radius and perspective as Explorer.
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

		_glyph.Scale = new Vector3(scale, scale, 1);
		_glyph.Offset = new Vector3((Size.X - 34f * scale) / 2f + 2f * scale,
			(Size.Y - 26f * scale) / 2f, 0);
		_activeIndicator.Scale = new Vector3(scale, scale, 1);
		_activeIndicator.Offset = new Vector3((Size.X - 12f * scale) / 2f + 2f * scale,
			Size.Y - 3f * scale, 0);
		ApplyState();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;
	public void SetVisible(bool visible) => Root.IsVisible = visible;
	public void SetHovered(bool hovered)
	{
		if (_hovered == hovered) return;
		_hovered = hovered;
		ApplyState();
	}
	public void SetPressed(bool pressed)
	{
		if (_pressed == pressed) return;
		_pressed = pressed;
		ApplyState();
	}
	public void SetActive(bool active)
	{
		if (_active == active) return;
		_active = active;
		ApplyState();
	}

	private void ApplyState()
	{
		if (_disposed) return;
		_backgroundBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_pressed ? 190 : _hovered ? 160 : _active ? 140 : 112), 22, 27, 36);
		_iconBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_pressed || _hovered || _active ? 255 : 238), 139, 196, 235);
		_minimizeIcon.IsVisible = !_active;
		_restoreIcon.IsVisible = _active;
		_activeIndicator.IsVisible = _active;
		var scale = _pressed ? 0.95f : _hovered ? 1.025f : 1f;
		Root.Scale = new Vector3(scale, scale, 1);
	}

	private T Own<T>(T resource) where T : CompositionObject
	{
		_resources.Add(resource);
		return resource;
	}

	private CompositionColorBrush Brush(byte alpha, byte red, byte green, byte blue) =>
		Own(_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(alpha, red, green, blue)));

	private ShapeVisual CreateIcon()
	{
		var visual = Own(_compositor.CreateShapeVisual());
		visual.Size = _glyph.Size;
		_glyph.Children.InsertAtTop(visual);
		return visual;
	}

	private void AddRectangle(ShapeVisual visual, Vector2 offset, Vector2 size, float radius,
		CompositionBrush brush)
	{
		var geometry = Own(_compositor.CreateRoundedRectangleGeometry());
		geometry.Offset = offset;
		geometry.Size = size;
		geometry.CornerRadius = new Vector2(radius);
		var shape = Own(_compositor.CreateSpriteShape(geometry));
		shape.FillBrush = brush;
		visual.Shapes.Add(shape);
	}

	private void AddStroke(ShapeVisual visual, Vector2 from, Vector2 to, float thickness,
		CompositionBrush brush)
	{
		var delta = to - from;
		var geometry = Own(_compositor.CreateRoundedRectangleGeometry());
		geometry.Offset = new Vector2(0, -thickness / 2f);
		geometry.Size = new Vector2(delta.Length(), thickness);
		geometry.CornerRadius = new Vector2(thickness / 2f);
		var shape = Own(_compositor.CreateSpriteShape(geometry));
		shape.FillBrush = brush;
		shape.TransformMatrix = Matrix3x2.CreateRotation(MathF.Atan2(delta.Y, delta.X)) *
			Matrix3x2.CreateTranslation(from);
		visual.Shapes.Add(shape);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		Root.Children.RemoveAll();
		_glyph.Children.RemoveAll();
		_minimizeIcon.Shapes.Clear();
		_restoreIcon.Shapes.Clear();
		_activeIndicator.Shapes.Clear();
		for (var index = _resources.Count - 1; index >= 0; index--)
			_resources[index].Dispose();
		_resources.Clear();
	}
}
