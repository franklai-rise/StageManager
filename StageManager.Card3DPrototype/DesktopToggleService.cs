using System.Runtime.InteropServices;
using System.Text;

namespace StageManager.Card3DPrototype;

internal sealed class DesktopToggleService
{
	private const int WindowPlacementRestoreToMaximized = 0x0002;
	private readonly Func<bool, bool> _setDesktopVisibility;
	private readonly Func<IReadOnlyList<IntPtr>> _enumerateWindows;
	private readonly List<DesktopWindowState> _session = new();
	private IntPtr _previousForeground;

	public DesktopToggleService()
	{
		_enumerateWindows = EnumerateWindows;
		_setDesktopVisibility = SetDesktopVisibility;
	}

	internal DesktopToggleService(Func<bool, bool> setDesktopVisibility)
	{
		_enumerateWindows = Array.Empty<IntPtr>;
		_setDesktopVisibility = setDesktopVisibility;
	}

	internal DesktopToggleService(Func<IReadOnlyList<IntPtr>> enumerateWindows)
	{
		_enumerateWindows = enumerateWindows;
		_setDesktopVisibility = SetDesktopVisibility;
	}

	public bool IsDesktopShown { get; private set; }

	public void MarkDesktopDismissed()
	{
		IsDesktopShown = false;
		_session.Clear();
		_previousForeground = IntPtr.Zero;
	}

	public bool TryToggle(out string? error)
		=> TrySetDesktopShown(!IsDesktopShown, out error);

	public bool TryRestore(out string? error)
	{
		if (!IsDesktopShown)
		{
			error = null;
			return true;
		}
		return TrySetDesktopShown(false, out error);
	}

	private bool TrySetDesktopShown(bool showDesktop, out string? error)
	{
		try
		{
			if (!_setDesktopVisibility(showDesktop))
				throw new InvalidOperationException("Windows did not accept the desktop command.");
			IsDesktopShown = showDesktop;
			error = null;
			return true;
		}
		catch (Exception exception)
		{
			error = exception.InnerException?.Message ?? exception.Message;
			return false;
		}
	}

	private bool SetDesktopVisibility(bool showDesktop)
	{
		if (showDesktop)
		{
			_session.Clear();
			_previousForeground = NativeMethods.GetForegroundWindow();
			foreach (var handle in _enumerateWindows())
			{
				if (!IsMinimizableApplicationWindow(handle))
					continue;
				var placement = new NativeWindowPlacement { Length = Marshal.SizeOf<NativeWindowPlacement>() };
				if (NativeMethods.GetWindowPlacement(handle, ref placement))
				{
					NativeDesktopMethods.GetWindowThreadProcessId(handle, out var processId);
					var wasMaximized = NativeMethods.IsZoomed(handle) ||
						placement.ShowCommand == NativeMethods.SwShowMaximized ||
						(placement.Flags & WindowPlacementRestoreToMaximized) != 0;
					_session.Add(new DesktopWindowState(handle, processId, placement, wasMaximized));
				}
			}
			foreach (var window in _session)
				NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwMinimize);
			return true;
		}

		for (var index = _session.Count - 1; index >= 0; index--)
		{
			var window = _session[index];
			if (!NativeMethods.IsWindow(window.Handle))
				continue;
			NativeDesktopMethods.GetWindowThreadProcessId(window.Handle, out var currentProcessId);
			if (currentProcessId != window.ProcessId)
				continue;
			var placement = window.Placement;
			placement.Length = Marshal.SizeOf<NativeWindowPlacement>();
			placement.Flags = window.WasMaximized
				? placement.Flags | WindowPlacementRestoreToMaximized
				: placement.Flags & ~WindowPlacementRestoreToMaximized;
			placement.ShowCommand = window.WasMaximized
				? NativeMethods.SwShowMaximized
				: NativeMethods.SwRestore;
			NativeMethods.SetWindowPlacement(window.Handle, ref placement);
			// Some tray-style applications become hidden instead of remaining iconic.
			// Always issue the recorded restore command; checking IsIconic here races
			// with the asynchronous minimize request and can skip the window entirely.
			NativeMethods.ShowWindowAsync(window.Handle, placement.ShowCommand);
		}
		if (NativeMethods.IsWindow(_previousForeground))
		{
			NativeMethods.BringWindowToTop(_previousForeground);
			NativeMethods.SetForegroundWindow(_previousForeground);
		}
		_session.Clear();
		_previousForeground = IntPtr.Zero;
		return true;
	}

	private static IReadOnlyList<IntPtr> EnumerateWindows()
	{
		var windows = new List<IntPtr>();
		NativeDesktopMethods.EnumWindows((handle, _) =>
		{
			windows.Add(handle);
			return true;
		}, IntPtr.Zero);
		return windows;
	}

	private static bool IsMinimizableApplicationWindow(IntPtr handle)
	{
		if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
			return false;
		NativeDesktopMethods.GetWindowThreadProcessId(handle, out var processId);
		if (processId == Environment.ProcessId || NativeDesktopMethods.GetWindow(handle, NativeDesktopMethods.GwOwner) != IntPtr.Zero)
			return false;
		var style = NativeDesktopMethods.GetWindowStyle(handle);
		var extendedStyle = NativeDesktopMethods.GetWindowExtendedStyle(handle);
		if ((style & NativeDesktopMethods.WsChild) != 0 || (extendedStyle & NativeDesktopMethods.WsExToolWindow) != 0)
			return false;
		if (NativeDesktopMethods.IsCloaked(handle))
			return false;
		var className = NativeDesktopMethods.GetClassName(handle);
		return className is not "Progman" and not "WorkerW" and not "Shell_TrayWnd" and not "Shell_SecondaryTrayWnd";
	}

	private readonly record struct DesktopWindowState(
		IntPtr Handle,
		uint ProcessId,
		NativeWindowPlacement Placement,
		bool WasMaximized);
}

internal static class NativeDesktopMethods
{
	public const uint GwOwner = 4;
	public const long WsChild = 0x40000000L;
	public const long WsExToolWindow = 0x00000080L;
	private const int GwlStyle = -16;
	private const int GwlExStyle = -20;
	private const int DwmwaCloaked = 14;

	internal delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

	[DllImport("user32.dll")]
	internal static extern IntPtr GetWindow(IntPtr handle, uint command);

	[DllImport("user32.dll")]
	internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong32(IntPtr handle, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr handle, StringBuilder className, int capacity);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

	internal static long GetWindowStyle(IntPtr handle) => GetLong(handle, GwlStyle);
	internal static long GetWindowExtendedStyle(IntPtr handle) => GetLong(handle, GwlExStyle);
	private static long GetLong(IntPtr handle, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index).ToInt64() : GetWindowLong32(handle, index);

	internal static string GetClassName(IntPtr handle)
	{
		var value = new StringBuilder(256);
		GetClassName(handle, value, value.Capacity);
		return value.ToString();
	}

	internal static bool IsCloaked(IntPtr handle) =>
		DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
}
