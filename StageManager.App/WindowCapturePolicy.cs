namespace StageManager.Desktop;

internal static class WindowCapturePolicy
{
	public const int DefaultRefreshMinutes = 5;

	public static bool NeedsCapture(DateTime lastCaptureUtc, DateTime nowUtc, int refreshMinutes = DefaultRefreshMinutes)
	{
		if (lastCaptureUtc == DateTime.MinValue)
			return true;

		return nowUtc - lastCaptureUtc >= TimeSpan.FromMinutes(Math.Clamp(refreshMinutes, 1, 60));
	}
}
