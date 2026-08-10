using System.Numerics;

namespace StageManager.Card3DPrototype;

internal readonly record struct CardHoverTransform(Vector3 Offset, Vector3 Scale, float Angle);

internal static class Card3DGeometry
{
	public static CardHoverTransform CreateSubtleHoverTransform(int index, int count, bool hovered, float dpiScale)
	{
		var safeScale = Math.Max(0.75f, dpiScale);
		var middle = (Math.Max(1, count) - 1) / 2f;
		var offset = new Vector3(
			index * 4f * safeScale,
			(index - middle) * 0.75f * safeScale,
			index * 3f * safeScale);
		if (hovered)
			offset += new Vector3(1.5f * safeScale, -1f * safeScale, 8f * safeScale);

		var scale = hovered ? 1.015f : 1f;
		var angle = -7.5f - Math.Min(index, 4) * 0.20f + (hovered ? 0.75f : 0f);
		return new CardHoverTransform(offset, new Vector3(scale, scale, 1), angle);
	}

	public static Vector2[] ProjectCard(
		Vector3 stageOffset,
		float stageScale,
		Vector3 cardOffset,
		Vector3 cardScale,
		float angleDegrees,
		Vector2 cardSize,
		Vector2 pivot,
		Vector2 cameraCenter,
		float perspectiveDistance)
	{
		var corners = new[]
		{
			Vector2.Zero,
			new Vector2(cardSize.X, 0),
			new Vector2(cardSize.X, cardSize.Y),
			new Vector2(0, cardSize.Y)
		};
		var angle = MathF.PI * angleDegrees / 180f;
		var sine = MathF.Sin(angle);
		var cosine = MathF.Cos(angle);
		for (var index = 0; index < corners.Length; index++)
		{
			var localX = (corners[index].X - pivot.X) * cardScale.X;
			var localY = (corners[index].Y - pivot.Y) * cardScale.Y;
			var rotatedX = localX * cosine;
			var rotatedZ = -localX * sine;
			var worldX = stageOffset.X + (pivot.X + rotatedX + cardOffset.X) * stageScale;
			var worldY = stageOffset.Y + (pivot.Y + localY + cardOffset.Y) * stageScale;
			var worldZ = stageOffset.Z + (rotatedZ + cardOffset.Z) * stageScale;
			var denominator = Math.Max(120f, perspectiveDistance - worldZ);
			var factor = perspectiveDistance / denominator;
			corners[index] = new Vector2(
				cameraCenter.X + (worldX - cameraCenter.X) * factor,
				cameraCenter.Y + (worldY - cameraCenter.Y) * factor);
		}
		return corners;
	}

	public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
	{
		var inside = false;
		for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
		{
			var a = polygon[current];
			var b = polygon[previous];
			if ((a.Y > point.Y) == (b.Y > point.Y))
				continue;
			var intersectionX = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
			if (point.X < intersectionX)
				inside = !inside;
		}
		return inside;
	}
}
