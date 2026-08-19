using StageManager.Model;
using StageManager.Infrastructure;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StageManager.Native;

public sealed class WindowsWindow : IWindow
{
	private readonly IntPtr _handle;
	private IWindowLocation? _lastLocation;
	private int _processId = -1;
	private string _processName = string.Empty;
	private string _processFileName = string.Empty;
	private string _processExecutable = string.Empty;
	private string? _appUserModelId;

	public WindowsWindow(IntPtr handle, VirtualDesktopService? virtualDesktops = null)
	{
		_handle = handle;
		ResolveProcess();
		var desktopId = virtualDesktops?.GetDesktopId(handle) ?? Guid.Empty;
		Identity = new WindowIdentity(handle, ProcessId, ProcessName, ProcessExecutable, AppUserModelId, Class, desktopId);
	}

	public event IWindowDelegate? WindowClosed;
	public event IWindowDelegate? WindowUpdated;
	public event IWindowDelegate? WindowFocused;

	public WindowIdentity Identity { get; private set; }
	public IntPtr Handle => _handle;
	public int ProcessId => _processId;
	public string ProcessFileName => _processFileName;
	public string ProcessName => _processName;
	public string ProcessExecutable => _processExecutable;
	public string? AppUserModelId => _appUserModelId;

	public string Title
	{
		get
		{
			var buffer = new StringBuilder(512);
			Win32.GetWindowText(_handle, buffer, buffer.Capacity);
			return buffer.ToString().Trim();
		}
	}

	public string Class
	{
		get
		{
			var buffer = new StringBuilder(256);
			Win32.GetClassName(_handle, buffer, buffer.Capacity);
			return buffer.ToString();
		}
	}

	public IWindowLocation Location
	{
		get
		{
			var rectangle = new Win32.Rect();
			Win32.GetWindowRect(_handle, ref rectangle);
			var state = IsMinimized ? WindowState.Minimized : IsMaximized ? WindowState.Maximized : WindowState.Normal;
			return new WindowLocation(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, state);
		}
	}

	public Rectangle Offset
	{
		get
		{
			var standard = new Win32.Rect();
			Win32.GetWindowRect(_handle, ref standard);
			var extended = new Win32.Rect();
			var size = Marshal.SizeOf<Win32.Rect>();
			Win32.DwmGetWindowAttribute(_handle, (int)Win32.DwmWindowAttribute.DWMWA_EXTENDED_FRAME_BOUNDS, out extended, size);
			return new Rectangle(
				standard.Left - extended.Left,
				standard.Top - extended.Top,
				(standard.Right - standard.Left) - (extended.Right - extended.Left),
				(standard.Bottom - standard.Top) - (extended.Bottom - extended.Top));
		}
	}

	public bool CanLayout => Win32.IsWindow(_handle) && Win32Helper.IsAppWindow(_handle) && Win32Helper.IsAltTabWindow(_handle);
	public bool IsFocused => Win32.GetForegroundWindow() == _handle;
	public bool IsMinimized => Win32.IsIconic(_handle);
	public bool IsMaximized => Win32.IsZoomed(_handle);
	public bool IsMouseMoving { get; internal set; }

	public bool IsCandidate() => CanLayout;

	public void RefreshIdentity(VirtualDesktopService virtualDesktops)
	{
		Identity = Identity with { VirtualDesktopId = virtualDesktops.GetDesktopId(_handle) };
	}

	public void StoreLastLocation() => _lastLocation = Location;

	public IWindowLocation? PopLastLocation()
	{
		var value = _lastLocation;
		_lastLocation = null;
		return value;
	}

	public void Focus()
	{
		if (!IsFocused && Win32.IsWindow(_handle))
		{
			if (Win32Helper.ForceForegroundWindow(_handle))
				WindowFocused?.Invoke(this);
			else
				AppLogger.Warn($"Foreground activation was rejected for {ProcessName} window {_handle} ({Title}).");
		}
	}

	public void ShowNormal()
	{
		Win32.ShowWindowAsync(_handle, Win32.SW.SW_SHOWNOACTIVATE);
	}

	public void ShowMaximized()
	{
		Win32.ShowWindowAsync(_handle, Win32.SW.SW_SHOWMAXIMIZED);
	}

	public void ShowMinimized()
	{
		Win32.ShowWindowAsync(_handle, Win32.SW.SW_SHOWMINNOACTIVE);
	}

	public void ShowInCurrentState()
	{
		if (IsMaximized)
			ShowMaximized();
		else if (IsMinimized)
			Win32.ShowWindowAsync(_handle, Win32.SW.SW_RESTORE);
		else
			ShowNormal();
		WindowUpdated?.Invoke(this);
	}

	public void BringToTop()
	{
		if (Win32.IsWindow(_handle))
			Win32.BringWindowToTop(_handle);
		WindowUpdated?.Invoke(this);
	}

	public void Close()
	{
		Win32Helper.QuitApplication(_handle);
		WindowClosed?.Invoke(this);
	}

	public void NotifyUpdated() => WindowUpdated?.Invoke(this);

	public Icon? ExtractIcon()
	{
		if (string.IsNullOrWhiteSpace(_processExecutable))
			return null;
		try
		{
			return Icon.ExtractAssociatedIcon(_processExecutable);
		}
		catch
		{
			return null;
		}
	}

	public override string ToString() => $"[{Handle}][{Title}][{Class}][{ProcessName}]";

	private void ResolveProcess()
	{
		try
		{
			Win32.GetWindowThreadProcessId(_handle, out var processId);
			using var frameProcess = Process.GetProcessById((int)processId);
			var resolvedId = ResolvePackagedChildProcess(frameProcess);
			using var process = resolvedId == frameProcess.Id ? Process.GetProcessById(frameProcess.Id) : Process.GetProcessById(resolvedId);
			_processId = process.Id;
			_processName = process.ProcessName;
			try
			{
				_processExecutable = process.MainModule?.FileName ?? string.Empty;
				_processFileName = Path.GetFileName(_processExecutable);
			}
			catch
			{
				_processFileName = process.ProcessName + ".exe";
			}
			_appUserModelId = TryGetApplicationUserModelId((uint)process.Id);
		}
		catch
		{
			_processId = -1;
			_processName = string.Empty;
			_processFileName = string.Empty;
			_processExecutable = string.Empty;
		}
	}

	private int ResolvePackagedChildProcess(Process frameProcess)
	{
		if (!frameProcess.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
			return frameProcess.Id;

		var resolvedId = frameProcess.Id;
		Win32.EnumChildWindows(_handle, (child, _) =>
		{
			Win32.GetWindowThreadProcessId(child, out var childProcessId);
			if (childProcessId != 0 && childProcessId != frameProcess.Id)
			{
				resolvedId = (int)childProcessId;
				return false;
			}
			return true;
		}, IntPtr.Zero);
		return resolvedId;
	}

	private static string? TryGetApplicationUserModelId(uint processId)
	{
		var processHandle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
		if (processHandle == IntPtr.Zero)
			return null;

		try
		{
			uint length = 0;
			if (Win32.GetApplicationUserModelId(processHandle, ref length, null) != Win32.ERROR_INSUFFICIENT_BUFFER || length == 0)
				return null;
			var value = new StringBuilder((int)length);
			return Win32.GetApplicationUserModelId(processHandle, ref length, value) == Win32.ERROR_SUCCESS ? value.ToString() : null;
		}
		finally
		{
			Win32.CloseHandle(processHandle);
		}
	}
}
