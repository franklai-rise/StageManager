namespace StageManager.Card3DPrototype;

internal static class SidebarDisplayPolicy
{
	public static T SelectLeftmost<T>(IEnumerable<T> displays, Func<T, Rectangle> getWorkingArea)
	{
		return displays
			.OrderBy(display => getWorkingArea(display).Left)
			.ThenBy(display => getWorkingArea(display).Top)
			.First();
	}
}
