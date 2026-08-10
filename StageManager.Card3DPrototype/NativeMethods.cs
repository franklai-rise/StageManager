using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

internal static class NativeMethods
{
	public const uint PwRenderFullContent = 0x00000002;
	public const int DibRgbColors = 0;
	public const uint Srccopy = 0x00CC0020;
	public const int SwMinimize = 6;
	public const int SwRestore = 9;
	public static readonly IntPtr HwndTop = IntPtr.Zero;
	public const uint SwpNoSize = 0x0001;
	public const uint SwpNoMove = 0x0002;
	public const uint SwpNoActivate = 0x0010;

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindow(IntPtr windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsIconic(IntPtr windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool IsWindowVisible(IntPtr windowHandle);

	[DllImport("user32.dll")]
	public static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	public static extern IntPtr GetWindowDC(IntPtr windowHandle);

	[DllImport("user32.dll")]
	public static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool BringWindowToTop(IntPtr windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetForegroundWindow(IntPtr windowHandle);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetWindowPos(
		IntPtr windowHandle,
		IntPtr insertAfter,
		int x,
		int y,
		int width,
		int height,
		uint flags);

	[DllImport("gdi32.dll")]
	public static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DeleteDC(IntPtr deviceContext);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DeleteObject(IntPtr graphicsObject);

	[DllImport("gdi32.dll")]
	public static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

	[DllImport("gdi32.dll")]
	public static extern IntPtr CreateDIBSection(
		IntPtr deviceContext,
		ref BitmapInfo bitmapInfo,
		uint usage,
		out IntPtr bits,
		IntPtr section,
		uint offset);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool BitBlt(
		IntPtr destination,
		int destinationX,
		int destinationY,
		int width,
		int height,
		IntPtr source,
		int sourceX,
		int sourceY,
		uint rasterOperation);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
	public int Left;
	public int Top;
	public int Right;
	public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
	public uint Size;
	public int Width;
	public int Height;
	public ushort Planes;
	public ushort BitCount;
	public uint Compression;
	public uint SizeImage;
	public int XPelsPerMeter;
	public int YPelsPerMeter;
	public uint ColorsUsed;
	public uint ColorsImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfo
{
	public BitmapInfoHeader Header;
	public uint Colors;
}
