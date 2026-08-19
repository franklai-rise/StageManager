using StageManager.Settings;

namespace StageManager.Desktop;

internal static class SidebarDisplayPolicy
{
	public static T SelectLeftmost<T>(IEnumerable<T> displays, Func<T, Rectangle> getWorkingArea)
	{
		return displays
			.OrderBy(display => getWorkingArea(display).Left)
			.ThenBy(display => getWorkingArea(display).Top)
			.First();
	}

	public static T Select<T>(
		IEnumerable<T> displays,
		Func<T, Rectangle> getWorkingArea,
		Func<T, string> getIdentity,
		Func<T, bool> isPrimary,
		SidebarDisplayMode mode,
		string? selectedIdentity)
	{
		var available = displays.ToArray();
		if (available.Length == 0)
			throw new InvalidOperationException("At least one display is required.");

		if (mode == SidebarDisplayMode.Specific && !string.IsNullOrWhiteSpace(selectedIdentity))
		{
			var selected = available.FirstOrDefault(display =>
				string.Equals(getIdentity(display), selectedIdentity, StringComparison.OrdinalIgnoreCase));
			if (selected is not null)
				return selected;
		}
		if (mode == SidebarDisplayMode.Primary)
		{
			var primary = available.FirstOrDefault(isPrimary);
			if (primary is not null)
				return primary;
		}

		return SelectLeftmost(available, getWorkingArea);
	}
}
