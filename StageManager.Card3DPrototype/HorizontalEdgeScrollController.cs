using System;
using System.Diagnostics;
using System.Drawing;

namespace StageManager.Card3DPrototype;

// Geometry only: no window, hit-test, timer or renderer dependencies.
internal sealed class HorizontalEdgeScrollController
{
	public const float ActivationWidthDip = 24f;
	public const float VerticalActivationWidthDip = 80f;
	public const float MaximumSpeedDip = 480f;
	public const double WheelCooldownSeconds = 0.250;
	private readonly Func<double> _monotonicSeconds;
	private double _wheelSuppressedUntil = double.NegativeInfinity;

	public HorizontalEdgeScrollController(Func<double>? monotonicSeconds = null)
	{
		_monotonicSeconds = monotonicSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
	}

	// Pixels/second in scroll-offset coordinates: leading edge negative, trailing positive.
	public float Velocity { get; private set; }
	public bool IsWheelSuppressed => _monotonicSeconds() < _wheelSuppressedUntil;

	// Pointer and visible viewport band use the same pixel coordinate space.
	// dpiScale is DPI / 96. range MUST be the actual content bounds, without wheel
	// overscroll/extra travel; equal endpoints mean no overflow. The caller disables
	// this for hidden bars, blocked interactions, buttons, menus and AI.
	// Velocity is a desired speed. EdgeScrollMotion smooths/integrates it per frame.
	public float Update(PointF pointer, RectangleF viewportBand, float dpiScale,
		SidebarScrollRange range, bool enabled, bool horizontal = true)
	{
		Stop();
		if (!enabled || !float.IsFinite(dpiScale) || dpiScale <= 0 ||
			!float.IsFinite(range.Minimum) || !float.IsFinite(range.Maximum) || range.Maximum <= range.Minimum ||
			!float.IsFinite(viewportBand.Left) || !float.IsFinite(viewportBand.Top) ||
			!float.IsFinite(viewportBand.Right) || !float.IsFinite(viewportBand.Bottom) ||
			viewportBand.Width <= 0 || viewportBand.Height <= 0 ||
			!viewportBand.Contains(pointer) || IsWheelSuppressed)
			return Velocity;

		var leftDistance = (horizontal ? pointer.X - viewportBand.Left : pointer.Y - viewportBand.Top) / dpiScale;
		var rightDistance = (horizontal ? viewportBand.Right - pointer.X : viewportBand.Bottom - pointer.Y) / dpiScale;
		// Narrow bands choose the nearest edge; their exact midpoint stays neutral.
		if (leftDistance == rightDistance)
			return Velocity;
		var distance = Math.Min(leftDistance, rightDistance);
		var activationWidth = horizontal ? ActivationWidthDip : Math.Min(VerticalActivationWidthDip, viewportBand.Height / dpiScale / 2f);
		if (distance > activationWidth)
			return Velocity;
		var proximity = 1f - distance / activationWidth;
		// Both axes start gently. Vertical docks retain their wider area. Smoothstep has zero
		// slope at entry and at the outer edge, avoiding an abrupt speed step.
		var speed = MaximumSpeedDip * proximity * proximity * (3f - 2f * proximity);
		Velocity = (leftDistance < rightDistance ? -1f : 1f) * dpiScale * speed;
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

	// Unlike wheel travel, this permits only movement needed to reveal clipped content.
	internal static SidebarScrollRange ContentRange(float start, float height, float viewport, float margin)
	{
		if (!float.IsFinite(start) || !float.IsFinite(height) || !float.IsFinite(viewport) ||
			!float.IsFinite(margin) || height <= 0 || viewport <= margin * 2 || margin < 0)
			return new(0, 0);
		return new(Math.Min(0, start - margin), Math.Max(0, start + height - (viewport - margin)));
	}
}
