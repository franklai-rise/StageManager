using System.Drawing;

namespace StageManager.Card3DPrototype;

// Temporal smoothing is independent of pointer polling frequency. Integrating
// the exponential exactly avoids different travel distances at 30/60/120 Hz.
internal sealed class EdgeScrollMotion
{
    internal const double ResponseSeconds = .075;
    public float Velocity { get; private set; }
    public void Stop() => Velocity = 0;

    public float Advance(float desired, double seconds, float position, SidebarScrollRange range, float dpi)
    {
        if (!float.IsFinite(desired) || !float.IsFinite(position) || !float.IsFinite(dpi) || dpi <= 0 ||
            !double.IsFinite(seconds) || seconds <= 0 || desired == 0 ||
            !float.IsFinite(range.Minimum) || !float.IsFinite(range.Maximum) || range.Maximum <= range.Minimum)
        { Stop(); return 0; }
        var remaining = desired < 0 ? position - range.Minimum : range.Maximum - position;
        if (remaining <= 0) { Stop(); return 0; }
        // A delayed UI frame must not make the dock jump to catch up afterwards.
        if (seconds > .12) { Stop(); seconds = 1d/60; }
        if (Math.Sign(Velocity) != Math.Sign(desired)) Velocity = 0;
        var target = Math.CopySign(Math.Min(Math.Abs(desired), Math.Sqrt(2 * 1800 * dpi * remaining)), desired);
        var previous = (double)Velocity;
        var decay = Math.Exp(-seconds / ResponseSeconds);
        Velocity = (float)(target + (previous - target) * decay);
        var delta = target * seconds + (previous - target) * ResponseSeconds * (1 - decay);
        return (float)Math.CopySign(Math.Min(Math.Abs(delta), remaining), desired);
    }
}

// Once edge travel begins, a card sliding underneath a stationary pointer is
// not a new hover gesture. Moving back inward or across into its controls is.
internal sealed class EdgeScrollIntent
{
    private bool _active;
    private int _direction;
    private bool _horizontal;
    private float _furthestAlong, _crossOrigin;
    public void Reset() => _active = false;
    public bool Allows(PointF pointer, bool horizontal, float desired, float dpi, bool protectedHit)
    {
        if (desired == 0) { Reset(); return false; }
        var direction = Math.Sign(desired);
        var along = horizontal ? pointer.X : pointer.Y;
        var cross = horizontal ? pointer.Y : pointer.X;
        if (_active && (_horizontal != horizontal || _direction != direction)) Reset();
        var deliberateHover = _active &&
            ((_furthestAlong-along)*direction >= 6*dpi || Math.Abs(cross-_crossOrigin) >= 8*dpi);
        if (protectedHit && (!_active || deliberateHover)) { Reset(); return false; }
        if (!_active) { _active=true; _direction=direction; _horizontal=horizontal; _furthestAlong=along; _crossOrigin=cross; }
        _furthestAlong = direction < 0 ? Math.Min(_furthestAlong,along) : Math.Max(_furthestAlong,along);
        return true;
    }
}
