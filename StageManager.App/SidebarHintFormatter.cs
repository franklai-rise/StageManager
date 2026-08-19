namespace StageManager.Card3DPrototype;

internal static class SidebarHintFormatter
{
	public static string Format(bool autoHideEnabled, int idleSeconds)
	{
		if (!autoHideEnabled)
			return "Always visible  ·  Click the arrow to hide";

		var seconds = Math.Clamp(idleSeconds, 15, 600);
		var delay = seconds % 60 == 0
			? $"{seconds / 60} min"
			: $"{seconds} sec";
		return $"Auto-hides after {delay}  ·  Move to the left edge to show";
	}
}
