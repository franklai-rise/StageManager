using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

// WH_MOUSE_LL executes on the installing thread, never on our renderer/UI.
internal sealed class SidebarMouseWheelHook : IDisposable
{
	internal const int WheelMessage = 0x8000 + 73;
	private const int WhMouseLl = 14;
	private const uint WmMouseWheel = 0x020A;
	private readonly HookProc _callback;
	private readonly Thread _thread;
	private WheelRegion _region = new(IntPtr.Zero, Rectangle.Empty, 0);
	private IntPtr _hook;
	private int _disposed, _pendingDelta, _messagePending;
	private long _installedAt, _lastWheelTimestamp;

	public SidebarMouseWheelHook()
	{
		_callback = HookCallback;
		_thread = new Thread(Run) { IsBackground = true, Name = "Sidebar mouse input" };
		_thread.SetApartmentState(ApartmentState.STA);
		_thread.Start();
	}

	// UI publishes immutable coordinates + heartbeat. Hook never touches controls.
	public void PublishRegion(IntPtr owner, Rectangle bounds) =>
		Volatile.Write(ref _region, new WheelRegion(owner, bounds, Stopwatch.GetTimestamp()));
	public bool IsActive => Volatile.Read(ref _hook) != IntPtr.Zero;
	public bool ReceivedWheelRecently(TimeSpan interval)
	{
		var timestamp = Interlocked.Read(ref _lastWheelTimestamp);
		return timestamp != 0 && Stopwatch.GetElapsedTime(timestamp) <= interval;
	}
	internal static bool CanHandle(Rectangle bounds, Point point, TimeSpan age) =>
		bounds.Contains(point) && age <= TimeSpan.FromMilliseconds(300);

	public int TakePendingDelta()
	{
		Interlocked.Exchange(ref _messagePending, 0);
		return Interlocked.Exchange(ref _pendingDelta, 0);
	}

	private void Run()
	{
		using var context = new ApplicationContext();
		using var timer = new System.Windows.Forms.Timer { Interval = 100 };
		timer.Tick += (_, _) =>
		{
			if (Volatile.Read(ref _disposed) != 0) { context.ExitThread(); return; }
			var region = Volatile.Read(ref _region);
			var enabled = region.Owner != IntPtr.Zero && !region.Bounds.IsEmpty &&
				Stopwatch.GetElapsedTime(region.Timestamp) <= TimeSpan.FromMilliseconds(300);
			// Windows may silently remove a hook. A handle alone cannot prove health.
			// Periodic re-registration bounds recovery without synthetic input.
			if (!enabled || (_hook != IntPtr.Zero && Stopwatch.GetElapsedTime(_installedAt).TotalSeconds >= 30))
				ReleaseHook();
			if (enabled && _hook == IntPtr.Zero)
			{
				_hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
				_installedAt = Stopwatch.GetTimestamp();
			}
		};
		try { timer.Start(); Application.Run(context); }
		finally { ReleaseHook(); }
	}

	private IntPtr HookCallback(int code, UIntPtr message, IntPtr data)
	{
		if (code >= 0 && message.ToUInt64() == WmMouseWheel && Volatile.Read(ref _disposed) == 0)
		{
			var region = Volatile.Read(ref _region);
			var input = Marshal.PtrToStructure<LowLevelMouseInput>(data);
			var delta = unchecked((short)(input.MouseData >> 16));
			if (delta != 0 && region.Owner != IntPtr.Zero && CanHandle(region.Bounds,
				new Point(input.Point.X, input.Point.Y), Stopwatch.GetElapsedTime(region.Timestamp)))
			{
				Interlocked.Add(ref _pendingDelta, delta);
				if (Interlocked.Exchange(ref _messagePending, 1) == 0 &&
					!PostMessage(region.Owner, WheelMessage, IntPtr.Zero, IntPtr.Zero))
				{
					Interlocked.Exchange(ref _pendingDelta, 0);
					Interlocked.Exchange(ref _messagePending, 0);
					return CallNextHookEx(_hook, code, message, data);
				}
				Interlocked.Exchange(ref _lastWheelTimestamp, Stopwatch.GetTimestamp());
				return (IntPtr)1;
			}
		}
		return CallNextHookEx(_hook, code, message, data);
	}

	private void ReleaseHook()
	{
		var hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
		if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
	}
	public void Dispose()
	{
		Interlocked.Exchange(ref _disposed, 1);
		PublishRegion(IntPtr.Zero, Rectangle.Empty);
		if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(1));
	}
	private sealed record WheelRegion(IntPtr Owner, Rectangle Bounds, long Timestamp);
	[StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
	[StructLayout(LayoutKind.Sequential)] private struct LowLevelMouseInput
	{
		public NativePoint Point;
		public uint MouseData, Flags, Time;
		public UIntPtr ExtraInfo;
	}
	private delegate IntPtr HookProc(int code, UIntPtr message, IntPtr data);
	[DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint threadId);
	[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
	[DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr message, IntPtr data);
	[DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
