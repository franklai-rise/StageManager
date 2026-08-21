namespace StageManager.Card3DPrototype;

internal static class SidebarIdleBehavior
{
	public static bool ShouldHide(bool enabled, int idleSeconds, DateTime lastInteractionUtc, DateTime nowUtc)
	{
		return enabled && nowUtc - lastInteractionUtc >= TimeSpan.FromSeconds(Math.Clamp(idleSeconds, 15, 600));
	}

	public static bool IsNearLeftEdge(Point screenPoint, Rectangle screenBounds, int activationWidth)
	{
		return screenPoint.Y >= screenBounds.Top &&
			screenPoint.Y < screenBounds.Bottom &&
			screenPoint.X >= screenBounds.Left &&
			screenPoint.X <= screenBounds.Left + Math.Max(1, activationWidth);
	}

	public static bool ShouldRequestHiddenEdgePoll(
		bool sidebarVisible,
		Point screenPoint,
		Rectangle screenBounds,
		int activationWidth) =>
		!sidebarVisible && IsNearLeftEdge(screenPoint, screenBounds, activationWidth);

	public static int GetHiddenEdgePollingInterval(bool largeWindowActive) =>
		largeWindowActive ? 50 : 100;
}
