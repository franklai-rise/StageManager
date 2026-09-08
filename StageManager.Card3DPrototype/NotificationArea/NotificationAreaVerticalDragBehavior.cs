namespace StageManager.Card3DPrototype.NotificationArea;

internal static class NotificationAreaVerticalDragBehavior
{
	public static int CalculateOffset(int initialOffset, int verticalPixelDelta, float dpiScale)
	{
		var scale = Math.Max(0.75f, dpiScale);
		var logicalDelta = (int)Math.Round(verticalPixelDelta / scale, MidpointRounding.AwayFromZero);
		return Math.Clamp(initialOffset + logicalDelta, -2000, 0);
	}
}

internal static class NotificationAreaControlBehavior
{
	public static bool ShouldRefresh(bool refreshPressActive, bool releaseOverRefresh, bool dragActive) =>
		refreshPressActive && releaseOverRefresh && !dragActive;
}
