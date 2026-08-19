namespace StageManager.Desktop;

internal static class WindowCapturePolicy
{
	public const int DefaultRefreshMinutes = 5;
	public const long MaximumSourcePixels = 16_000_000;

	public static bool CanCaptureSource(int width, int height) =>
		width >= 2 && height >= 2 && width <= 8192 && height <= 8192 &&
		(long)width * height <= MaximumSourcePixels;

	public static bool NeedsCapture(DateTime lastCaptureUtc, DateTime nowUtc, int refreshMinutes = DefaultRefreshMinutes)
	{
		if (lastCaptureUtc == DateTime.MinValue)
			return true;

		return nowUtc - lastCaptureUtc >= TimeSpan.FromMinutes(Math.Clamp(refreshMinutes, 1, 60));
	}
}
