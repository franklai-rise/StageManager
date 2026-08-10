namespace StageManager.Card3DPrototype;

internal enum WindowClickAction
{
	Ignore,
	Activate,
	Minimize
}

internal static class WindowClickBehavior
{
	public static WindowClickAction Decide(IntPtr selectedWindow, IntPtr foregroundWindow, bool isMinimized, bool exists)
	{
		if (!exists || selectedWindow == IntPtr.Zero)
			return WindowClickAction.Ignore;
		if (!isMinimized && selectedWindow == foregroundWindow)
			return WindowClickAction.Minimize;
		return WindowClickAction.Activate;
	}
}
