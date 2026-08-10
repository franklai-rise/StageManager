namespace StageManager.Card3DPrototype;

internal enum MultiWindowCardClickAction
{
	SelectWindow,
	Expand,
	Collapse
}

internal static class MultiWindowCardInteraction
{
	public static MultiWindowCardClickAction Decide(int windowCount, bool isExpandedStage, bool isPrimaryCard)
	{
		if (windowCount <= 1)
			return MultiWindowCardClickAction.SelectWindow;
		if (!isExpandedStage)
			return MultiWindowCardClickAction.Expand;
		return isPrimaryCard
			? MultiWindowCardClickAction.Collapse
			: MultiWindowCardClickAction.SelectWindow;
	}

	public static ExpandedWindowPage<T> CreateExpandedPage<T>(
		IReadOnlyList<T> windows,
		Func<T, IntPtr> getHandle,
		IntPtr preferredPrimaryHandle,
		int requestedPage,
		int maximumVisibleCards)
	{
		ArgumentNullException.ThrowIfNull(windows);
		ArgumentNullException.ThrowIfNull(getHandle);
		if (windows.Count == 0)
			throw new ArgumentException("An expanded stage requires at least one window.", nameof(windows));
		if (maximumVisibleCards < 2)
			throw new ArgumentOutOfRangeException(nameof(maximumVisibleCards));

		var primaryIndex = -1;
		for (var index = 0; index < windows.Count; index++)
		{
			if (getHandle(windows[index]) != preferredPrimaryHandle)
				continue;
			primaryIndex = index;
			break;
		}
		var primary = windows[primaryIndex >= 0 ? primaryIndex : 0];
		var primaryHandle = getHandle(primary);
		var children = windows.Where(window => getHandle(window) != primaryHandle).ToArray();
		var childrenPerPage = maximumVisibleCards - 1;
		var pageCount = Math.Max(1, (int)Math.Ceiling(children.Length / (double)childrenPerPage));
		var pageIndex = Math.Clamp(requestedPage, 0, pageCount - 1);
		var visibleCards = new List<T>(maximumVisibleCards) { primary };
		visibleCards.AddRange(children.Skip(pageIndex * childrenPerPage).Take(childrenPerPage));
		return new ExpandedWindowPage<T>(primary, visibleCards, pageIndex, pageCount);
	}
}

internal sealed record ExpandedWindowPage<T>(
	T Primary,
	IReadOnlyList<T> VisibleCards,
	int PageIndex,
	int PageCount);
