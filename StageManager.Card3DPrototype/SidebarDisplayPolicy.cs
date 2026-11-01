using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal sealed record SidebarDisplayInfo(string DeviceName, string MonitorId, string Name, Rectangle Bounds, bool Primary);

internal static class SidebarDisplayPolicy
{
	public static SidebarDisplayInfo Select(IReadOnlyList<SidebarDisplayInfo> displays, SidebarDisplayMode mode, string monitorId, string deviceName)
	{
		if (displays.Count == 0) throw new ArgumentException("No connected displays.", nameof(displays));
		var primary = displays.FirstOrDefault(d => d.Primary) ?? SelectLeftmost(displays, d => d.Bounds);
		if (mode == SidebarDisplayMode.Primary) return primary;
		if (mode == SidebarDisplayMode.Specific)
		{
			// Never match a reused DISPLAY number when a monitor identity is known.
			// A missing chosen monitor falls back without overwriting its saved identity.
			var selected = string.IsNullOrEmpty(monitorId)
				? displays.FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
				: displays.FirstOrDefault(d => string.Equals(d.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));
			return selected ?? primary;
		}
		return SelectLeftmost(displays, d => d.Bounds);
	}

	public static T SelectLeftmost<T>(IEnumerable<T> displays, Func<T, Rectangle> getWorkingArea)
	{
		return displays
			.OrderBy(display => getWorkingArea(display).Left)
			.ThenBy(display => getWorkingArea(display).Top)
			.First();
	}
}
