using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype.NotificationArea;

internal readonly record struct NotificationTrayIconSlot(
	NotificationIconActivation Activation,
	string Name,
	Rectangle Bounds);

internal sealed class NotificationTrayCardVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly D3DCompositionDevice _graphics;
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _backgroundGeometry;
	private readonly CompositionGeometricClip _backgroundClip;
	private readonly DropShadow _shadow;
	private readonly SpriteVisual _hoverHighlight;
	private readonly CompositionColorBrush _hoverBrush;
	private readonly CompositionRoundedRectangleGeometry _hoverGeometry;
	private readonly CompositionGeometricClip _hoverClip;
	private readonly SpriteVisual _content;
	private readonly NotificationTrayControlsVisual _controls;
	private CardSwapChain? _surface;
	private CompositionSurfaceBrush? _surfaceBrush;
	private readonly List<NotificationIconSnapshot> _icons = new();
	private readonly List<NotificationTrayIconSlot> _slots = new();
	private bool _hovered;
	private bool _pressed;
	private bool _disposed;

	public NotificationTrayCardVisual(Compositor compositor, D3DCompositionDevice graphics)
	{
		_compositor = compositor;
		_graphics = graphics;
		Root = compositor.CreateContainerVisual();
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(128, 18, 23, 31));
		_background = compositor.CreateSpriteVisual();
		_background.Brush = _backgroundBrush;
		_backgroundGeometry = compositor.CreateRoundedRectangleGeometry();
		_backgroundClip = compositor.CreateGeometricClip(_backgroundGeometry);
		_background.Clip = _backgroundClip;
		_shadow = compositor.CreateDropShadow();
		_shadow.BlurRadius = 22f;
		_shadow.Opacity = 0.45f;
		_shadow.Offset = new Vector3(0, 6, 0);
		_shadow.Color = Windows.UI.Color.FromArgb(220, 2, 4, 8);
		_background.Shadow = _shadow;

		_hoverBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 112, 190, 255));
		_hoverHighlight = compositor.CreateSpriteVisual();
		_hoverHighlight.Brush = _hoverBrush;
		_hoverGeometry = compositor.CreateRoundedRectangleGeometry();
		_hoverClip = compositor.CreateGeometricClip(_hoverGeometry);
		_hoverHighlight.Clip = _hoverClip;
		_hoverHighlight.IsVisible = false;
		_content = compositor.CreateSpriteVisual();
		_controls = new NotificationTrayControlsVisual(compositor);

		Root.Children.InsertAtBottom(_background);
		Root.Children.InsertAtTop(_hoverHighlight);
		Root.Children.InsertAtTop(_content);
		// The card now belongs to the scrolling main column. Refresh remains in
		// its context menu; the former external drag/refresh controls are hidden.
		_controls.Root.IsVisible = false;
		SetLayout(1f, 196f);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle => -7.5f;
	public IReadOnlyList<NotificationTrayIconSlot> Slots => _slots;
	public Rectangle DragHandleBounds => _controls.DragHandleBounds;
	public Rectangle RefreshButtonBounds => _controls.RefreshButtonBounds;
	public float ExternalControlsHeight => 0;

	public void SetLayout(float dpiScale, float cardWidth)
	{
		var scale = Math.Max(0.75f, dpiScale);
		var side = Math.Max(96f * scale, cardWidth);
		var nextSize = new Vector2(side, side);
		var surfaceChanged = (int)Math.Ceiling(Size.X) != (int)Math.Ceiling(nextSize.X) ||
			(int)Math.Ceiling(Size.Y) != (int)Math.Ceiling(nextSize.Y);
		Size = nextSize;
		Root.Size = Size;
		_background.Size = Size;
		_background.CenterPoint = new Vector3(Size.X * 0.88f, Size.Y / 2f, 0);
		_background.RotationAxis = Vector3.UnitY;
		_background.RotationAngleInDegrees = Angle;
		Pivot = new Vector2(_background.CenterPoint.X, _background.CenterPoint.Y);
		_backgroundGeometry.Size = Size;
		_backgroundGeometry.CornerRadius = new Vector2(Math.Max(9f, Size.Y * 0.105f));
		_content.Size = Size;
		_controls.SetLayout(scale);
		if (surfaceChanged || _surface is null)
			RecreateSurface();
		RenderIcons();
		ApplyState();
	}

	public void UpdateIcons(IReadOnlyList<NotificationIconSnapshot> icons)
	{
		foreach (var icon in _icons)
			icon.Dispose();
		_icons.Clear();
		foreach (var icon in icons.Take(NotificationTrayLayout.MaximumIcons))
			_icons.Add(new NotificationIconSnapshot(icon.Ordinal, icon.Name, new Bitmap(icon.Icon)));
		RenderIcons();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;
	public void SetVisible(bool visible) => Root.IsVisible = visible;

	public void SetHovered(NotificationIconActivation? activation)
	{
		_hovered = activation is not null;
		if (activation is null)
		{
			_hoverHighlight.IsVisible = false;
			ApplyState();
			return;
		}
		var slot = _slots.FirstOrDefault(candidate => candidate.Activation.Equals(activation.Value));
		if (slot.Bounds.Width <= 0)
		{
			_hoverHighlight.IsVisible = false;
			ApplyState();
			return;
		}
		var padding = Math.Max(4f, Size.X * 0.025f);
		var highlightBounds = RectangleF.Inflate(slot.Bounds, padding, padding);
		_hoverHighlight.Size = new Vector2(highlightBounds.Width, highlightBounds.Height);
		_hoverHighlight.Offset = new Vector3(highlightBounds.X, highlightBounds.Y, 2);
		_hoverGeometry.Size = _hoverHighlight.Size;
		_hoverGeometry.CornerRadius = new Vector2(Math.Max(5f, highlightBounds.Height * 0.24f));
		_hoverHighlight.IsVisible = true;
		ApplyState();
	}

	public void SetPressed(bool pressed)
	{
		_pressed = pressed;
		ApplyState();
	}

	public void SetControlHovered(bool dragHovered, bool refreshHovered) =>
		_controls.SetHovered(dragHovered, refreshHovered);

	public void SetControlPressed(bool dragPressed, bool refreshPressed) =>
		_controls.SetPressed(dragPressed, refreshPressed);

	private void ApplyState()
	{
		if (_disposed)
			return;
		_backgroundBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_pressed ? 180 : _hovered ? 150 : 128), 18, 23, 31);
		_hoverBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_pressed ? 105 : _hovered ? 68 : 0), 112, 190, 255);
	}

	private void RecreateSurface()
	{
		_content.Brush = null;
		_surfaceBrush?.Dispose();
		_surfaceBrush = null;
		_surface?.Dispose();
		_surface = _graphics.CreateSurface(
			_compositor,
			Math.Max(1, (int)Math.Ceiling(Size.X)),
			Math.Max(1, (int)Math.Ceiling(Size.Y)));
		_surfaceBrush = _compositor.CreateSurfaceBrush(_surface.CompositionSurface);
		_surfaceBrush.Stretch = CompositionStretch.None;
		_surfaceBrush.HorizontalAlignmentRatio = 0;
		_surfaceBrush.VerticalAlignmentRatio = 0;
		_content.Brush = _surfaceBrush;
	}

	private void RenderIcons()
	{
		if (_surface is null)
			return;
		_slots.Clear();
		using var bitmap = new Bitmap(_surface.Width, _surface.Height, PixelFormat.Format32bppPArgb);
		using (var graphics = Graphics.FromImage(bitmap))
		{
			graphics.Clear(Color.Transparent);
			var rectangles = NotificationTrayLayout.Arrange(
				bitmap.Size,
				_icons.Select(icon => icon.Icon.Size).ToArray());
			for (var index = 0; index < rectangles.Count; index++)
			{
				var icon = _icons[index];
				var bounds = rectangles[index];
				graphics.DrawImageUnscaled(icon.Icon, bounds.Location);
				_slots.Add(new NotificationTrayIconSlot(
					new NotificationIconActivation(icon.Ordinal, icon.Name),
					icon.Name,
					bounds));
			}
		}
		_surface.Upload(CopyPixels(bitmap));
	}

	private static byte[] CopyPixels(Bitmap bitmap)
	{
		var data = bitmap.LockBits(
			new Rectangle(0, 0, bitmap.Width, bitmap.Height),
			ImageLockMode.ReadOnly,
			PixelFormat.Format32bppPArgb);
		try
		{
			var pixels = new byte[bitmap.Width * bitmap.Height * 4];
			for (var row = 0; row < bitmap.Height; row++)
				Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * bitmap.Width * 4, bitmap.Width * 4);
			return pixels;
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		foreach (var icon in _icons)
			icon.Dispose();
		_icons.Clear();
		_slots.Clear();
		_content.Brush = null;
		_surfaceBrush?.Dispose();
		_surface?.Dispose();
		_controls.Dispose();
		_content.Dispose();
		_hoverClip.Dispose();
		_hoverGeometry.Dispose();
		_hoverHighlight.Dispose();
		_hoverBrush.Dispose();
		_shadow.Dispose();
		_backgroundClip.Dispose();
		_backgroundGeometry.Dispose();
		_background.Dispose();
		_backgroundBrush.Dispose();
		Root.Dispose();
	}
}

internal sealed class NotificationTrayControlsVisual : IDisposable
{
	private readonly SpriteVisual _dragBackground;
	private readonly CompositionColorBrush _dragBackgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _dragGeometry;
	private readonly CompositionGeometricClip _dragClip;
	private readonly SpriteVisual _refreshBackground;
	private readonly CompositionColorBrush _refreshBackgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _refreshGeometry;
	private readonly CompositionGeometricClip _refreshClip;
	private readonly CompositionColorBrush _iconBrush;
	private readonly SpriteVisual[] _dragStrokes;
	private readonly SpriteVisual[] _refreshStrokes;
	private bool _dragHovered;
	private bool _refreshHovered;
	private bool _dragPressed;
	private bool _refreshPressed;

	public NotificationTrayControlsVisual(Compositor compositor)
	{
		Root = compositor.CreateContainerVisual();
		_dragBackgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(100, 35, 42, 54));
		_dragBackground = compositor.CreateSpriteVisual();
		_dragBackground.Brush = _dragBackgroundBrush;
		_dragGeometry = compositor.CreateRoundedRectangleGeometry();
		_dragClip = compositor.CreateGeometricClip(_dragGeometry);
		_dragBackground.Clip = _dragClip;
		_refreshBackgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(100, 35, 42, 54));
		_refreshBackground = compositor.CreateSpriteVisual();
		_refreshBackground.Brush = _refreshBackgroundBrush;
		_refreshGeometry = compositor.CreateRoundedRectangleGeometry();
		_refreshClip = compositor.CreateGeometricClip(_refreshGeometry);
		_refreshBackground.Clip = _refreshClip;
		_iconBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(225, 215, 226, 244));
		_dragStrokes = Enumerable.Range(0, 5).Select(_ =>
		{
			var stroke = compositor.CreateSpriteVisual();
			stroke.Brush = _iconBrush;
			Root.Children.InsertAtTop(stroke);
			return stroke;
		}).ToArray();
		_refreshStrokes = Enumerable.Range(0, 10).Select(_ =>
		{
			var stroke = compositor.CreateSpriteVisual();
			stroke.Brush = _iconBrush;
			Root.Children.InsertAtTop(stroke);
			return stroke;
		}).ToArray();
		Root.Children.InsertAtBottom(_dragBackground);
		Root.Children.InsertAtBottom(_refreshBackground);
		SetLayout(1f);
	}

	public ContainerVisual Root { get; }
	public Rectangle DragHandleBounds { get; private set; }
	public Rectangle RefreshButtonBounds { get; private set; }
	public float ExternalHeight { get; private set; }

	public void SetLayout(float scale)
	{
		var buttonWidth = 30f * scale;
		var buttonHeight = 26f * scale;
		var left = 12f * scale;
		var gap = 7f * scale;
		var top = -(buttonHeight + 8f * scale);
		DragHandleBounds = Rectangle.Round(new RectangleF(left, top, buttonWidth, buttonHeight));
		RefreshButtonBounds = Rectangle.Round(new RectangleF(left + buttonWidth + gap, top, buttonWidth, buttonHeight));
		ExternalHeight = -top;
		_dragBackground.Size = new Vector2(buttonWidth, buttonHeight);
		_dragBackground.Offset = new Vector3(left, top, 1);
		_dragGeometry.Size = _dragBackground.Size;
		_dragGeometry.CornerRadius = new Vector2(6f * scale);
		_refreshBackground.Size = new Vector2(buttonWidth, buttonHeight);
		_refreshBackground.Offset = new Vector3(left + buttonWidth + gap, top, 1);
		_refreshGeometry.Size = _refreshBackground.Size;
		_refreshGeometry.CornerRadius = new Vector2(6f * scale);
		var dragCenterX = left + buttonWidth / 2f;
		var dragCenterY = top + buttonHeight / 2f;
		ConfigureStroke(_dragStrokes[0], dragCenterX, dragCenterY, 2f * scale, 13f * scale, 0f);
		ConfigureStroke(_dragStrokes[1], dragCenterX - 2.1f * scale, dragCenterY - 5.7f * scale, 6f * scale, 1.8f * scale, -38f);
		ConfigureStroke(_dragStrokes[2], dragCenterX + 2.1f * scale, dragCenterY - 5.7f * scale, 6f * scale, 1.8f * scale, 38f);
		ConfigureStroke(_dragStrokes[3], dragCenterX - 2.1f * scale, dragCenterY + 5.7f * scale, 6f * scale, 1.8f * scale, 38f);
		ConfigureStroke(_dragStrokes[4], dragCenterX + 2.1f * scale, dragCenterY + 5.7f * scale, 6f * scale, 1.8f * scale, -38f);

		var refreshCenterX = left + buttonWidth + gap + buttonWidth / 2f;
		var refreshCenterY = dragCenterY;
		var radius = 7f * scale;
		var arcAngles = new[] { -150f, -105f, -60f, -15f, 30f, 75f, 120f, 165f };
		for (var index = 0; index < arcAngles.Length; index++)
		{
			var radians = arcAngles[index] * MathF.PI / 180f;
			ConfigureStroke(
				_refreshStrokes[index],
				refreshCenterX + MathF.Cos(radians) * radius,
				refreshCenterY + MathF.Sin(radians) * radius,
				4.5f * scale,
				1.8f * scale,
				arcAngles[index] + 90f);
		}
		var headRadians = 165f * MathF.PI / 180f;
		var headX = refreshCenterX + MathF.Cos(headRadians) * radius;
		var headY = refreshCenterY + MathF.Sin(headRadians) * radius;
		ConfigureStroke(_refreshStrokes[8], headX - 1.4f * scale, headY + 1.5f * scale, 5.5f * scale, 1.8f * scale, 220f);
		ConfigureStroke(_refreshStrokes[9], headX + 1.1f * scale, headY + 2.2f * scale, 5.5f * scale, 1.8f * scale, 285f);
		Root.Size = new Vector2(RefreshButtonBounds.Right + 4f * scale, buttonHeight);
		ApplyState();
	}

	public void SetHovered(bool dragHovered, bool refreshHovered)
	{
		_dragHovered = dragHovered;
		_refreshHovered = refreshHovered;
		ApplyState();
	}

	public void SetPressed(bool dragPressed, bool refreshPressed)
	{
		_dragPressed = dragPressed;
		_refreshPressed = refreshPressed;
		ApplyState();
	}

	private static void ConfigureStroke(SpriteVisual stroke, float centerX, float centerY, float width, float height, float angle)
	{
		stroke.Size = new Vector2(width, height);
		stroke.Offset = new Vector3(centerX - width / 2f, centerY - height / 2f, 2);
		stroke.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
		stroke.RotationAngleInDegrees = angle;
	}

	private void ApplyState()
	{
		_dragBackgroundBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_dragPressed ? 200 : _dragHovered ? 155 : 100), 35, 42, 54);
		_refreshBackgroundBrush.Color = Windows.UI.Color.FromArgb(
			(byte)(_refreshPressed ? 200 : _refreshHovered ? 155 : 100), 35, 42, 54);
		_iconBrush.Color = _dragPressed || _refreshPressed
			? Windows.UI.Color.FromArgb(255, 112, 190, 255)
			: Windows.UI.Color.FromArgb(235, 215, 226, 244);
	}

	public void Dispose()
	{
		foreach (var stroke in _dragStrokes) stroke.Dispose();
		foreach (var stroke in _refreshStrokes) stroke.Dispose();
		_iconBrush.Dispose();
		_dragClip.Dispose();
		_dragGeometry.Dispose();
		_dragBackground.Dispose();
		_dragBackgroundBrush.Dispose();
		_refreshClip.Dispose();
		_refreshGeometry.Dispose();
		_refreshBackground.Dispose();
		_refreshBackgroundBrush.Dispose();
		Root.Dispose();
	}
}
