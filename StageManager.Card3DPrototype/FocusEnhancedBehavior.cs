using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal static class FocusEnhancedBehavior
{
	private const int MinimumReservedWidth = 96;

	public static bool UsesTransientSidebar(
		StageMode mode,
		bool maximizedOrFullScreen,
		bool exclusiveFullScreen)
	{
		return mode == StageMode.Focus ? exclusiveFullScreen : maximizedOrFullScreen;
	}

	public static bool ShouldReserveSidebar(
		StageMode mode,
		bool sidebarVisible,
		bool transientSession,
		bool edgeRevealSession)
	{
		return mode == StageMode.Focus &&
			sidebarVisible &&
			!transientSession &&
			!edgeRevealSession;
	}

	public static bool ShouldIdleHide(StageMode mode, bool idleAutoHideEnabled)
	{
		return mode != StageMode.Focus && idleAutoHideEnabled;
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
