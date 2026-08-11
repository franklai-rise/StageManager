namespace StageManager.Card3DPrototype;

internal static class WindowCapturePolicy
{
	public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

	public static bool NeedsCapture(DateTime lastCaptureUtc, DateTime nowUtc)
	{
		if (lastCaptureUtc == DateTime.MinValue)
			return true;

		return nowUtc - lastCaptureUtc >= RefreshInterval;
	}
}
