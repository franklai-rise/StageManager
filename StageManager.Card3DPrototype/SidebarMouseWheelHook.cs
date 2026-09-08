using System.Runtime.InteropServices;
using System.Diagnostics;

namespace StageManager.Card3DPrototype;

internal sealed class SidebarMouseWheelHook : IDisposable
{
	private const int WhMouseLl = 14;
	private const uint WmMouseWheel = 0x020A;
	private readonly HookProc _callback;
	private readonly Func<Point, int, bool> _wheelHandler;
	private IntPtr _hook;
	private long _lastWheelTimestamp;

	public SidebarMouseWheelHook(Func<Point, int, bool> wheelHandler)
	{
		_wheelHandler = wheelHandler;
		_callback = HookCallback;
		_hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
	}

	public bool IsActive => _hook != IntPtr.Zero;

	public bool ReceivedWheelRecently(TimeSpan interval)
	{
		var timestamp = Interlocked.Read(ref _lastWheelTimestamp);
		return timestamp != 0 && Stopwatch.GetElapsedTime(timestamp) <= interval;
	}

	private IntPtr HookCallback(int code, UIntPtr message, IntPtr data)
	{
		if (code >= 0 && message.ToUInt64() == WmMouseWheel)
		{
			Interlocked.Exchange(ref _lastWheelTimestamp, Stopwatch.GetTimestamp());
			var input = Marshal.PtrToStructure<LowLevelMouseInput>(data);
			var delta = unchecked((short)(input.MouseData >> 16));
			if (delta != 0 && _wheelHandler(new Point(input.Point.X, input.Point.Y), delta))
				return (IntPtr)1;
		}
		return CallNextHookEx(_hook, code, message, data);
	}

	public void Dispose()
	{
		if (_hook == IntPtr.Zero)
			return;
		UnhookWindowsHookEx(_hook);
		_hook = IntPtr.Zero;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct LowLevelMouseInput
	{
		public NativePoint Point;
		public uint MouseData;
		public uint Flags;
		public uint Time;
		public UIntPtr ExtraInfo;
	}

	private delegate IntPtr HookProc(int code, UIntPtr message, IntPtr data);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hook);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr message, IntPtr data);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? moduleName);
}
