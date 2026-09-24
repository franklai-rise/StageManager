using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal static class FocusEnhancedBehavior
{
	private const int MinimumReservedWidth = 96;

	public static bool UsesTransientSidebar(
		StageMode mode,
		bool maximizedOrFullScreen,
		bool exclusiveFullScreen,
		bool managedForeground = true)
	{
		return mode == StageMode.Focus
			? managedForeground && exclusiveFullScreen
			: maximizedOrFullScreen;
	}

	public static bool ShouldReserveSidebar(
		StageMode mode,
		bool sidebarVisible,
		bool transientSession,
		bool edgeRevealSession,
		bool manuallyCollapsed = false)
	{
		return mode == StageMode.Focus &&
			!transientSession &&
			(manuallyCollapsed || (sidebarVisible && !edgeRevealSession));
	}

	public static bool ShouldIdleHide(StageMode mode, bool idleAutoHideEnabled)
	{
		return mode != StageMode.Focus && idleAutoHideEnabled;
	}

	public static bool CanHideSidebar(StageMode mode, bool exclusiveFullScreenActive, bool manualCollapse = false)
	{
		return mode != StageMode.Focus || exclusiveFullScreenActive || manualCollapse;
	}

	public static bool ShouldPollHiddenSidebar(StageMode mode, bool pointerAtLeftEdge)
	{
		// A hidden Focus sidebar must continue observing the foreground window so it
		// can restore itself as soon as an exclusive full-screen session ends.
		return mode == StageMode.Focus || pointerAtLeftEdge;
	}

	public static bool ShouldShowCollapseButton(StageMode mode)
	{
		return true;
	}

	public static bool ShouldShowSidebarPinButton(StageMode mode, bool manuallyCollapsed, bool pinned) =>
		mode == StageMode.Focus && (manuallyCollapsed || pinned);

	public static bool ShouldApplyTransientHide(TransientSidebarAction action, bool pinned, bool exclusiveFullScreenActive) =>
		action == TransientSidebarAction.Hide && (!pinned || exclusiveFullScreenActive);

	public static bool ShouldRestoreAfterTransientSession(StageMode mode, bool wasVisibleBeforeSession, bool manuallyCollapsed = false)
	{
		return !manuallyCollapsed && (mode == StageMode.Focus || wasVisibleBeforeSession);
	}

	public static Rectangle GetSidebarHostArea(StageMode mode, Rectangle displayBounds, Rectangle workingArea)
	{
		return mode == StageMode.Focus ? displayBounds : workingArea;
	}

	public static int CalculateReservedWidth(float sidebarInteractionWidth, float dpiScale, int displayWidth)
	{
		var margin = Math.Max(8, (int)Math.Ceiling(12f * Math.Max(0.5f, dpiScale)));
		var desired = Math.Max(MinimumReservedWidth, (int)Math.Ceiling(sidebarInteractionWidth) + margin);
		var maximum = Math.Max(MinimumReservedWidth, (int)Math.Floor(Math.Max(1, displayWidth) * 0.45));
		return Math.Min(desired, maximum);
	}

	public static Rectangle CalculateAvailableWorkArea(Rectangle workArea, int reservedRight)
	{
		var left = Math.Clamp(reservedRight, workArea.Left, workArea.Right);
		return Rectangle.FromLTRB(left, workArea.Top, workArea.Right, workArea.Bottom);
	}

	public static Rectangle KeepWindowOutOfReservedColumn(Rectangle windowBounds, Rectangle availableWorkArea)
	{
		if (windowBounds.Width <= 0 || windowBounds.Height <= 0 ||
			availableWorkArea.Width <= 0 || availableWorkArea.Height <= 0 ||
			windowBounds.Left >= availableWorkArea.Left)
		{
			return windowBounds;
		}

		var width = Math.Min(windowBounds.Width, availableWorkArea.Width);
		return new Rectangle(
			availableWorkArea.Left,
			windowBounds.Top,
			Math.Max(1, width),
			windowBounds.Height);
	}
}
