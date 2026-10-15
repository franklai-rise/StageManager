namespace StageManager.Card3DPrototype.AiUsage;

internal enum AiPopupAction { None, Open, KeepOpen, FadeOut }

// Pure timing state: no focus changes, global hooks or per-frame window allocation.
internal sealed class AiUsagePopupController
{
	private DateTime? _entered, _left;
	private bool _suppressUntilExit;
	public void Dismiss() { _suppressUntilExit = true; _entered = _left = null; }
	public void Click() { _suppressUntilExit = false; _left = null; }
	public AiPopupAction Update(bool onCard, bool onPanel, bool open, DateTime now)
	{
		if (!onCard) { _entered = null; _suppressUntilExit = false; }
		if (open)
		{
			if (onCard || onPanel) { _left = null; return AiPopupAction.KeepOpen; }
			_left ??= now;
			return now - _left >= TimeSpan.FromMilliseconds(200) ? AiPopupAction.FadeOut : AiPopupAction.None;
		}
		_left = null;
		if (!onCard || _suppressUntilExit) return AiPopupAction.None;
		_entered ??= now;
		return now - _entered >= TimeSpan.FromMilliseconds(300) ? AiPopupAction.Open : AiPopupAction.None;
	}
}
