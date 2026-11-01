using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Card3DPrototype.Calendar;
using StageManager.Settings;

internal static class CalendarTypographyTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Check(CalendarPresentation.DateFontFamily(UiLanguage.SimplifiedChinese) == "Microsoft YaHei UI", "Chinese date needs an explicit UI font");
        Check(CalendarPresentation.DateFontFamily(UiLanguage.English) == "Segoe UI", "English date uses the existing interface font");
        foreach (var size in new[] { new Size(64, 36), new Size(112, 36), new Size(140, 45), new Size(224, 72), new Size(980, 700), new Size(1600, 900) })
        {
            var texture = CalendarPresentation.DateTextureSize(size);
            Check(texture.Width >= size.Width && texture.Height >= size.Height, "Date texture must not undersample the device");
            Check(texture.Width <= size.Width * 2 && texture.Height <= size.Height * 2, "Date supersampling is bounded");
            if ((long)size.Width * size.Height <= 1048576)
                Check((long)texture.Width * texture.Height <= 1048576, "Date texture exceeded its extra-pixel budget");
            else Check(texture == size, "Already large cards must not acquire more texture pixels");
        }
        Check(CalendarPresentation.DateTextureSize(Size.Empty) == Size.Empty, "Empty texture");
        Check(CalendarPresentation.DateTextureSize(new Size(112, 36)) == new Size(224, 72), "Normal compact date gets two-times coverage");

        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var date in new[] { new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8)), new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(8)), new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.FromHours(8)) })
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var display = new Size((int)Math.Ceiling(196 * .57f * scale), (int)Math.Ceiling(36 * scale));
            var size = CalendarPresentation.DateTextureSize(display);
            using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bitmap);
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
            CalendarPresentation.PaintCard(g, size, language, date);
            Check(g.TextRenderingHint == TextRenderingHint.SingleBitPerPixel, "Date drawing changed the caller's graphics state");
            int edges = 0, ink = 0, left = size.Width, right = 0;
            for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > CalendarPresentation.CardPaper.A + 5)
                {
                    ink++; left = Math.Min(left, x); right = Math.Max(right, x);
                    if (pixel.A < 245) edges++;
                }
            }
            Check(ink > 100 && edges > 40, "Date requires real grayscale antialias coverage, independent of system text smoothing");
            Check(left > 1 && right < size.Width - 2, "Localized date lost its side padding or was clipped");
            Check(bitmap.GetPixel(0, 0).A == CalendarPresentation.CardPaper.A, "Font fix removed glass transparency");
        }
    }

    // Explicit opt-in, synthetic dates only. Native physical pixels at the user's card scale.
    public static void Preview(string path)
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8));
        using var canvas = new Bitmap(820, 465, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(86, 107, 131));
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var labelFont = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelInk = new SolidBrush(Color.White);
        g.DrawString("Previous", labelFont, labelInk, 195, 18);
        g.DrawString("Refined", labelFont, labelInk, 505, 18);
        int y = 62;
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var display = new Size((int)Math.Ceiling(196 * .57f * scale), (int)Math.Ceiling(36 * scale));
            using var before = new Bitmap(display.Width, display.Height, PixelFormat.Format32bppPArgb);
            using (var old = Graphics.FromImage(before))
            {
                using var fill = new SolidBrush(CalendarPresentation.CardPaper); old.FillRectangle(fill, new Rectangle(Point.Empty, display));
                float padding = display.Width * .08f;
                AiUsagePresentation.DrawText(old, CalendarPresentation.CompactDate(now, UiLanguage.SimplifiedChinese),
                    new RectangleF(padding, 0, display.Width - padding * 2, display.Height), Math.Min(display.Height * .68f, display.Width * .16f), CalendarPresentation.Ink, true);
            }
            var texture = CalendarPresentation.DateTextureSize(display);
            using var after = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppPArgb);
            using (var improved = Graphics.FromImage(after)) CalendarPresentation.PaintCard(improved, texture, UiLanguage.SimplifiedChinese, now);
            g.DrawString($"{scale:P0}", labelFont, labelInk, 26, y + 9);
            void Card(Bitmap image, int x)
            {
                var saved = g.Save();
                var target = new Rectangle(x, y, display.Width, display.Height);
                using var clip = AiUsagePresentation.Rounded(target, 8 * scale);
                g.SetClip(clip); g.InterpolationMode = InterpolationMode.Bilinear; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(image, target, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel); g.Restore(saved);
            }
            Card(before, 195); Card(after, 505); y += display.Height + 30;
        }
        g.DrawString("57% card size · native device pixels · synthetic date", labelFont, labelInk, 26, 425);
        canvas.Save(path, ImageFormat.Png);
    }
}
