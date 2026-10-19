using System;
using System.Diagnostics;
using System.Drawing;

namespace StageManager.Card3DPrototype;

// Geometry only: no window, hit-test, timer or renderer dependencies.
internal sealed class HorizontalEdgeScrollController
{
	public const float ActivationWidthDip = 24f;
	public const float MinimumSpeedDip = 100f;
	public const float MaximumSpeedDip = 480f;
	public const double WheelCooldownSeconds = 0.250;
	private readonly Func<double> _monotonicSeconds;
	private double _wheelSuppressedUntil = double.NegativeInfinity;

	public HorizontalEdgeScrollController(Func<double>? monotonicSeconds = null)
	{
		_monotonicSeconds = monotonicSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
	}

	// Pixels/second in scroll-offset coordinates: left negative, right positive.
	public float Velocity { get; private set; }
	public bool IsWheelSuppressed => _monotonicSeconds() < _wheelSuppressedUntil;

	// Pointer and visible horizontal viewport band use the same pixel coordinate space.
	// dpiScale is DPI / 96. range MUST be the actual content bounds, without wheel
	// overscroll/extra travel; equal endpoints mean no overflow. The caller disables
	// this for vertical docks, hidden bars, blocked interactions, buttons, menus and AI.
	// Each tick: Update(...), then motion.MoveBounded(Velocity * dt, range).
	public float Update(PointF pointer, RectangleF viewportBand, float dpiScale,
		SidebarScrollRange range, bool enabled)
	{
		Stop();
		if (!enabled || !float.IsFinite(dpiScale) || dpiScale <= 0 ||
			!float.IsFinite(range.Minimum) || !float.IsFinite(range.Maximum) || range.Maximum <= range.Minimum ||
			!float.IsFinite(viewportBand.Left) || !float.IsFinite(viewportBand.Top) ||
			!float.IsFinite(viewportBand.Right) || !float.IsFinite(viewportBand.Bottom) ||
			viewportBand.Width <= 0 || viewportBand.Height <= 0 ||
			!viewportBand.Contains(pointer) || IsWheelSuppressed)
			return Velocity;

		var leftDistance = (pointer.X - viewportBand.Left) / dpiScale;
		var rightDistance = (viewportBand.Right - pointer.X) / dpiScale;
		// Narrow bands choose the nearest edge; their exact midpoint stays neutral.
		if (leftDistance == rightDistance)
			return Velocity;
		var distance = Math.Min(leftDistance, rightDistance);
		if (distance > ActivationWidthDip)
			return Velocity;
		var proximity = 1f - distance / ActivationWidthDip;
		Velocity = (leftDistance < rightDistance ? -1f : 1f) * dpiScale *
			(MinimumSpeedDip + (MaximumSpeedDip - MinimumSpeedDip) * proximity);
		return Velocity;
	}

	// Call before processing wheel input; never freezes or changes the wheel spring.
	public void SuppressForWheel()
	{
		_wheelSuppressedUntil = _monotonicSeconds() + WheelCooldownSeconds;
		Stop();
	}

	// Does not cancel wheel priority. Call immediately on leave/hide/block events.
	public void Stop() => Velocity = 0;
}
