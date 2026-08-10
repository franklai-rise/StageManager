namespace StageManager.Card3DPrototype;

internal enum MultiWindowCardClickAction
{
	SelectWindow,
	Expand
}

internal static class MultiWindowCardInteraction
{
	public static MultiWindowCardClickAction Decide(int windowCount, bool isExpandedStage)
	{
		return windowCount > 1 && !isExpandedStage
			? MultiWindowCardClickAction.Expand
			: MultiWindowCardClickAction.SelectWindow;
	}
}
