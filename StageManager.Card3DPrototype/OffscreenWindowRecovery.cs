using StageManager.Native.Window;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StageManager.Card3DPrototype;

internal static class OffscreenWindowRecovery
{
	private const int MinimumVisibleWidth = 96;
	private const int MinimumVisibleHeight = 48;

	public static bool IsOffscreen(IWindow window)
	{
		if (!TryGetVisibilityBounds(window, out var bounds))
			return false;

		return !IsMeaningfullyVisible(bounds, Screen.AllScreens.Select(screen => screen.WorkingArea));
	}

	public static bool TryCenterIfOffscreen(IWindow window, Screen targetDisplay, bool restoreMaximized = false)
	{
		if (!NativeMethods.IsWindow(window.Handle) || !IsOffscreen(window))
			return false;

		var restoreBounds = TryGetNormalBounds(window.Handle, out var normalBounds)
			? normalBounds
			: GetCurrentBounds(window.Handle);
		var centered = CenterInWorkArea(restoreBounds, targetDisplay.WorkingArea);
		var wasMaximized = restoreMaximized || window.IsMaximized;

		NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwRestore);
		NativeMethods.SetWindowPos(
			window.Handle,
			IntPtr.Zero,
			centered.Left,
			centered.Top,
			centered.Width,
			centered.Height,
			NativeMethods.SwpNoZOrder |
			NativeMethods.SwpNoActivate |
			NativeMethods.SwpAsyncWindowPos);
		if (wasMaximized)
			NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwShowMaximized);
		window.NotifyUpdated();
		return true;
	}

	internal static bool IsMeaningfullyVisible(Rectangle bounds, IEnumerable<Rectangle> workAreas)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0)
			return false;

		var requiredWidth = Math.Min(MinimumVisibleWidth, bounds.Width);
		var requiredHeight = Math.Min(MinimumVisibleHeight, bounds.Height);
		return workAreas.Any(workArea =>
		{
			var intersection = Rectangle.Intersect(bounds, workArea);
			return intersection.Width >= requiredWidth && intersection.Height >= requiredHeight;
		});
	}

	internal static Rectangle CenterInWorkArea(Rectangle bounds, Rectangle workArea)
	{
		var fallbackWidth = Math.Max(1, workArea.Width * 2 / 3);
		var fallbackHeight = Math.Max(1, workArea.Height * 2 / 3);
		var minimumWidth = Math.Min(160, Math.Max(1, workArea.Width));
		var minimumHeight = Math.Min(120, Math.Max(1, workArea.Height));
		var width = Math.Clamp(bounds.Width > 0 ? bounds.Width : fallbackWidth, minimumWidth, Math.Max(minimumWidth, workArea.Width));
		var height = Math.Clamp(bounds.Height > 0 ? bounds.Height : fallbackHeight, minimumHeight, Math.Max(minimumHeight, workArea.Height));
		var x = workArea.Left + (workArea.Width - width) / 2;
		var y = workArea.Top + (workArea.Height - height) / 2;
		return new Rectangle(x, y, width, height);
	}

	private static bool TryGetVisibilityBounds(IWindow window, out Rectangle bounds)
	{
		if (window.IsMinimized && TryGetNormalBounds(window.Handle, out bounds))
			return true;

		bounds = GetCurrentBounds(window.Handle);
		return bounds.Width > 0 && bounds.Height > 0;
	}

	private static Rectangle GetCurrentBounds(IntPtr handle)
	{
		if (!NativeMethods.GetWindowRect(handle, out var rectangle))
			return Rectangle.Empty;
		return ToRectangle(rectangle);
	}

	private static bool TryGetNormalBounds(IntPtr handle, out Rectangle bounds)
	{
		var placement = new NativeWindowPlacement
		{
			Length = Marshal.SizeOf<NativeWindowPlacement>()
		};
		if (NativeMethods.GetWindowPlacement(handle, ref placement))
		{
			bounds = ToRectangle(placement.NormalPosition);
			if (bounds.Width > 0 && bounds.Height > 0)
				return true;
		}

		bounds = Rectangle.Empty;
		return false;
	}

	private static Rectangle ToRectangle(NativeRect rectangle)
	{
		return Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
	}
}
