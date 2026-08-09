using StageManager.Native.PInvoke;
using System;
using System.Windows.Forms;

namespace StageManager.Services;

public static class FullScreenService
{
	public static bool IsExclusiveFullScreenOn(IntPtr handle, Screen display)
	{
		if (handle == IntPtr.Zero || !Win32.IsWindow(handle) || Win32.IsIconic(handle))
			return false;

		var rectangle = new Win32.Rect();
		if (!Win32.GetWindowRect(handle, ref rectangle))
			return false;

		var bounds = display.Bounds;
		const int tolerance = 2;
		return Math.Abs(rectangle.Left - bounds.Left) <= tolerance &&
			Math.Abs(rectangle.Top - bounds.Top) <= tolerance &&
			Math.Abs(rectangle.Right - bounds.Right) <= tolerance &&
			Math.Abs(rectangle.Bottom - bounds.Bottom) <= tolerance;
	}
}
