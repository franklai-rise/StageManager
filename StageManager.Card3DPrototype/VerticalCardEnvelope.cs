using System.Numerics;

namespace StageManager.Card3DPrototype;

// With the camera and card transforms fixed, each projected corner is a*y+b.
// Solve all corner constraints together instead of treating the front card's
// unprojected height as the space occupied by a three-dimensional stack.
internal sealed class VerticalCardEnvelope(float cameraY, float distance)
{
    private readonly List<(float A, float B)> _corners = new();
    public void Add(Vector2 size, Vector2 pivot, CardHoverTransform transform)
    {
        var sine = MathF.Sin(transform.Angle * MathF.PI / 180f);
        foreach (var corner in new[] { Vector2.Zero, new Vector2(size.X, 0), size, new Vector2(0, size.Y) })
        {
            var z = -(corner.X - pivot.X) * transform.Scale.X * sine + transform.Offset.Z;
            var a = distance / Math.Max(120f, distance - z);
            var b = cameraY + a * (pivot.Y + (corner.Y - pivot.Y) * transform.Scale.Y + transform.Offset.Y - cameraY);
            _corners.Add((a, b));
        }
    }
    public float OriginForTop(float top) => _corners.Max(c => (top - c.B) / c.A);
    public float Top(float origin) => _corners.Min(c => c.A * origin + c.B);
    public float Bottom(float origin) => _corners.Max(c => c.A * origin + c.B);
}
