namespace StageManager.Desktop;

internal enum TransientSidebarAction
{
	None,
	Reveal,
	Hide
}

internal static class TransientSidebarBehavior
{
	private static readonly TimeSpan LeaveGracePeriod = TimeSpan.FromMilliseconds(300);

	public static TransientSidebarAction Decide(
		bool largeWindowActive,
		bool sidebarVisible,
		bool pointerAtLeftEdge,
		bool pointerNearSidebar,
		DateTime revealedUtc,
		DateTime nowUtc)
	{
		if (!largeWindowActive)
			return TransientSidebarAction.None;
		if (!sidebarVisible)
			return pointerAtLeftEdge ? TransientSidebarAction.Reveal : TransientSidebarAction.None;
		if (pointerNearSidebar || nowUtc - revealedUtc < LeaveGracePeriod)
			return TransientSidebarAction.None;
		return TransientSidebarAction.Hide;
	}
}
