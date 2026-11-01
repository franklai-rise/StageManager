using System.Diagnostics;
using System.Drawing.Drawing2D;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Settings;

namespace StageManager.Card3DPrototype.Calendar;

internal sealed class CalendarDetailsForm : Form
{
    internal const int PreferredWidth = 420, PreferredHeight = 740;
    private readonly UiLanguage _language;
    private readonly bool _animations;
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private readonly CalendarButton _previous, _next, _today, _refresh, _settings, _close, _openOutlook;
    private readonly CalendarButton[] _days = new CalendarButton[42];
    private readonly Label _month = new(), _status = new(), _selected = new(), _progress = new();
    private readonly Label[] _weekdays = new Label[7];
    private readonly Panel _agenda = new() { AutoScroll = true, TabStop = false };
    private readonly Panel _viewport = new() { AutoScroll = true, Dock = DockStyle.Fill, TabStop = false };
    private readonly Panel _surface = new() { TabStop = false };
    private HashSet<string> _completed = new(StringComparer.Ordinal);
    private CalendarSnapshot _snapshot = CalendarSnapshot.Empty(CalendarStatus.Loading);
    private readonly List<AgendaEntry> _entries = new();
    private sealed record AgendaEntry(CalendarEvent Event, CompletionCheckBox Tick, CalendarButton Row);
    private Font? _ownedFont, _monthFont, _selectedFont;
    private float _scale = 1;
    private bool _refreshing;
    private bool _synchronizingCompletion;
    private string? _expanded;
    private Point _agendaScroll;
    private long _fadeStarted;
    private double _fadeFrom, _fadeTo;
    public DateOnly DisplayMonth { get; private set; } = CalendarDates.MonthStart(CalendarDates.Today);
    public DateOnly SelectedDate { get; private set; } = CalendarDates.Today;
    public bool ManualRefresh { get; set; }
    public event Action<DateOnly>? MonthRequested;
    public event Action<CalendarEvent, bool>? CompletionChanged;
    public event EventHandler? RefreshRequested, SettingsRequested, CloseRequested;

    public CalendarDetailsForm(UiLanguage language, bool animationsEnabled = true)
    {
        _language = language;
        _animations = animationsEnabled && SystemInformation.IsMenuAnimationEnabled;
        Text = T("Calendar", "日历"); AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = CalendarPresentation.Paper; ForeColor = CalendarPresentation.Ink; DoubleBuffered = true;
        TopMost = true;
        _viewport.BackColor = _surface.BackColor = _agenda.BackColor = BackColor;
        _status.ForeColor = _progress.ForeColor = CalendarPresentation.Muted;
        _status.UseMnemonic = _month.UseMnemonic = _selected.UseMnemonic = false;
        _previous = Button("‹", T("Previous month", "上个月"), () => ChangeMonth(-1));
        _next = Button("›", T("Next month", "下个月"), () => ChangeMonth(1));
        _today = Button(T("Today", "今天"), T("Select today", "选择今天"), () => SelectDate(CalendarDates.Today));
        _refresh = Button(T("Refresh", "刷新"), T("Refresh calendar", "刷新日历"), () => { if (!_refreshing) RefreshRequested?.Invoke(this, EventArgs.Empty); });
        _settings = Button(T("Settings", "设置"), T("Calendar settings", "日历设置"), () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        _openOutlook = Button(T("Open Outlook", "打开 Outlook"), T("Open Outlook calendar", "打开 Outlook 日历"),
            () => OpenOutlook("https://outlook.office.com/calendar/"));
        _agenda.AccessibleName = T("Calendar agenda", "日历日程列表");
        _close = Button("×", T("Close calendar", "关闭日历"), () => CloseRequested?.Invoke(this, EventArgs.Empty));
        Controls.AddRange(new Control[] { _month, _status, _selected, _progress, _agenda });
        var monday = new DateOnly(2024, 1, 1);
        for (int i = 0; i < 7; i++)
        {
            _weekdays[i] = new Label { Text = monday.AddDays(i).ToString("ddd", CalendarPresentation.Culture(language)),
                ForeColor = CalendarPresentation.Muted, TextAlign = ContentAlignment.MiddleCenter };
            Controls.Add(_weekdays[i]);
        }
        for (int i = 0; i < 42; i++)
        {
            var day = new CalendarButton { TabStop = false, BackColor = BackColor, Filled = false };
            day.Click += (_, _) => { if (day.Tag is DateOnly date) SelectDate(date); };
            _days[i] = day; Controls.Add(day);
        }
        _fade.Tick += (_, _) => AdvanceFade();
        _surface.Paint += PaintSurface;
        foreach (Control child in Controls.Cast<Control>().ToArray()) _surface.Controls.Add(child);
        _viewport.Controls.Add(_surface); Controls.Add(_viewport);
        ClientSize = new Size(PreferredWidth, PreferredHeight);
        SetScale(1); UpdateContent();
    }
    private string T(string en, string zh) => CalendarPresentation.T(_language, en, zh);
    private CalendarButton Button(string text, string name, Action action)
    {
        var button = new CalendarButton { Text = text, AccessibleName = name, BackColor = BackColor,
            ForeColor = AiUsagePresentation.ButtonForeground, TabStop = false };
        button.Click += (_, _) => action(); Controls.Add(button); return button;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }
        base.WndProc(ref m);
    }
    public bool IsPointerInside(Point screen) => Visible && Bounds.Contains(screen) &&
        (Region is null || Region.IsVisible(PointToClient(screen)));
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { Opacity = _animations ? 0 : 1; FadeTo(1); }
        else _fade.Stop();
    }
    public void FadeOut() => FadeTo(0);
    public void KeepOpen() { if (_fadeTo != 1 || Opacity < 1) FadeTo(1); }
    private void FadeTo(double target)
    {
        if (_fade.Enabled && _fadeTo == target) return;
        _fadeTo = target;
        if (!_animations) { Opacity = target; if (target == 0) Hide(); return; }
        _fadeFrom = Opacity; _fadeStarted = Stopwatch.GetTimestamp(); _fade.Start();
    }
    private void AdvanceFade()
    {
        var progress = Math.Clamp(Stopwatch.GetElapsedTime(_fadeStarted).TotalMilliseconds / 150, 0, 1);
        Opacity = _fadeFrom + (_fadeTo - _fadeFrom) * (1 - Math.Pow(1 - progress, 3));
        if (progress < 1) return;
        _fade.Stop(); if (_fadeTo == 0) Hide();
    }
    public void SetSnapshot(CalendarSnapshot snapshot)
    {
        // The parent must discard responses belonging to an earlier requested month.
        _snapshot = snapshot with { Events = snapshot.Events.ToArray() }; UpdateContent();
    }
    public void SetCompletedEvents(IReadOnlySet<string> keys)
    {
        _completed = new HashSet<string>(keys, StringComparer.Ordinal);
        // Keep the live checkbox and scroll position during the host's persistence echo.
        // Recreating rows here used to interrupt both the pressed state and the tick animation.
        _synchronizingCompletion = true;
        try
        {
            foreach (var entry in _entries)
            {
                entry.Tick.Checked = _completed.Contains(CalendarCompletionStore.Key(entry.Event));
                UpdateCompletionRow(entry.Event, entry.Row, entry.Tick.Checked);
            }
        }
        finally { _synchronizingCompletion = false; }
        UpdateProgress();
    }
    public void SetRefreshing(bool value)
    {
        _refreshing = value; _refresh.Enabled = !value;
        _refresh.Text = value ? T("Refreshing…", "刷新中…") : T("Refresh", "刷新");
        UpdateStatus();
    }
    private void ChangeMonth(int delta)
    {
        if ((delta < 0 && DisplayMonth.Year == 1 && DisplayMonth.Month == 1) ||
            (delta > 0 && DisplayMonth.Year == 9999 && DisplayMonth.Month == 12)) return;
        var month = DisplayMonth.AddMonths(delta);
        SelectDate(new DateOnly(month.Year, month.Month, Math.Min(SelectedDate.Day, DateTime.DaysInMonth(month.Year, month.Month))));
    }
    internal void SelectDate(DateOnly date)
    {
        bool changedMonth = CalendarDates.MonthStart(date) != DisplayMonth;
        if (SelectedDate != date)
        {
            _expanded = null; _agendaScroll = Point.Empty;
            _agenda.AutoScrollPosition = Point.Empty;
        }
        SelectedDate = date;
        if (changedMonth)
        {
            DisplayMonth = CalendarDates.MonthStart(date);
            // Navigation must not turn a disabled/disconnected account into a fictitious load.
            var status = _snapshot.Status is CalendarStatus.Disabled or CalendarStatus.NotConfigured or
                CalendarStatus.NotConnected or CalendarStatus.AuthRequired ? _snapshot.Status : CalendarStatus.Loading;
            _snapshot = CalendarSnapshot.Empty(status);
        }
        UpdateContent();
        if (changedMonth) MonthRequested?.Invoke(DisplayMonth);
    }
    private void UpdateStatus()
    {
        _status.Text = (_refreshing ? T("Refreshing · ", "刷新中 · ") : "") +
            (ManualRefresh && _snapshot.Status == CalendarStatus.Connected ? T("Cache · manual sync", "缓存 · 手动同步") : CalendarPresentation.StatusText(_snapshot, _language));
        if (_snapshot.LastSuccess is { } success)
            _status.Text += " · " + success.LocalDateTime.ToString("MM-dd HH:mm", CalendarPresentation.Culture(_language));
        _status.AccessibleName = _status.Text;
    }
    private void UpdateContent()
    {
        _month.Text = DisplayMonth.ToString("Y", CalendarPresentation.Culture(_language));
        _month.AccessibleName = _month.Text;
        _selected.Text = SelectedDate.ToString("D", CalendarPresentation.Culture(_language));
        _selected.AccessibleName = _selected.Text;
        UpdateStatus();
        var start = CalendarDates.GridStart(DisplayMonth);
        for (int i = 0; i < 42; i++)
        {
            var button = _days[i];
            if (start.DayNumber > DateOnly.MaxValue.DayNumber - i) { button.Visible = false; continue; }
            var date = start.AddDays(i); button.Visible = true; button.Tag = date;
            button.Text = date.Day.ToString();
            button.Marked = CalendarPresentation.HasData(_snapshot) && CalendarDates.ForDay(_snapshot.Events, date).Any();
            button.Selected = date == SelectedDate;
            button.BackColor = BackColor;
            button.ForeColor = date == CalendarDates.Today ? CalendarPresentation.Accent : date.Month == DisplayMonth.Month ? ForeColor : CalendarPresentation.Muted;
            button.AccessibleName = date.ToString("D", CalendarPresentation.Culture(_language)) +
                (date == SelectedDate ? T(", selected", "，已选择") : "") + (button.Marked ? T(", has events", "，有日程") : "");
            button.Invalidate();
        }
        RebuildAgenda();
    }
    private void RebuildAgenda()
    {
        var scroll = _agenda.AutoScrollPosition;
        // Remember the last meaningful viewport across temporary loading/error rows too.
        if (scroll != Point.Empty || _agenda.VerticalScroll.Visible)
            _agendaScroll = new Point(-scroll.X, -scroll.Y);
        _agenda.SuspendLayout();
        try
        {
            _entries.Clear();
            foreach (Control child in _agenda.Controls.Cast<Control>().ToArray()) child.Dispose();
            _agenda.AutoScrollPosition = Point.Empty;
            int y = 0, width = Math.Max(40, _agenda.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            void LabelRow(string text, bool detail = false)
            {
                var indent = detail ? S(42) : S(6);
                var height = TextRenderer.MeasureText(text, Font, new Size(width - indent, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(16);
                var label = new Label { Text = text, AccessibleName = text, UseMnemonic = false,
                    ForeColor = CalendarPresentation.Muted, Bounds = new Rectangle(indent, y, width - indent, height) };
                _agenda.Controls.Add(label); y += height;
            }
            if (!CalendarPresentation.HasData(_snapshot)) LabelRow(CalendarPresentation.StatusText(_snapshot, _language));
            else
            {
                var events = CalendarDates.ForDay(_snapshot.Events, SelectedDate).ToArray();
                if (events.Length == 0) LabelRow(CalendarPresentation.EmptyDayText(_snapshot, _language));
                foreach (var item in events)
                {
                    var key = CalendarCompletionStore.Key(item);
                    bool done = _completed.Contains(key);
                    bool ended = CalendarPresentation.IsEnded(item, DateTimeOffset.Now);
                    var text = CalendarPresentation.TimeText(item, _language) + "  " + CalendarPresentation.EventName(item, _language);
                    var row = new CalendarButton { Text = text, AccessibleName = text + T(", expand details", "，展开详情"),
                        BackColor = BackColor, ForeColor = done || ended ? CalendarPresentation.Muted : ForeColor,
                        TimeCaption = CalendarPresentation.TimeText(item, _language), Subject = CalendarPresentation.EventName(item, _language), Completed = done,
                        Expanded = _expanded == key, RenderScale = _scale,
                        Bounds = new Rectangle(S(36), y, Math.Max(1, width - S(36)), S(60)), TabStop = false };
                    row.Click += (_, _) => { _expanded = _expanded == key ? null : key; RebuildAgenda(); };
                    var tick = new CompletionCheckBox { Checked = done, BackColor = BackColor,
                        RenderScale = _scale, AnimationsEnabled = _animations,
                        AccessibleName = T("Completed locally: ", "本地完成标记：") + CalendarPresentation.EventName(item, _language),
                        Bounds = new Rectangle(0, y, S(34), S(60)), TabStop = false };
                    tick.CheckedChanged += (_, _) =>
                    {
                        if (_synchronizingCompletion) return;
                        bool value = tick.Checked;
                        if (value) _completed.Add(key); else _completed.Remove(key);
                        UpdateCompletionRow(item, row, value); UpdateProgress();
                        CompletionChanged?.Invoke(item, value);
                    };
                    _entries.Add(new(item, tick, row));
                    _agenda.Controls.Add(tick); _agenda.Controls.Add(row); y += S(68);
                    if (_expanded != key) continue;
                    LabelRow(T("Location: ", "地点：") + item.Location + "\n" + T("Organizer: ", "组织者：") + item.Organizer +
                        "\n" + T("Preview: ", "摘要：") + item.Summary, detail: true);
                    var link = new CalendarButton { Text = T("Open in Outlook", "在 Outlook 中打开"), AccessibleName = T("Open event in Outlook", "在 Outlook 中打开日程"),
                        Enabled = CalendarPresentation.TryOutlookLink(item.WebLink, out _), BackColor = BackColor, RenderScale = _scale,
                        ForeColor = AiUsagePresentation.ButtonForeground, Bounds = new Rectangle(S(36), y, width - S(36), S(34)), TabStop = false };
                    link.Click += (_, _) =>
                    {
                        OpenOutlook(item.WebLink);
                    };
                    _agenda.Controls.Add(link); y += S(44);
                }
            }
            _agenda.AutoScrollMinSize = new Size(0, y);
            UpdateProgress();
        }
        finally
        {
            _agenda.ResumeLayout();
            _agenda.AutoScrollPosition = _agendaScroll;
        }
    }
    private void UpdateCompletionRow(CalendarEvent item, CalendarButton row, bool done)
    {
        row.Completed = done;
        row.ForeColor = done || CalendarPresentation.IsEnded(item, DateTimeOffset.Now) ? CalendarPresentation.Muted : ForeColor;
        row.Invalidate();
    }
    private void UpdateProgress()
    {
        var events = CalendarPresentation.HasData(_snapshot) ? CalendarDates.ForDay(_snapshot.Events, SelectedDate).ToArray() : Array.Empty<CalendarEvent>();
        var done = events.Count(e => _completed.Contains(CalendarCompletionStore.Key(e)));
        _progress.Text = events.Length == 0 ? "" : T($"{done}/{events.Length} done", $"完成 {done}/{events.Length}");
        _progress.AccessibleName = _progress.Text;
    }
    private void OpenOutlook(string link)
    {
        if (!CalendarPresentation.TryOutlookLink(link, out var uri)) return;
        try { Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { _status.Text = T("Unable to open Outlook", "暂时无法打开 Outlook"); _status.AccessibleName = _status.Text; }
    }
    private int S(float value) => (int)Math.Round(value * _scale);
    private void SetScale(float scale)
    {
        _scale = scale;
        var old = _ownedFont; _ownedFont = new Font("Segoe UI", 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = _ownedFont; old?.Dispose();
        var oldMonth = _monthFont; _monthFont = new Font("Segoe UI", 16 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _month.Font = _monthFont; oldMonth?.Dispose();
        var oldSelected = _selectedFont; _selectedFont = new Font("Segoe UI", 14 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _selected.Font = _selectedFont; oldSelected?.Dispose();
        Padding = new Padding(Math.Max(1, S(1)));
        LayoutContent();
    }
    public void FitToWorkingArea(Rectangle area, float dpiScale)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        float scale = float.IsFinite(dpiScale) ? Math.Clamp(dpiScale, .5f, 4) : 1;
        // Preserve physical DPI/text size. Small work areas scroll the full native surface instead of shrinking it.
        SetScale(scale);
        ClientSize = new Size(Math.Min(area.Width, Math.Max(1, S(PreferredWidth))), Math.Min(area.Height, Math.Max(1, S(PreferredHeight))));
        Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
        LayoutContent(); RebuildAgenda();
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e); if (_days[41] is null) return; LayoutContent(); RebuildAgenda();
    }
    private void LayoutContent()
    {
        if (_days[41] is null) return;
        _surface.Size = new Size(S(PreferredWidth) - Padding.Horizontal, S(PreferredHeight) - Padding.Vertical);
        Rectangle R(float x, float y, float w, float h) => new(S(x), S(y), S(w), S(h));
        _close.Bounds = R(366, 14, 32, 32);
        _status.Bounds = R(20, 50, 378, 26);
        _month.Bounds = R(20, 80, 202, 32); _month.TextAlign = ContentAlignment.MiddleLeft;
        _previous.Bounds = R(230, 80, 36, 32); _next.Bounds = R(272, 80, 36, 32); _today.Bounds = R(320, 80, 78, 32);
        for (int i = 0; i < 7; i++) _weekdays[i].Bounds = R(20 + i * 54, 120, 54, 22);
        for (int i = 0; i < 42; i++) _days[i].Bounds = R(20 + i % 7 * 54, 146 + i / 7 * 32, 52, 31);
        _selected.Bounds = R(20, 358, 266, 28);
        _selected.TextAlign = ContentAlignment.MiddleLeft;
        _progress.Bounds = R(290, 358, 108, 28); _progress.TextAlign = ContentAlignment.MiddleRight;
        _agenda.Bounds = R(20, 398, 378, PreferredHeight - 398 - 64);
        _refresh.Bounds = R(20, PreferredHeight - 52, 108, 34);
        _openOutlook.Bounds = R(136, PreferredHeight - 52, 152, 34);
        _settings.Bounds = R(296, PreferredHeight - 52, 102, 34);
        foreach (var button in _days.Concat(new[] { _previous, _next, _today, _refresh, _settings, _close, _openOutlook }))
            button.RenderScale = _scale;
        using var shape = AiUsagePresentation.Rounded(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), AiUsagePresentation.PanelRadius * _scale);
        var oldRegion = Region; Region = new Region(shape); oldRegion?.Dispose();
        _surface.Invalidate(); Invalidate();
    }
    private void PaintSurface(object? sender, PaintEventArgs e)
    {
        AiUsagePresentation.DrawText(e.Graphics, T("Calendar", "日历"), new RectangleF(S(20), S(16), S(270), S(28)),
            18 * _scale, ForeColor, true, StringAlignment.Near);
        using var divider = new Pen(AiUsagePresentation.PanelBorder, Math.Max(1, _scale));
        e.Graphics.DrawLine(divider, S(20), S(348), S(398), S(348));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(AiUsagePresentation.PanelBorder, Math.Max(1, _scale));
        using var path = AiUsagePresentation.Rounded(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1), AiUsagePresentation.PanelRadius * _scale);
        e.Graphics.DrawPath(border, path);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _fade.Dispose();
        base.Dispose(disposing);
        if (disposing) { _ownedFont?.Dispose(); _ownedFont = null; _monthFont?.Dispose(); _selectedFont?.Dispose(); }
    }
}
