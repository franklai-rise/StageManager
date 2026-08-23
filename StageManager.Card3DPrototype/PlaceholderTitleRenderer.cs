using System.Globalization;
using System.Text;
using System.Drawing.Text;

namespace StageManager.Card3DPrototype;

internal static class PlaceholderTitleRenderer
{
	internal const string ChineseFontFamily = "华文中宋";
	internal const string LatinFontFamily = "Times New Roman";
	private const int MaximumTitleWidthUnits = 14;

	public static void Draw(Graphics graphics, string? title, string? fallbackTitle, Rectangle bounds)
	{
		ArgumentNullException.ThrowIfNull(graphics);
		var text = FormatTitle(title, fallbackTitle);
		if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 0 || bounds.Height <= 0)
			return;

		var runs = CreateRuns(text);
		var fontSize = Math.Clamp(bounds.Height * 0.16f, 11f, 24f);
		var maximumWidth = Math.Max(1f, bounds.Width * 0.78f);
		var priorHint = graphics.TextRenderingHint;
		graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		try
		{
			using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
			format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
			using var brush = new SolidBrush(Color.FromArgb(226, 48, 53, 64));

			var fonts = CreateFonts(fontSize);
			try
			{
				var measurements = MeasureRuns(graphics, runs, fonts, format);
				var totalWidth = measurements.Sum(item => item.Size.Width);
				if (totalWidth > maximumWidth && fontSize > 9f)
				{
					fonts.Dispose();
					fontSize = Math.Max(9f, fontSize * maximumWidth / totalWidth);
					fonts = CreateFonts(fontSize);
					measurements = MeasureRuns(graphics, runs, fonts, format);
					totalWidth = measurements.Sum(item => item.Size.Width);
				}

				var x = bounds.Left + (bounds.Width - totalWidth) / 2f;
				foreach (var measurement in measurements)
				{
					var y = bounds.Top + (bounds.Height - measurement.Size.Height) / 2f;
					graphics.DrawString(measurement.Run.Text, measurement.Font, brush, new PointF(x, y), format);
					x += measurement.Size.Width;
				}
			}
			finally
			{
				fonts.Dispose();
			}
		}
		finally
		{
			graphics.TextRenderingHint = priorHint;
		}
	}

	internal static string FormatTitle(string? title, string? fallbackTitle)
	{
		var normalized = NormalizeWhitespace(title);
		if (normalized.Length == 0)
			normalized = NormalizeWhitespace(fallbackTitle);
		if (normalized.Length == 0)
			return string.Empty;

		var output = new StringBuilder();
		var usedUnits = 0;
		var truncated = false;
		var elements = StringInfo.GetTextElementEnumerator(normalized);
		while (elements.MoveNext())
		{
			var element = elements.GetTextElement();
			var units = UsesChineseFont(element) ? 2 : 1;
			if (usedUnits + units > MaximumTitleWidthUnits)
			{
				truncated = true;
				break;
			}
			output.Append(element);
			usedUnits += units;
		}

		if (truncated)
			output.Append("...");
		return output.ToString();
	}

	internal static string GetPreferredFontFamily(string textElement) =>
		UsesChineseFont(textElement) ? ChineseFontFamily : LatinFontFamily;

	private static string NormalizeWhitespace(string? value) =>
		string.IsNullOrWhiteSpace(value)
			? string.Empty
			: string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	private static IReadOnlyList<TitleRun> CreateRuns(string text)
	{
		var runs = new List<TitleRun>();
		var current = new StringBuilder();
		bool? currentChinese = null;
		var elements = StringInfo.GetTextElementEnumerator(text);
		while (elements.MoveNext())
		{
			var element = elements.GetTextElement();
			var chinese = UsesChineseFont(element);
			if (currentChinese is not null && currentChinese != chinese)
			{
				runs.Add(new TitleRun(current.ToString(), currentChinese.Value));
				current.Clear();
			}
			currentChinese = chinese;
			current.Append(element);
		}
		if (current.Length > 0)
			runs.Add(new TitleRun(current.ToString(), currentChinese == true));
		return runs;
	}

	private static bool UsesChineseFont(string textElement)
	{
		foreach (var rune in textElement.EnumerateRunes())
		{
			var value = rune.Value;
			if ((value >= 0x3400 && value <= 0x4DBF) ||
				(value >= 0x4E00 && value <= 0x9FFF) ||
				(value >= 0xF900 && value <= 0xFAFF) ||
				(value >= 0x20000 && value <= 0x2FA1F) ||
				(value >= 0x3000 && value <= 0x303F) ||
				(value >= 0xFF00 && value <= 0xFFEF))
				return true;
		}
		return false;
	}

	private static FontSet CreateFonts(float size) => new(
		CreateFont(ChineseFontFamily, size),
		CreateFont(LatinFontFamily, size));

	private static Font CreateFont(string familyName, float size)
	{
		try
		{
			return new Font(familyName, size, FontStyle.Regular, GraphicsUnit.Pixel);
		}
		catch (ArgumentException)
		{
			return new Font(FontFamily.GenericSerif, size, FontStyle.Regular, GraphicsUnit.Pixel);
		}
	}

	private static IReadOnlyList<MeasuredRun> MeasureRuns(
		Graphics graphics,
		IReadOnlyList<TitleRun> runs,
		FontSet fonts,
		StringFormat format) => runs
		.Select(run =>
		{
			var font = run.Chinese ? fonts.Chinese : fonts.Latin;
			return new MeasuredRun(run, font, graphics.MeasureString(run.Text, font, PointF.Empty, format));
		})
		.ToArray();

	private sealed record TitleRun(string Text, bool Chinese);
	private sealed record MeasuredRun(TitleRun Run, Font Font, SizeF Size);

	private sealed class FontSet(Font chinese, Font latin) : IDisposable
	{
		public Font Chinese { get; } = chinese;
		public Font Latin { get; } = latin;

		public void Dispose()
		{
			Chinese.Dispose();
			Latin.Dispose();
		}
	}
}
