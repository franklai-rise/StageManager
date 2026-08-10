namespace StageManager.Card3DPrototype;

internal enum FullScreenSidebarAction
{
	None,
	Reveal,
	Hide
}

internal static class FullScreenSidebarBehavior
{
	private static readonly TimeSpan LeaveGracePeriod = TimeSpan.FromMilliseconds(300);

	public static FullScreenSidebarAction Decide(
		bool fullScreenActive,
		bool sidebarVisible,
		bool pointerAtLeftEdge,
		bool pointerNearSidebar,
		DateTime revealedUtc,
		DateTime nowUtc)
	{
		if (!fullScreenActive)
			return FullScreenSidebarAction.None;
		if (!sidebarVisible)
			return pointerAtLeftEdge ? FullScreenSidebarAction.Reveal : FullScreenSidebarAction.None;
		if (pointerNearSidebar || nowUtc - revealedUtc < LeaveGracePeriod)
			return FullScreenSidebarAction.None;
		return FullScreenSidebarAction.Hide;
	}
}
