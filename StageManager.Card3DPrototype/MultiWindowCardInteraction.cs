namespace StageManager.Card3DPrototype;

internal enum MultiWindowCardClickAction
{
	SelectWindow,
	Expand,
	Collapse,
	KeepExpanded
}

internal static class MultiWindowCardInteraction
{
	private static readonly TimeSpan HoverCollapseDelay = TimeSpan.FromMilliseconds(750);

	public static bool ShouldExpandOnHover(int windowCount, bool isExpandedStage, bool isPrimaryCard) =>
		windowCount > 1 && !isExpandedStage && isPrimaryCard;

	public static bool ShouldCollapseOnPointerLeave(bool pinned, TimeSpan elapsedSinceCard)
		=> !pinned && elapsedSinceCard >= HoverCollapseDelay;

	public static bool CanCollapseExpandedStage(bool pinned, bool force)
		=> !pinned || force;

	public static MultiWindowCardClickAction Decide(
		int windowCount,
		bool isExpandedStage,
		bool isPrimaryCard,
		bool hasPinnedExpandedStage = false)
	{
		if (windowCount <= 1)
			return MultiWindowCardClickAction.SelectWindow;
		if (hasPinnedExpandedStage)
		{
			if (isExpandedStage && isPrimaryCard)
				return MultiWindowCardClickAction.KeepExpanded;
			if (!isExpandedStage)
				return MultiWindowCardClickAction.SelectWindow;
		}
		if (!isExpandedStage)
			return MultiWindowCardClickAction.Expand;
		return isPrimaryCard
			? MultiWindowCardClickAction.Collapse
			: MultiWindowCardClickAction.SelectWindow;
	}

	public static ExpandedChildPage<T> CreateExpandedChildPage<T>(
		IReadOnlyList<T> windows,
		int requestedPage,
		int maximumChildrenPerPage)
	{
		ArgumentNullException.ThrowIfNull(windows);
		if (windows.Count == 0)
			throw new ArgumentException("An expanded stage requires at least one window.", nameof(windows));
		if (maximumChildrenPerPage < 1)
			throw new ArgumentOutOfRangeException(nameof(maximumChildrenPerPage));

		var pageCount = Math.Max(1, (int)Math.Ceiling(windows.Count / (double)maximumChildrenPerPage));
		var pageIndex = Math.Clamp(requestedPage, 0, pageCount - 1);
		var visibleChildren = windows
			.Skip(pageIndex * maximumChildrenPerPage)
			.Take(maximumChildrenPerPage)
			.ToArray();
		return new ExpandedChildPage<T>(visibleChildren, pageIndex, pageCount);
	}
}

internal sealed record ExpandedChildPage<T>(
	IReadOnlyList<T> VisibleChildren,
	int PageIndex,
	int PageCount);
