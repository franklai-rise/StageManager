using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using StageManager.Settings;

namespace StageManager.Card3DPrototype.AiUsage;

// Shared by the composition texture and the visual regression host.
internal static class AiUsagePresentation
{
	public const float CardHeight = 120;
	internal static readonly string[] ProviderOrder = { "codex", "deepseek", "gemini", "kimi" };
	internal static RectangleF CellBounds(int index, Size size) =>
		new(index % 2 * size.Width / 2f, index / 2 * size.Height / 2f, size.Width / 2f, size.Height / 2f);
	public static Color Accent(string id) => id switch
	{
		"codex" => Color.FromArgb(127, 223, 189),
		"deepseek" => Color.FromArgb(143, 172, 255),
		"gemini" => Color.FromArgb(154, 186, 255),
		_ => Color.FromArgb(189, 173, 255)
	};

	public static string Duration(int? minutes) => minutes switch
	{
		null or <= 0 => "Primary",
		{ } n when n % 1440 == 0 => $"{n / 1440}d",
		{ } n when n % 60 == 0 => $"{n / 60}h",
		{ } n => $"{n}m"
	};

	public static (string Value, string Caption) Compact(AiUsageSnapshot item, UiLanguage language)
	{
		if (item.Status == "Disabled") return ("—", UiText.Get(language, "Disabled", "未启用"));
		if (item.Windows?.FirstOrDefault() is { } window)
			return ($"{window.RemainingPercent:0}%", Duration(window.DurationMinutes));
		if (item.TrendValue is { } value)
		{
			if (item.ProviderId == "codex") return ($"{value:0}%", "Codex");
			return (value.ToString("0.00", CultureInfo.CurrentCulture), item.TrendUnit.Length == 0 ? item.Name : item.TrendUnit);
		}
		return ("—", UiText.Get(language, item.NeedsConnection ? "Connect" : item.Status == "Loading" ? "Loading" : "Unavailable",
			item.NeedsConnection ? "待连接" : item.Status == "Loading" ? "读取中" : "暂无数据"));
	}

	public static string Status(AiUsageSnapshot item, UiLanguage language)
	{
		var text = item.Status switch
		{
			"Connected" => ("Up to date", "已更新"),
			"Gemini browser connection required" => ("Browser connection pending · Google AI Pro, not API credits", "待连接浏览器订阅额度 · 非 API 余额"),
			"Gemini browser unavailable" => ("Browser quota query failed; refresh in the extension", "浏览器额度查询失败，请在扩展中刷新"),
			"Gemini browser stale" => ("Old data · open your browser and refresh the extension", "数据已过期 · 请打开浏览器并刷新扩展"),
			"Loading" => ("Reading usage…", "正在读取额度…"),
			"Waiting for API key" => ("Add an API key in Settings", "请在设置中填写 API Key"),
			"Codex CLI not found" => ("Select a Codex CLI in Settings", "请在设置中选择 Codex CLI"),
			"Codex login required" => ("Sign in to Codex CLI first", "请先登录本机 Codex CLI"),
			"API key rejected" => ("API key was rejected", "API Key 无效或权限不足"),
			"Request timed out" or "Codex query timed out" => ("Update timed out", "更新超时"),
			"HttpRequestException" => ("Network unavailable", "网络暂时无法连接"),
			"Codex quota is unavailable" or "No Codex rate limits returned" => ("No quota windows returned", "接口未返回额度窗口"),
			_ => ("Could not update usage", "暂时无法更新额度")
		};
		var status = UiText.Get(language, text.Item1, text.Item2);
		return item.HasData && item.Status != "Connected"
			? UiText.Get(language, "Update failed · ", "更新失败 · ") + status : status;
	}

	public static void PaintCard(Graphics g, Size size, float dpi, IReadOnlyList<AiUsageSnapshot> items,
		UiLanguage language, AiProviderIcons icons)
	{
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		if (items.Count == 0)
		{
			DrawText(g, UiText.Get(language, "Select AI services", "选择 AI 平台"),
				new RectangleF(4 * dpi, 0, size.Width - 8 * dpi, size.Height), 10 * dpi, Color.Gainsboro);
			return;
		}
		var column = size.Width / 2f;
		for (var i = 0; i < ProviderOrder.Length; i++)
		{
			var item = items.FirstOrDefault(s => s.ProviderId == ProviderOrder[i]) ?? AiUsageService.Empty(ProviderOrder[i], "Disabled");
			var cell = CellBounds(i, size);
			var state = g.Save();
			g.TranslateTransform(0, cell.Top);
			var left = cell.Left;
			var center = left + column / 2;
			if (i % 2 != 0)
			{
				using var divider = new Pen(Color.FromArgb(32, 255, 255, 255), dpi);
				g.DrawLine(divider, left, 10 * dpi, left, cell.Height - 10 * dpi);
			}
			icons.Draw(g, item.ProviderId, new RectangleF(center - 9 * dpi, 5 * dpi, 18 * dpi, 18 * dpi));
			var (value, caption) = Compact(item, language);
			var available = Math.Max(12, column - 6 * dpi);
			DrawText(g, value, new RectangleF(left + 3 * dpi, 26 * dpi, available, 17 * dpi),
				13 * dpi, item.HasData ? Color.FromArgb(243, 248, 255) : Color.FromArgb(184, 198, 213), bold: true);
			DrawText(g, caption, new RectangleF(left + 3 * dpi, 44 * dpi, available, 12 * dpi),
				8.5f * dpi, item.HasData ? Accent(item.ProviderId) : Color.FromArgb(168, 185, 204));
			if (item.HasData && item.Status != "Connected")
				using (var warning = new SolidBrush(Color.FromArgb(244, 190, 91)))
					g.FillEllipse(warning, center + 10 * dpi, 6 * dpi, 3 * dpi, 3 * dpi);
			g.Restore(state);
		}
	}

	internal static void DrawText(Graphics g, string text, RectangleF bounds, float pixels, Color color, bool bold = false,
		StringAlignment alignment = StringAlignment.Center)
	{
		using var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center,
			Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
		using var brush = new SolidBrush(color);
		var fontSize = pixels;
		using (var initial = new Font("Segoe UI", pixels, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
		{
			var measured = g.MeasureString(text, initial).Width;
			if (measured > bounds.Width) fontSize = Math.Max(pixels * .78f, pixels * bounds.Width / measured);
		}
		using var font = new Font("Segoe UI", fontSize, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
		g.DrawString(text, font, brush, bounds, format);
	}

	internal static GraphicsPath Rounded(RectangleF rect, float radius)
	{
		var path = new GraphicsPath();
		var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
		path.AddArc(rect.X, rect.Y, d, d, 180, 90);
		path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
		path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
		path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
		path.CloseFigure();
		return path;
	}
}

internal sealed class AiProviderIcons : IDisposable
{
	private readonly Dictionary<string, Bitmap> _images = new();
	public AiProviderIcons()
	{
		foreach (var id in new[] { "codex", "deepseek", "kimi" })
		{
			using var stream = typeof(AiProviderIcons).Assembly.GetManifestResourceStream($"StageManager.Card3DPrototype.AiUsage.Assets.{id}.png");
			if (stream is null) continue;
			using var original = new Bitmap(stream);
			_images[id] = new Bitmap(original);
			if (id == "codex")
			{
				// The embedded mark is white on blue. Its red channel encodes the
				// antialiased white mark; use it as coverage on a black background.
				var monochrome = _images[id];
				for (var y = 0; y < monochrome.Height; y++)
					for (var x = 0; x < monochrome.Width; x++)
					{
						var pixel = monochrome.GetPixel(x, y);
						monochrome.SetPixel(x, y, Color.FromArgb(pixel.A, pixel.R, pixel.R, pixel.R));
					}
			}
		}
	}
	public void Draw(Graphics g, string id, RectangleF bounds)
	{
		if (id == "gemini")
		{
			// Vector sparkle avoids a raster fallback and remains crisp at mixed DPI.
			using var path = new GraphicsPath();
			var x = bounds.X; var y = bounds.Y; var w = bounds.Width; var h = bounds.Height;
			path.AddBezier(x + w / 2, y, x + w * .57f, y + h * .34f, x + w * .66f, y + h * .43f, x + w, y + h / 2);
			path.AddBezier(x + w, y + h / 2, x + w * .66f, y + h * .57f, x + w * .57f, y + h * .66f, x + w / 2, y + h);
			path.AddBezier(x + w / 2, y + h, x + w * .43f, y + h * .66f, x + w * .34f, y + h * .57f, x, y + h / 2);
			path.AddBezier(x, y + h / 2, x + w * .34f, y + h * .43f, x + w * .43f, y + h * .34f, x + w / 2, y);
			path.CloseFigure();
			using var brush = new LinearGradientBrush(bounds, Color.FromArgb(82, 158, 255), Color.FromArgb(208, 124, 222), 35f);
			g.FillPath(brush, path);
			return;
		}
		if (!_images.TryGetValue(id, out var icon)) return;
		var state = g.Save();
		using var clip = AiUsagePresentation.Rounded(bounds, bounds.Width * .24f);
		g.SetClip(clip, CombineMode.Intersect);
		g.InterpolationMode = InterpolationMode.HighQualityBicubic;
		g.DrawImage(icon, bounds);
		g.Restore(state);
	}
	public void Dispose() { foreach (var image in _images.Values) image.Dispose(); _images.Clear(); }
}
