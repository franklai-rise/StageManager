using System;

namespace StageManager.Services;

public sealed record CardLayout(
	double CardWidth,
	double CardHeight,
	double PreviewWidth,
	double PreviewHeight,
	double IconHostHeight,
	double IconSize,
	double Gap,
	double Scale,
	bool RequiresScrolling);

public static class CardLayoutCalculator
{
	public static CardLayout Calculate(double availableHeight, int visibleCount, double preferenceScale)
	{
		preferenceScale = Math.Clamp(preferenceScale, 0.55, 1.25);
		availableHeight = Math.Max(1, availableHeight);
		visibleCount = Math.Max(0, visibleCount);
		var maximumWidth = 196 * preferenceScale;
		var maximumHeight = 122 * preferenceScale;
		var maximumGap = 8 * preferenceScale;
		var fitScale = visibleCount > 0
			? Math.Min(1, availableHeight / (visibleCount * (maximumHeight + maximumGap)))
			: 1;
		fitScale = Math.Max(0.55, fitScale);
		var stride = (maximumHeight + maximumGap) * fitScale;
		return new CardLayout(
			Math.Round(maximumWidth * fitScale, 1),
			Math.Round(maximumHeight * fitScale, 1),
			Math.Round(188 * preferenceScale * fitScale, 1),
			Math.Round(92 * preferenceScale * fitScale, 1),
			Math.Round(22 * preferenceScale * fitScale, 1),
			Math.Round(20 * preferenceScale * fitScale, 1),
			Math.Round(maximumGap * fitScale, 1),
			fitScale,
			visibleCount * stride > availableHeight + 0.5);
	}
}
