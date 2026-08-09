using System;
using System.Runtime.InteropServices;

namespace StageManager.Services;

internal static class BackdropService
{
	private const int DwmwaUseImmersiveDarkMode = 20;
	private const int DwmwaWindowCornerPreference = 33;
	private const int DwmwaSystemBackdropType = 38;

	public static void Apply(IntPtr handle)
	{
		if (handle == IntPtr.Zero)
			return;
		var enabled = 1;
		var rounded = 2;
		var mica = 2;
		DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
		DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
		DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref mica, sizeof(int));
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
