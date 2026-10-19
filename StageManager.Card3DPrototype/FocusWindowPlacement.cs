using StageManager.Native.Window;
using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal static class FocusWindowPlacement
{
	public static bool TryKeepOutOfReservedColumn(IWindow window, Screen sidebarDisplay, Rectangle reserved, SidebarEdge edge)
	{
		ArgumentNullException.ThrowIfNull(window);
		ArgumentNullException.ThrowIfNull(sidebarDisplay);
		if (reserved.IsEmpty ||
			!NativeMethods.IsWindow(window.Handle) ||
			!NativeMethods.IsWindowVisible(window.Handle) ||
			NativeMethods.IsIconic(window.Handle) ||
			NativeMethods.IsZoomed(window.Handle) ||
			!string.Equals(
				Screen.FromHandle(window.Handle).DeviceName,
				sidebarDisplay.DeviceName,
				StringComparison.OrdinalIgnoreCase) ||
			!NativeMethods.GetWindowRect(window.Handle, out var nativeBounds))
		{
			return false;
		}

		var currentBounds = Rectangle.FromLTRB(
			nativeBounds.Left,
			nativeBounds.Top,
			nativeBounds.Right,
			nativeBounds.Bottom);
		var availableWorkArea = SidebarDockLayout.AvailableArea(sidebarDisplay.WorkingArea, reserved, edge);
		var adjustedBounds = SidebarDockLayout.KeepOutside(currentBounds, availableWorkArea, edge);
		if (adjustedBounds == currentBounds)
			return false;

		return NativeMethods.SetWindowPos(
			window.Handle,
			IntPtr.Zero,
			adjustedBounds.Left,
			adjustedBounds.Top,
			adjustedBounds.Width,
			adjustedBounds.Height,
			NativeMethods.SwpNoZOrder |
			NativeMethods.SwpNoActivate |
			NativeMethods.SwpAsyncWindowPos);
	}
}
