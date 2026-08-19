namespace StageManager.Desktop;

internal static class AccessibilityPreferences
{
	public static bool SystemAnimationsEnabled
	{
		get
		{
			return !NativeMethods.SystemParametersInfo(
				NativeMethods.SpiGetClientAreaAnimation,
				0,
				out var enabled,
				0) || enabled;
		}
	}

	public static bool ShouldAnimate(bool userEnabled, bool highContrast, bool systemAnimationsEnabled) =>
		userEnabled && !highContrast && systemAnimationsEnabled;
}
