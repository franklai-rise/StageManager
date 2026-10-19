using System.Numerics;

namespace StageManager.Card3DPrototype;

internal static class HorizontalCardLayout
{
    internal static int PageCount(int count) => (Math.Max(0, count) + 2) / 3;
    internal static RectangleF[] Rows(Vector2 size, int count, float dpi, bool header)
    {
        if (count is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(count));
        var pad = 4 * dpi;
        var top = header ? 24 * dpi : pad;
        var gap = 4 * dpi;
        var height = (size.Y - top - pad - (count - 1) * gap) / count;
        return Enumerable.Range(0, count).Select(i => new RectangleF(pad, top + i * (height + gap), size.X - 2 * pad, height)).ToArray();
    }
}
