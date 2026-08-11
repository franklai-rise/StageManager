namespace StageManager.Card3DPrototype;

internal static class WindowCapturePolicy
{
	public static bool NeedsInitialCapture(DateTime lastCaptureUtc) => lastCaptureUtc == DateTime.MinValue;
}
