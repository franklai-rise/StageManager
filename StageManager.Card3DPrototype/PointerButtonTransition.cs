namespace StageManager.Card3DPrototype;

internal static class PointerButtonTransition
{
	public static bool DidStart(short state, ref bool wasDown)
	{
		var isDown = (state & 0x8000) != 0;
		var started = (state & 0x0001) != 0 || (isDown && !wasDown);
		wasDown = isDown;
		return started;
	}
}
