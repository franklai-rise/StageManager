namespace StageManager.Services;

public static class ManagedWindowPresence
{
	public static bool ShouldDisplay(bool isVisible, bool isMinimized)
	{
		return isVisible || isMinimized;
	}
}
