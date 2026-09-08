using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype.QuickLaunch;

internal enum QuickLaunchApp
{
	Chrome,
	Edge
}

internal static class QuickLaunchAppResolver
{
	public static string DisplayName(QuickLaunchApp app) => app switch
	{
		QuickLaunchApp.Chrome => "Google Chrome",
		QuickLaunchApp.Edge => "Microsoft Edge",
		_ => app.ToString()
	};

	public static string CommandName(QuickLaunchApp app) => app switch
	{
		QuickLaunchApp.Chrome => "chrome.exe",
		QuickLaunchApp.Edge => "msedge.exe",
		_ => throw new ArgumentOutOfRangeException(nameof(app))
	};

	public static string? ResolveExecutable(QuickLaunchApp app)
	{
		var command = CommandName(app);
		var registryPaths = new[]
		{
			$@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{command}",
			$@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{command}"
		};
		foreach (var registryPath in registryPaths)
		{
			if (Registry.GetValue(registryPath, null, null) is string path && File.Exists(path))
				return path;
		}

		var candidates = app == QuickLaunchApp.Chrome
			? new[]
			{
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", command),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", command),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", command)
			}
			: new[]
			{
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", command),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", command)
			};
		return candidates.FirstOrDefault(File.Exists);
	}

	public static bool Launch(QuickLaunchApp app)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = ResolveExecutable(app) ?? CommandName(app),
				UseShellExecute = true
			});
			return true;
		}
		catch
		{
			return false;
		}
	}
}

internal sealed class SidebarQuickLaunchVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly D3DCompositionDevice _graphics;
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionGeometricClip _clip;
	private readonly SpriteVisual _content;
	private readonly Bitmap? _icon;
	private CardSwapChain? _surface;
	private CompositionSurfaceBrush? _surfaceBrush;
	private bool _hovered;
	private bool _pressed;

	public SidebarQuickLaunchVisual(Compositor compositor, D3DCompositionDevice graphics, QuickLaunchApp app)
	{
		_compositor = compositor;
		_graphics = graphics;
		App = app;
		ExecutablePath = QuickLaunchAppResolver.ResolveExecutable(app);
		if (ExecutablePath is not null)
		{
			using var icon = Icon.ExtractAssociatedIcon(ExecutablePath);
			_icon = icon?.ToBitmap();
		}
		Root = compositor.CreateContainerVisual();
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(112, 22, 27, 36));
		_background = compositor.CreateSpriteVisual();
		_background.Brush = _backgroundBrush;
		_geometry = compositor.CreateRoundedRectangleGeometry();
		_clip = compositor.CreateGeometricClip(_geometry);
		_background.Clip = _clip;
		_content = compositor.CreateSpriteVisual();
		Root.Children.InsertAtBottom(_background);
		Root.Children.InsertAtTop(_content);
		SetLayout(1f, 196f);
	}

	public QuickLaunchApp App { get; }
	public string? ExecutablePath { get; }
	public bool IsAvailable => _icon is not null;
	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle => -7.5f;

	public void SetLayout(float dpiScale, float cardWidth)
	{
		var scale = Math.Max(0.75f, dpiScale);
		var nextSize = new Vector2(Math.Max(64f * scale, cardWidth), 36f * scale);
		var surfaceChanged = (int)Math.Ceiling(Size.X) != (int)Math.Ceiling(nextSize.X) ||
			(int)Math.Ceiling(Size.Y) != (int)Math.Ceiling(nextSize.Y);
		Size = nextSize;
		Root.Size = Size;
		Root.CenterPoint = new Vector3(Size.X * 0.88f, Size.Y / 2f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.RotationAngleInDegrees = Angle;
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		_background.Size = Size;
		_geometry.Size = Size;
		_geometry.CornerRadius = new Vector2(8f * scale);
		if (surfaceChanged || _surface is null)
			RecreateSurface();
		ApplyState();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;
	public void SetVisible(bool visible) => Root.IsVisible = visible;
	public void SetHovered(bool hovered) { _hovered = hovered; ApplyState(); }
	public void SetPressed(bool pressed) { _pressed = pressed; ApplyState(); }

	private void ApplyState()
	{
		_backgroundBrush.Color = Windows.UI.Color.FromArgb((byte)(_pressed ? 205 : _hovered ? 165 : 112), 22, 27, 36);
		Root.Scale = new Vector3(_pressed ? 0.96f : _hovered ? 1.025f : 1f);
	}

	private void RecreateSurface()
	{
		_content.Brush = null;
		_surfaceBrush?.Dispose();
		_surface?.Dispose();
		var width = Math.Max(1, (int)Math.Ceiling(Size.X));
		var height = Math.Max(1, (int)Math.Ceiling(Size.Y));
		_surface = _graphics.CreateSurface(_compositor, width, height);
		_surfaceBrush = _compositor.CreateSurfaceBrush(_surface.CompositionSurface);
		_surfaceBrush.Stretch = CompositionStretch.None;
		_content.Size = Size;
		_content.Brush = _surfaceBrush;
		using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
		using (var drawing = Graphics.FromImage(bitmap))
		{
			drawing.Clear(Color.Transparent);
			if (_icon is not null)
			{
				drawing.InterpolationMode = InterpolationMode.HighQualityBicubic;
				var side = Math.Min(_icon.Width, Math.Min(_icon.Height, Math.Max(20, (int)Math.Round(height * 0.72))));
				drawing.DrawImage(_icon, (width - side) / 2, (height - side) / 2, side, side);
			}
		}
		_surface.Upload(CopyPixels(bitmap));
	}

	private static byte[] CopyPixels(Bitmap bitmap)
	{
		var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
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
		_content.Brush = null;
		_surfaceBrush?.Dispose();
		_surface?.Dispose();
		_icon?.Dispose();
		_content.Dispose();
		_clip.Dispose();
		_geometry.Dispose();
		_background.Dispose();
		_backgroundBrush.Dispose();
		Root.Dispose();
	}
}
