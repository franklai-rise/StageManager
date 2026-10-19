using System.Numerics;

namespace StageManager.Card3DPrototype;

// All coordinates are device pixels, with DIP constants converted exactly once.
internal readonly record struct TopPanelLayout(float Left, float Top, float Width, float Height, int Columns, float StrideX, float StrideY)
{
    public static TopPanelLayout Calculate(Vector2 viewport, Vector2 card, float dpi, float contentHeight)
    {
        var gap = 26 * dpi;
        var available = Math.Max(card.X, viewport.X - 24 * dpi);
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (card.X + gap)));
        var width = columns * card.X + (columns - 1) * gap;
        var top = card.Y + 24 * dpi;
        var height = Math.Max(0, Math.Min(contentHeight, Math.Min(viewport.Y * .45f, viewport.Y - top - 12 * dpi)));
        return new((viewport.X - width) / 2, top, width, height, columns, card.X + gap, card.Y + gap);
    }
    public Vector3 Child(int index, float groupTop, float dpi) => new(Left + index % Columns * StrideX, groupTop + 40 * dpi + index / Columns * StrideY, 0);
    public float GroupHeight(int count, Vector2 card, float dpi) => 40 * dpi + (int)Math.Ceiling(count / (double)Columns) * StrideY - 26 * dpi + 26 * dpi;
}
