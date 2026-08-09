using StageManager.Infrastructure;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace StageManager.Services;

public sealed class VirtualDesktopService
{
	private readonly IVirtualDesktopManager? _manager;

	public VirtualDesktopService()
	{
		try
		{
			var type = Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"));
			if (type is not null)
				_manager = (IVirtualDesktopManager)Activator.CreateInstance(type)!;
		}
		catch (Exception ex)
		{
			AppLogger.Warn($"Virtual desktop integration is unavailable: {ex.Message}");
		}
	}

	public bool IsAvailable => _manager is not null;

	public bool IsWindowOnCurrentDesktop(IntPtr handle)
	{
		if (_manager is null || handle == IntPtr.Zero)
			return true;

		try
		{
			return _manager.IsWindowOnCurrentVirtualDesktop(handle, out var isCurrent) == 0 && isCurrent;
		}
		catch
		{
			return true;
		}
	}

	public Guid GetDesktopId(IntPtr handle)
	{
		if (_manager is null || handle == IntPtr.Zero)
			return Guid.Empty;

		try
		{
			return _manager.GetWindowDesktopId(handle, out var id) == 0 ? id : Guid.Empty;
		}
		catch
		{
			return Guid.Empty;
		}
	}

	public Guid GetCurrentDesktopId(IEnumerable<IWindow> windows, IntPtr foregroundHandle)
	{
		if (foregroundHandle != IntPtr.Zero && IsWindowOnCurrentDesktop(foregroundHandle))
		{
			var foregroundDesktop = GetDesktopId(foregroundHandle);
			if (foregroundDesktop != Guid.Empty)
				return foregroundDesktop;
		}

		var currentWindow = windows.FirstOrDefault(window => IsWindowOnCurrentDesktop(window.Handle));
		return currentWindow is null ? Guid.Empty : GetDesktopId(currentWindow.Handle);
	}

	[ComImport]
	[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IVirtualDesktopManager
	{
		[PreserveSig]
		int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

		[PreserveSig]
		int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

		[PreserveSig]
		int MoveWindowToDesktop(IntPtr topLevelWindow, [In] ref Guid desktopId);
	}

}
