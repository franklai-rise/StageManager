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

		Root.Children.InsertAtBottom(_background);
		Root.Children.InsertAtTop(_hoverHighlight);
		Root.Children.InsertAtTop(_content);
		SetLayout(1f, 196f);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle => -7.5f;
	public IReadOnlyList<NotificationTrayIconSlot> Slots => _slots;

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
