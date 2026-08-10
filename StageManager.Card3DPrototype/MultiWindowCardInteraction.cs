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
}
