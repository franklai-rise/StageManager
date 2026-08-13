using StageManager.Native;
using StageManager.Native.Window;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

internal sealed record CapturedCardFrame(IntPtr Handle, byte[] Pixels, int Width, int Height, bool IsPlaceholder);

internal sealed class WindowFrameCapture : IDisposable
{
	private readonly ConcurrentDictionary<string, Bitmap> _icons = new(StringComparer.OrdinalIgnoreCase);
	private bool _disposed;

	public CapturedCardFrame Capture(IWindow window, int targetWidth, int targetHeight, string? countBadge = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		targetWidth = Math.Max(32, targetWidth);
		targetHeight = Math.Max(24, targetHeight);
		// Minimized windows are rendered as lightweight placeholders. Calling PrintWindow on
		// them can wake or flash GPU-heavy applications without producing a useful frame.
		using var source = window.IsMinimized ? null : TryCaptureWindow(window.Handle);
		using var card = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
		using var graphics = Graphics.FromImage(card);
		graphics.Clear(Color.Transparent);
		graphics.CompositingMode = CompositingMode.SourceOver;
		graphics.CompositingQuality = CompositingQuality.HighQuality;
		graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
		graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;

		var radius = Math.Max(8, (int)Math.Round(targetHeight * 0.10));
		using var clipPath = CreateRoundedRectangle(new Rectangle(1, 1, targetWidth - 2, targetHeight - 2), radius);
		graphics.SetClip(clipPath);

		var placeholder = window.IsMinimized || source is null;
		if (placeholder)
		{
			using var placeholderBackground = new SolidBrush(Color.FromArgb(238, 232, 236, 242));
			graphics.FillPath(placeholderBackground, clipPath);
		}
		else
		{
			var target = new Rectangle(1, 1, targetWidth - 2, targetHeight - 2);
			var crop = AspectFillCrop(source!.Size, target.Size);
			graphics.DrawImage(source, target, crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel);
		}

		graphics.ResetClip();
		DrawIconBadge(graphics, window, targetWidth, targetHeight);
		DrawStatusBadge(
			graphics,
			window.IsMinimized ? "MINIMIZED" : source is null ? "NO PREVIEW" : null,
			targetWidth,
			targetHeight);
		if (!string.IsNullOrWhiteSpace(countBadge))
			DrawCountBadge(graphics, countBadge!, targetWidth);
		using var border = new Pen(Color.FromArgb(118, 225, 232, 246), Math.Max(1, targetWidth / 220f));
		graphics.DrawPath(border, clipPath);

		return new CapturedCardFrame(window.Handle, CopyPremultipliedPixels(card), targetWidth, targetHeight, placeholder);
	}

	public CapturedCardFrame CaptureApplicationCard(IWindow window, int targetWidth, int targetHeight)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		targetWidth = Math.Max(32, targetWidth);
		targetHeight = Math.Max(24, targetHeight);
		using var card = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
		using var graphics = Graphics.FromImage(card);
		graphics.Clear(Color.Transparent);
		graphics.CompositingMode = CompositingMode.SourceOver;
		graphics.CompositingQuality = CompositingQuality.HighQuality;
		graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
		graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;

		var radius = Math.Max(8, (int)Math.Round(targetHeight * 0.10));
		using var clipPath = CreateRoundedRectangle(new Rectangle(1, 1, targetWidth - 2, targetHeight - 2), radius);
		graphics.SetClip(clipPath);
		using var cardBackground = new SolidBrush(Color.White);
		graphics.FillPath(cardBackground, clipPath);
		var iconSize = Math.Max(24, (int)Math.Round(Math.Min(targetHeight * 0.56, targetWidth * 0.40)));
		var iconBounds = new Rectangle(
			(targetWidth - iconSize) / 2,
			(targetHeight - iconSize) / 2,
			iconSize,
			iconSize);
		var icon = GetCachedIcon(window);
		if (icon is not null)
		{
			lock (icon)
				graphics.DrawImage(icon, iconBounds);
		}
		else
		{
			using var fallbackBrush = new SolidBrush(Color.FromArgb(255, 235, 239, 248));
			graphics.FillEllipse(fallbackBrush, iconBounds);
			using var font = new Font("Segoe UI", Math.Max(12, iconSize * 0.48f), FontStyle.Bold, GraphicsUnit.Pixel);
			using var textBrush = new SolidBrush(Color.FromArgb(255, 52, 65, 91));
			var letter = string.IsNullOrWhiteSpace(window.ProcessName) ? "?" : window.ProcessName[..1].ToUpperInvariant();
			var size = graphics.MeasureString(letter, font);
			graphics.DrawString(letter, font, textBrush, iconBounds.Left + (iconBounds.Width - size.Width) / 2, iconBounds.Top + (iconBounds.Height - size.Height) / 2);
		}

		graphics.ResetClip();
		using var border = new Pen(Color.FromArgb(105, 194, 202, 218), Math.Max(1, targetWidth / 220f));
		graphics.DrawPath(border, clipPath);
		return new CapturedCardFrame(window.Handle, CopyPremultipliedPixels(card), targetWidth, targetHeight, false);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		foreach (var icon in _icons.Values)
			icon?.Dispose();
		_icons.Clear();
	}

	private Bitmap? TryCaptureWindow(IntPtr handle)
	{
		if (!NativeMethods.IsWindow(handle) || !NativeMethods.GetWindowRect(handle, out var rectangle))
			return null;
		var width = rectangle.Right - rectangle.Left;
		var height = rectangle.Bottom - rectangle.Top;
		if (width < 2 || height < 2 || width > 8192 || height > 8192)
			return null;

		var memoryDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
		if (memoryDc == IntPtr.Zero)
			return null;

		var bitmapInfo = new BitmapInfo
		{
			Header = new BitmapInfoHeader
			{
				Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
				Width = width,
				Height = -height,
				Planes = 1,
				BitCount = 32,
				Compression = 0,
				SizeImage = (uint)(width * height * 4)
			}
		};
		var bitmapHandle = NativeMethods.CreateDIBSection(memoryDc, ref bitmapInfo, NativeMethods.DibRgbColors, out var bits, IntPtr.Zero, 0);
		if (bitmapHandle == IntPtr.Zero || bits == IntPtr.Zero)
		{
			NativeMethods.DeleteDC(memoryDc);
			return null;
		}

		var priorObject = NativeMethods.SelectObject(memoryDc, bitmapHandle);
		try
		{
			var captured = NativeMethods.PrintWindow(handle, memoryDc, NativeMethods.PwRenderFullContent);
			if (!captured)
				captured = NativeMethods.PrintWindow(handle, memoryDc, 0);
			if (!captured && NativeMethods.GetForegroundWindow() == handle)
			{
				var windowDc = NativeMethods.GetWindowDC(handle);
				try
				{
					captured = windowDc != IntPtr.Zero && NativeMethods.BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, NativeMethods.Srccopy);
				}
				finally
				{
					if (windowDc != IntPtr.Zero)
						NativeMethods.ReleaseDC(handle, windowDc);
				}
			}
			if (!captured)
				return null;

			var pixelCount = width * height;
			unsafe
			{
				var pixel = (byte*)bits;
				for (var index = 0; index < pixelCount; index++, pixel += 4)
					pixel[3] = 255;
			}

			if (IsUniformFrame(bits, pixelCount))
				return null;

			var copiedPixels = new byte[width * height * 4];
			Marshal.Copy(bits, copiedPixels, 0, copiedPixels.Length);
			var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
			var copyData = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
			try
			{
				for (var row = 0; row < height; row++)
					Marshal.Copy(copiedPixels, row * width * 4, copyData.Scan0 + row * copyData.Stride, width * 4);
			}
			finally
			{
				copy.UnlockBits(copyData);
			}
			return copy;
		}
		finally
		{
			NativeMethods.SelectObject(memoryDc, priorObject);
			NativeMethods.DeleteObject(bitmapHandle);
			NativeMethods.DeleteDC(memoryDc);
		}
	}

	private void DrawIconBadge(Graphics graphics, IWindow window, int width, int height)
	{
		var badgeSize = Math.Max(22, (int)Math.Round(height * 0.25));
		var padding = Math.Max(4, height / 20);
		var badgeBounds = new Rectangle(padding, height - badgeSize - padding, badgeSize, badgeSize);
		using var badgePath = CreateRoundedRectangle(badgeBounds, Math.Max(6, badgeSize / 3));
		using var badgeBrush = new SolidBrush(Color.FromArgb(218, 246, 248, 252));
		graphics.FillPath(badgeBrush, badgePath);

		var icon = GetCachedIcon(window);
		if (icon is not null)
		{
			lock (icon)
				graphics.DrawImage(icon, Rectangle.Inflate(badgeBounds, -Math.Max(3, badgeSize / 7), -Math.Max(3, badgeSize / 7)));
			return;
		}

		using var font = new Font("Segoe UI", Math.Max(8, badgeSize * 0.42f), FontStyle.Bold, GraphicsUnit.Pixel);
		using var textBrush = new SolidBrush(Color.FromArgb(235, 34, 39, 50));
		var letter = string.IsNullOrWhiteSpace(window.ProcessName) ? "?" : window.ProcessName[..1].ToUpperInvariant();
		var size = graphics.MeasureString(letter, font);
		graphics.DrawString(letter, font, textBrush, badgeBounds.Left + (badgeBounds.Width - size.Width) / 2, badgeBounds.Top + (badgeBounds.Height - size.Height) / 2);
	}

	private Bitmap? GetCachedIcon(IWindow window)
	{
		var key = string.IsNullOrWhiteSpace(window.ProcessExecutable) ? window.ProcessName : window.ProcessExecutable;
		_icons.TryGetValue(key, out var icon);
		if (icon is not null)
			return icon;
		var extracted = ExtractIcon(window);
		if (extracted is null)
			return null;
		if (!_icons.TryAdd(key, extracted))
			extracted.Dispose();
		_icons.TryGetValue(key, out icon);
		return icon;
	}

	private static void DrawCountBadge(Graphics graphics, string text, int width)
	{
		using var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
		var measured = graphics.MeasureString(text, font);
		var badge = new RectangleF(width - measured.Width - 14, 6, measured.Width + 8, measured.Height + 2);
		using var path = CreateRoundedRectangle(Rectangle.Round(badge), (int)Math.Round(badge.Height / 2));
		using var background = new SolidBrush(Color.FromArgb(225, 62, 93, 184));
		using var foreground = new SolidBrush(Color.White);
		graphics.FillPath(background, path);
		graphics.DrawString(text, font, foreground, badge.Left + 4, badge.Top + 1);
	}

	private static void DrawStatusBadge(Graphics graphics, string? text, int width, int height)
	{
		if (string.IsNullOrWhiteSpace(text))
			return;
		using var font = new Font("Segoe UI", Math.Max(9, height * 0.075f), FontStyle.Bold, GraphicsUnit.Pixel);
		var measured = graphics.MeasureString(text, font);
		var badge = new RectangleF(
			width - measured.Width - 18,
			8,
			measured.Width + 10,
			measured.Height + 4);
		using var path = CreateRoundedRectangle(Rectangle.Round(badge), Math.Max(5, (int)Math.Round(badge.Height / 2)));
		using var background = new SolidBrush(Color.FromArgb(205, 105, 112, 126));
		using var foreground = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
		graphics.FillPath(background, path);
		graphics.DrawString(text, font, foreground, badge.Left + 5, badge.Top + 2);
	}

	private static Bitmap? ExtractIcon(IWindow window)
	{
		if (window is not WindowsWindow concrete)
			return null;
		using var icon = concrete.ExtractIcon();
		return icon?.ToBitmap();
	}

	private static Rectangle AspectFillCrop(Size source, Size target)
	{
		var sourceAspect = source.Width / (double)source.Height;
		var targetAspect = target.Width / (double)target.Height;
		if (sourceAspect > targetAspect)
		{
			var width = Math.Max(1, (int)Math.Round(source.Height * targetAspect));
			return new Rectangle((source.Width - width) / 2, 0, width, source.Height);
		}

		var height = Math.Max(1, (int)Math.Round(source.Width / targetAspect));
		return new Rectangle(0, (source.Height - height) / 2, source.Width, height);
	}

	private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
	{
		var diameter = Math.Max(2, radius * 2);
		var path = new GraphicsPath();
		path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
		path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
		path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
		path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
		path.CloseFigure();
		return path;
	}

	private static unsafe bool IsUniformFrame(IntPtr bits, int pixelCount)
	{
		if (pixelCount < 2)
			return true;
		var pixels = (uint*)bits;
		var first = pixels[0] & 0x00FFFFFF;
		var step = Math.Max(1, pixelCount / 2048);
		var changes = 0;
		for (var index = step; index < pixelCount; index += step)
		{
			if ((pixels[index] & 0x00FFFFFF) != first && ++changes >= 4)
				return false;
		}
		return true;
	}

	private static byte[] CopyPremultipliedPixels(Bitmap bitmap)
	{
		var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
		var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try
		{
			var pixels = new byte[bitmap.Width * bitmap.Height * 4];
			for (var row = 0; row < bitmap.Height; row++)
				Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * bitmap.Width * 4, bitmap.Width * 4);
			for (var index = 0; index < pixels.Length; index += 4)
			{
				var alpha = pixels[index + 3];
				pixels[index] = (byte)(pixels[index] * alpha / 255);
				pixels[index + 1] = (byte)(pixels[index + 1] * alpha / 255);
				pixels[index + 2] = (byte)(pixels[index + 2] * alpha / 255);
			}
			return pixels;
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
	}
}
