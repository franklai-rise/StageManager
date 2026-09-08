using System.Drawing;

namespace StageManager.Card3DPrototype.NotificationArea;

internal static class NotificationTrayLayout
{
	public const int MaximumIcons = 16;

	public static IReadOnlyList<Rectangle> Arrange(Size cardSize, IReadOnlyList<Size> iconSizes, int reservedTop = 0)
	{
		var count = Math.Min(MaximumIcons, iconSizes.Count);
		if (count == 0 || cardSize.Width <= 0 || cardSize.Height <= 0)
			return [];
		var columns = Math.Min(4, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count))));
		var rows = (int)Math.Ceiling(count / (double)columns);
		var inset = Math.Max(6, (int)Math.Round(cardSize.Width * 0.075));
		var availableWidth = Math.Max(1, cardSize.Width - inset * 2);
		var contentTop = Math.Max(inset, reservedTop);
		var availableHeight = Math.Max(1, cardSize.Height - contentTop - inset);
		var cellWidth = availableWidth / (float)columns;
		var cellHeight = availableHeight / (float)rows;
		var rectangles = new List<Rectangle>(count);
		for (var index = 0; index < count; index++)
		{
			var iconSize = iconSizes[index];
			var column = index % columns;
			var row = index / columns;
			var itemsInRow = Math.Min(columns, count - row * columns);
			var rowOffset = (columns - itemsInRow) * cellWidth / 2f;
			var left = (int)Math.Round(inset + rowOffset + column * cellWidth + (cellWidth - iconSize.Width) / 2f);
			var top = (int)Math.Round(contentTop + row * cellHeight + (cellHeight - iconSize.Height) / 2f);
			rectangles.Add(new Rectangle(left, top, iconSize.Width, iconSize.Height));
		}
		return rectangles;
	}
}
