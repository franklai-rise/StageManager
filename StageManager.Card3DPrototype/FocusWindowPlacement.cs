using StageManager.Native.Window;

namespace StageManager.Card3DPrototype;

internal static class FocusWindowPlacement
{
	public static bool TryKeepOutOfReservedColumn(IWindow window, Screen sidebarDisplay, int reservedRight)
	{
		ArgumentNullException.ThrowIfNull(window);
		ArgumentNullException.ThrowIfNull(sidebarDisplay);
		if (reservedRight <= sidebarDisplay.Bounds.Left ||
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
		var availableWorkArea = FocusEnhancedBehavior.CalculateAvailableWorkArea(
			sidebarDisplay.WorkingArea,
			reservedRight);
		var adjustedBounds = FocusEnhancedBehavior.KeepWindowOutOfReservedColumn(
			currentBounds,
			availableWorkArea);
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
