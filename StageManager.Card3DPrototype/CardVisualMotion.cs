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
    public CardHoverTransform Target { get; private set; }
    public bool Initialized { get; private set; }
    public bool IsMoving => _started != 0 && Stopwatch.GetElapsedTime(_started) < Duration;
    public CardHoverTransform Current => _started == 0 ? Target :
        Interpolate(_from, Target, Stopwatch.GetElapsedTime(_started).TotalMilliseconds / Duration.TotalMilliseconds);

    public CardVisualMotion(Compositor compositor, Visual visual)
    {
        _compositor = compositor;
        _visual = visual;
    }

    internal static CardHoverTransform Interpolate(CardHoverTransform from, CardHoverTransform to, double progress)
    {
        var eased = (float)(1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3));
        return new CardHoverTransform(Vector3.Lerp(from.Offset, to.Offset, eased),
            Vector3.Lerp(from.Scale, to.Scale, eased), from.Angle + (to.Angle - from.Angle) * eased);
    }

    public void Set(Vector3 offset, Vector3 scale, float angle, bool animate)
    {
        var target = new CardHoverTransform(offset, scale, angle);
        if (Initialized && Target == target && (animate || !IsMoving))
            return;
        var start = Current;
        animate &= Initialized;
        Initialized = true;
        _from = start;
        Target = target;
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
        using var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(1f / 3f, 1), new Vector2(2f / 3f, 1));
        AnimateVector(nameof(Visual.Offset), start.Offset, offset, easing);
        AnimateVector(nameof(Visual.Scale), start.Scale, scale, easing);
        if (start.Angle != angle)
        {
            using var animation = _compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = Duration;
            animation.InsertKeyFrame(0, start.Angle);
            animation.InsertKeyFrame(1, angle, easing);
            _visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), animation);
        }
    }

    private void AnimateVector(string property, Vector3 from, Vector3 to, CompositionEasingFunction easing)
    {
        if (from == to) return;
        using var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = Duration;
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, to, easing);
        _visual.StartAnimation(property, animation);
    }
}

internal sealed record CardHitProjection(
    CardVisualMotion Stage, CardVisualMotion Card, Vector2 Size, Vector2 Pivot,
    Vector2 CameraCenter, float PerspectiveDistance, Vector3? OverlayOffset = null, Vector2? OverlaySize = null)
{
    public bool IsMoving => Stage.IsMoving || Card.IsMoving;
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
