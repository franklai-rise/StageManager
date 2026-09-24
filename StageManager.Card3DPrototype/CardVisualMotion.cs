using System.Diagnostics;
using System.Numerics;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

// The hit geometry and compositor share a finite ease-out curve and start values.
// This keeps a moving card selectable without querying animated COM properties.
internal sealed class CardVisualMotion
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);
    private readonly Compositor _compositor;
    private readonly Visual _visual;
    private CardHoverTransform _from;
    private long _started;
	private TimeSpan _delay;
	private TimeSpan _duration = Duration;
	private bool _smooth;
    public CardHoverTransform Target { get; private set; }
    public bool Initialized { get; private set; }
	public bool IsWaiting => _started != 0 && Stopwatch.GetElapsedTime(_started) < _delay;
	internal TimeSpan AnimationDelay => _delay;
	internal TimeSpan AnimationDuration => _duration;
    public bool IsMoving => _started != 0 && Stopwatch.GetElapsedTime(_started) < _delay + _duration;
	public CardHoverTransform Current
	{
		get
		{
			if (_started == 0) return Target;
			var elapsed = Stopwatch.GetElapsedTime(_started);
			if (elapsed <= _delay) return _from;
			return Interpolate(_from, Target,
				(elapsed - _delay).TotalMilliseconds / _duration.TotalMilliseconds,
				_smooth);
		}
	}

    public CardVisualMotion(Compositor compositor, Visual visual)
    {
        _compositor = compositor;
        _visual = visual;
    }

	internal static CardHoverTransform Interpolate(CardHoverTransform from, CardHoverTransform to, double progress,
		bool smooth = false)
    {
		var clamped = Math.Clamp(progress, 0, 1);
		var eased = smooth
			? (float)(clamped * clamped * (3 - 2 * clamped))
			: (float)(1 - Math.Pow(1 - clamped, 3));
        return new CardHoverTransform(Vector3.Lerp(from.Offset, to.Offset, eased),
            Vector3.Lerp(from.Scale, to.Scale, eased), from.Angle + (to.Angle - from.Angle) * eased);
    }

	public void Set(Vector3 offset, Vector3 scale, float angle, bool animate,
		TimeSpan delay = default, TimeSpan? duration = null, bool smooth = false)
    {
        var target = new CardHoverTransform(offset, scale, angle);
		var requestedDuration = duration ?? Duration;
		if (Initialized && Target == target && _delay == delay && _duration == requestedDuration &&
			_smooth == smooth && (animate || !IsMoving))
            return;
        var start = Current;
        animate &= Initialized;
        Initialized = true;
        _from = start;
        Target = target;
		_delay = animate ? delay : TimeSpan.Zero;
		_duration = requestedDuration;
		_smooth = smooth;
        _started = animate ? Stopwatch.GetTimestamp() : 0;
        if (!animate)
        {
            _visual.StopAnimation(nameof(Visual.Offset));
            _visual.StopAnimation(nameof(Visual.Scale));
            _visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            _visual.Offset = offset;
            _visual.Scale = scale;
            _visual.RotationAngleInDegrees = angle;
            return;
        }
        // x(t) = t; y(t) = 1 - (1-t)^3, exactly matching Interpolate.
		using var easing = smooth
			? _compositor.CreateCubicBezierEasingFunction(new Vector2(1f / 3f, 0), new Vector2(2f / 3f, 1))
			: _compositor.CreateCubicBezierEasingFunction(new Vector2(1f / 3f, 1), new Vector2(2f / 3f, 1));
		AnimateVector(nameof(Visual.Offset), start.Offset, offset, easing, _delay, _duration);
		AnimateVector(nameof(Visual.Scale), start.Scale, scale, easing, _delay, _duration);
        if (start.Angle != angle)
        {
            using var animation = _compositor.CreateScalarKeyFrameAnimation();
			animation.Duration = _delay + _duration;
			animation.InsertKeyFrame(0, start.Angle);
			if (_delay > TimeSpan.Zero)
				animation.InsertKeyFrame((float)(_delay.TotalMilliseconds / animation.Duration.TotalMilliseconds), start.Angle);
            animation.InsertKeyFrame(1, angle, easing);
            _visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), animation);
        }
    }

	private void AnimateVector(string property, Vector3 from, Vector3 to, CompositionEasingFunction easing,
		TimeSpan delay, TimeSpan duration)
    {
        if (from == to) return;
        using var animation = _compositor.CreateVector3KeyFrameAnimation();
		animation.Duration = delay + duration;
        animation.InsertKeyFrame(0, from);
		if (delay > TimeSpan.Zero)
			animation.InsertKeyFrame((float)(delay.TotalMilliseconds / animation.Duration.TotalMilliseconds), from);
        animation.InsertKeyFrame(1, to, easing);
        _visual.StartAnimation(property, animation);
    }
}

internal sealed record CardHitProjection(
    CardVisualMotion Stage, CardVisualMotion Card, Vector2 Size, Vector2 Pivot,
    Vector2 CameraCenter, float PerspectiveDistance, Vector3? OverlayOffset = null, Vector2? OverlaySize = null)
{
    public bool IsMoving => Stage.IsMoving || Card.IsMoving;
	public bool IsInteractive => !Card.IsWaiting;
    public Vector2[] CurrentPolygon()
    {
        var stage = Stage.Current;
        var card = Card.Current;
        return OverlayOffset is { } overlay
            ? Card3DGeometry.ProjectCardOverlay(stage.Offset, stage.Scale.X, card.Offset, card.Scale,
                card.Angle, Size, Pivot, overlay, OverlaySize!.Value, CameraCenter, PerspectiveDistance)
            : Card3DGeometry.ProjectCard(stage.Offset, stage.Scale.X, card.Offset, card.Scale,
                card.Angle, Size, Pivot, CameraCenter, PerspectiveDistance);
    }
}
