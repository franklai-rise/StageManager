namespace StageManager.Card3DPrototype;

internal enum WindowClickAction
{
	Ignore,
	ActivatePreservingPlacement,
	RestoreAndActivate,
	Minimize
}

internal static class WindowClickBehavior
{
	public static bool ShouldProcessPointerPress(int clickCount) => clickCount == 1;

	public static WindowClickAction Decide(IntPtr selectedWindow, IntPtr foregroundWindow, bool isCurrentlyMinimized, bool exists, bool allowMinimize = true)
	{
		if (!exists || selectedWindow == IntPtr.Zero)
			return WindowClickAction.Ignore;
		if (isCurrentlyMinimized)
			return WindowClickAction.RestoreAndActivate;
		return allowMinimize && selectedWindow == foregroundWindow
			? WindowClickAction.Minimize
			: WindowClickAction.ActivatePreservingPlacement;
	}
}

internal sealed record PendingCardClick(CardHitTarget Target, WindowClickAction? Action);

internal sealed class CardClickGesture
{
	private Point _downPoint;
	private long _pressStarted;
	private string? _lastClickKey;
	private Point _lastClickPoint;
	private long _lastClickStarted;
	public PendingCardClick? Pending { get; private set; }
	public bool IsPressed { get; private set; }

	public bool Begin(CardHitTarget target, WindowClickAction? action, int clicks, Point point,
		long now, int doubleClickTime, Size doubleClickSize)
	{
		// Execute the first click on release. The system interval only rejects a
		// second click; it no longer delays activation. Keep this fallback because
		// changing the foreground window can reset native double-click reporting.
		var isDoubleClick = clicks != 1 || (_lastClickKey == CardInteraction.Key(target) &&
			now >= _lastClickStarted && now - _lastClickStarted <= Math.Max(1, doubleClickTime) &&
			Within(point, _lastClickPoint, doubleClickSize));
		Cancel();
		if (isDoubleClick)
			return false;
		Pending = new(target, action);
		IsPressed = true;
		_downPoint = point;
		_pressStarted = now;
		return true;
	}

	public bool IsDrag(Point point, Size dragSize) => IsPressed && !Within(point, _downPoint, dragSize);

	public bool Release(CardHitTarget? target, Point point, Size dragSize)
	{
		if (!IsPressed || Pending is null)
			return false;
		IsPressed = false;
		if (!CardInteraction.SameTarget(Pending.Target, target) || !Within(point, _downPoint, dragSize))
		{
			Cancel();
			return false;
		}
		return true;
	}

	public PendingCardClick? Take()
	{
		if (IsPressed)
			return null;
		var target = Pending;
		if (target is not null)
		{
			_lastClickKey = CardInteraction.Key(target.Target);
			_lastClickPoint = _downPoint;
			_lastClickStarted = _pressStarted;
		}
		Cancel();
		return target;
	}

	public void Cancel() { Pending = null; IsPressed = false; }

	private static bool Within(Point point, Point origin, Size size) =>
		Math.Abs((long)point.X - origin.X) <= Math.Max(1, size.Width / 2) &&
		Math.Abs((long)point.Y - origin.Y) <= Math.Max(1, size.Height / 2);
}
