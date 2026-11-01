using System.Globalization;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Settings;

namespace StageManager.Card3DPrototype.Calendar;

internal static class CalendarPresentation
{
    internal static readonly Color Paper = AiUsagePresentation.PanelBackground;
    internal static readonly Color CardPaper = Color.FromArgb(112, 22, 27, 36);
    internal static readonly Color Ink = AiUsagePresentation.PanelForeground;
    internal static readonly Color Accent = AiUsagePresentation.Accent("gemini");
    internal static readonly Color Muted = AiUsagePresentation.PanelMuted;
    internal static string T(UiLanguage language, string en, string zh) => UiText.Get(language, en, zh);
    internal static CultureInfo Culture(UiLanguage language) => CultureInfo.GetCultureInfo(T(language, "en-US", "zh-CN"));
    internal static bool HasData(CalendarSnapshot snapshot) => snapshot.Status is CalendarStatus.Connected or CalendarStatus.Stale;
    internal static string ErrorText(string code, UiLanguage language) => code.ToLowerInvariant() switch
    {
        "codexrefreshrequired" => T(language, "Click Refresh to sync this month from Codex", "点击刷新，从 Codex 同步本月日程"),
        "codexunavailable" or "codexprotocolunavailable" => T(language, "Codex CLI unavailable · check its path", "Codex CLI 不可用，请检查安装或路径"),
        "codexcalendarunavailable" => T(language, "Connect Outlook Calendar in Codex first", "请先在 Codex 中连接 Outlook 日历插件"),
        "codexapprovalrequired" => T(language, "Complete the required approval in Codex", "请先在 Codex 中完成所需授权"),
        "codexcalendarreadfailed" => T(language, "Codex calendar update failed · try again", "Codex 日历更新失败，请重试"),
        "authrequired" or "invalid_grant" or "unauthorized" => T(language, "Sign in again", "请重新登录"),
        "timeout" or "network" or "offline" => T(language, "Connection unavailable. Try refreshing.", "连接暂不可用，请刷新重试"),
        "throttled" or "ratelimited" => T(language, "Please try again later", "请稍后重试"),
        _ => T(language, "Unable to update calendar", "暂时无法更新日历")
    };
    internal static string StatusText(CalendarSnapshot snapshot, UiLanguage language) => snapshot.Status switch
    {
        CalendarStatus.Disabled => T(language, "Calendar disabled", "日历未启用"),
        CalendarStatus.NotConfigured => T(language, "Set up Calendar in Settings", "请在设置中配置日历"),
        CalendarStatus.NotConnected => T(language, "Connect your calendar", "请连接日历账户"),
        CalendarStatus.AuthRequired => T(language, "Sign in again to view calendar", "请重新登录以查看日历"),
        CalendarStatus.Loading => T(language, "Loading calendar…", "正在读取日历…"),
        CalendarStatus.Stale => T(language, "Saved data · update unavailable", "缓存数据 · 暂未更新"),
        CalendarStatus.Error => ErrorText(snapshot.ErrorCode, language),
        _ => T(language, "Up to date", "已更新")
    };
    internal static CalendarEvent[] TodayItems(CalendarSnapshot snapshot, DateTimeOffset now) =>
        HasData(snapshot) ? CalendarDates.ForDay(snapshot.Events, DateOnly.FromDateTime(now.LocalDateTime))
            .Where(e => e.IsAllDay || e.End > now).ToArray() : Array.Empty<CalendarEvent>();
    internal static string TimeText(CalendarEvent item, UiLanguage language) => item.IsAllDay
        ? T(language, "All day", "全天")
        : $"{item.Start.LocalDateTime:HH:mm}–{item.End.LocalDateTime:HH:mm}";
    internal static bool IsEnded(CalendarEvent item, DateTimeOffset now) => item.IsAllDay
        ? item.EndDate <= DateOnly.FromDateTime(now.LocalDateTime) : item.End <= now;
    internal static string EventName(CalendarEvent item, UiLanguage language) => string.IsNullOrWhiteSpace(item.Subject)
        ? T(language, "Untitled event", "无标题日程") : item.Subject;
    internal static bool TryOutlookLink(string link, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps ||
            !candidate.IsDefaultPort || candidate.UserInfo.Length != 0 ||
            !(candidate.IdnHost.Equals("outlook.live.com", StringComparison.OrdinalIgnoreCase) ||
              candidate.IdnHost.Equals("outlook.office.com", StringComparison.OrdinalIgnoreCase) ||
              candidate.IdnHost.Equals("outlook.office365.com", StringComparison.OrdinalIgnoreCase))) return false;
        uri = candidate;
        return true;
    }

    // The compact launcher shows only the local date. Event refreshes must not repaint it.
    public static string RenderKey(Size size, UiLanguage language, DateTimeOffset now) =>
        FormattableString.Invariant($"{size.Width}x{size.Height}:{language}:{now.LocalDateTime:yyyyMMdd}");
    public static string CompactDate(DateTimeOffset now, UiLanguage language) =>
        now.LocalDateTime.ToString(T(language, "MMM d", "M月d日"), Culture(language));

    // Supersample this single small texture, not the entire sidebar. At already
    // large device sizes cap the extra pixels; never undersample the display.
    internal static Size DateTextureSize(Size displaySize)
    {
        if (displaySize.Width <= 0 || displaySize.Height <= 0) return Size.Empty;
        var scale = Math.Max(1, Math.Min(2, Math.Min(2048d / Math.Max(displaySize.Width, displaySize.Height),
            Math.Sqrt(1048576d / ((long)displaySize.Width * displaySize.Height)))));
        return new Size(Math.Max(displaySize.Width, (int)Math.Floor(displaySize.Width * scale)),
            Math.Max(displaySize.Height, (int)Math.Floor(displaySize.Height * scale)));
    }
    internal static string DateFontFamily(UiLanguage language) =>
        Culture(language).TwoLetterISOLanguageName == "zh" ? "Microsoft YaHei UI" : "Segoe UI";

    internal static void Text(Graphics g, string text, Rectangle bounds, float pixels, Color color, bool bold = false, bool strike = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var font = new Font("Segoe UI", Math.Max(1, pixels), (bold ? FontStyle.Bold : FontStyle.Regular) | (strike ? FontStyle.Strikeout : FontStyle.Regular), GraphicsUnit.Pixel);
        // GDI+ retains alpha on the host's composition bitmap; GDI TextRenderer is used on native controls.
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        var hint = g.TextRenderingHint;
        try { g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; g.DrawString(text, font, brush, bounds, format); }
        finally { g.TextRenderingHint = hint; }
    }
    internal static string EmptyDayText(CalendarSnapshot snapshot, UiLanguage language, bool ended = false) =>
        snapshot.Status == CalendarStatus.Stale ? T(language, "Day unavailable · saved data incomplete", "当天信息不可用 · 缓存可能不完整") :
        T(language, ended ? "All done for today" : "No events on this day", ended ? "今日日程已结束" : "当天无日程");

    public static void PaintCard(Graphics g, Size size, UiLanguage language, DateTimeOffset now)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        var state = g.Save();
        try
        {
            g.SetClip(new Rectangle(Point.Empty, size), System.Drawing.Drawing2D.CombineMode.Intersect);
            using var paper = new SolidBrush(CardPaper);
            g.FillRectangle(paper, new Rectangle(Point.Empty, size));
            // Explicit grayscale coverage works on transparent, transformed cards.
            // ClearType is deliberately not baked into an alpha texture: its RGB
            // subpixels would move with the perspective transform and produce fringes.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
            float padding = size.Width * .07f;
            float pixels = Math.Max(1, Math.Min(size.Height * .66f, size.Width * .16f));
            var bounds = new RectangleF(padding, 0, Math.Max(1, size.Width - padding * 2), size.Height);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.Alignment = format.LineAlignment = StringAlignment.Center;
            format.FormatFlags |= StringFormatFlags.NoWrap;
            var text = CompactDate(now, language);
            using var measureFont = new Font(DateFontFamily(language), pixels, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, measureFont, int.MaxValue, format);
            var fitted = Math.Max(1, pixels * Math.Min(1, bounds.Width / Math.Max(1, measured.Width)));
            using var font = new Font(DateFontFamily(language), fitted, FontStyle.Regular, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Ink);
            g.DrawString(text, font, ink, bounds, format);
        }
        finally { g.Restore(state); }
    }
}
