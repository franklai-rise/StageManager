using System.Diagnostics;
using System.Drawing.Drawing2D;
using StageManager.Card3DPrototype.AiUsage;

namespace StageManager.Card3DPrototype.Calendar;

// The normal actions use the actual AI flyout button, not a visually similar copy.
internal sealed class CalendarButton : AiUsageButton
{
    public bool Filled { get; set; } = true;
    public bool Marked { get; set; }
    public bool Completed { get; set; }
    public bool Expanded { get; set; }
    public string? TimeCaption { get; set; }
    public string? Subject { get; set; }

    public CalendarButton()
    {
        SetStyle(ControlStyles.Selectable, false);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        UseMnemonic = false; Cursor = Cursors.Hand; TabStop = false;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }
        base.WndProc(ref m);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        bool navigationArrow = Text is "‹" or "›";
        if (Subject is null && Filled && !navigationArrow) { base.OnPaint(e); return; }
        var g = e.Graphics; var s = RenderScale;
        PaintButtonSurface(g, Filled || Selected || Enabled && (IsHovered || IsPressed));
        if (Subject is not null)
        {
            AiUsagePresentation.DrawText(g, TimeCaption ?? "", new RectangleF(12 * s, 5 * s, Width - 36 * s, 21 * s),
                11 * s, Completed ? CalendarPresentation.Muted : CalendarPresentation.Accent, alignment: StringAlignment.Near);
            using var subjectFont = new Font("Segoe UI", 13 * s, Completed ? FontStyle.Strikeout : FontStyle.Regular, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, Subject, subjectFont,
                Rectangle.Round(new RectangleF(10 * s, 27 * s, Math.Max(1, Width - 36 * s), 26 * s)), ForeColor,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            using var arrow = new Pen(CalendarPresentation.Muted, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x = Width - 15 * s, y = Height / 2f;
            g.DrawLines(arrow, Expanded
                ? new[] { new PointF(x - 4 * s, y - 2 * s), new PointF(x, y + 2 * s), new PointF(x + 4 * s, y - 2 * s) }
                : new[] { new PointF(x - 2 * s, y - 4 * s), new PointF(x + 2 * s, y), new PointF(x - 2 * s, y + 4 * s) });
            return;
        }
        AiUsagePresentation.DrawText(g, Text, new RectangleF(3 * s, 0, Math.Max(1, Width - 6 * s), Math.Max(1, Height - (Marked ? 4 * s : 0))),
            (navigationArrow ? 20 : 13) * s, Enabled ? ForeColor : CalendarPresentation.Muted, Selected);
        if (Marked)
        {
            using var brush = new SolidBrush(CalendarPresentation.Accent);
            float d = 3 * s;
            g.FillEllipse(brush, (Width - d) / 2, Height - d - 3 * s, d, d);
        }
    }
}

// Retain native CheckBox/UIA semantics, but paint a device-resolution rounded tick.
// The timer exists only during a visible transition and is stopped on hide/disposal.
internal sealed class CompletionCheckBox : CheckBox
{
    private System.Windows.Forms.Timer? _animation;
    private bool _hovered, _pressed;
    private float _progress, _from, _to;
    private long _started;
    public float RenderScale { get; set; } = 1;
    public bool AnimationsEnabled { get; set; } = true;

    public CompletionCheckBox()
    {
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        CheckAlign = ContentAlignment.MiddleCenter; Cursor = Cursors.Hand; TabStop = false;
        AccessibleRole = AccessibleRole.CheckButton;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }
        base.WndProc(ref m);
    }
    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { _pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
    protected override void OnCheckedChanged(EventArgs e)
    {
        _to = Checked ? 1 : 0;
        if (AnimationsEnabled && NativeMethods.ClientAreaAnimationsEnabled && Visible && IsHandleCreated)
        {
            _from = _progress; _started = Stopwatch.GetTimestamp();
            if (_animation is null)
            {
                _animation = new System.Windows.Forms.Timer { Interval = 16 };
                _animation.Tick += (_, _) => AdvanceAnimation();
            }
            _animation.Start();
        }
        else { _animation?.Stop(); _progress = _to; }
        Invalidate(); base.OnCheckedChanged(e);
    }
    private void AdvanceAnimation()
    {
        var time = Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / 140, 0, 1);
        _progress = _from + (_to - _from) * (float)(1 - Math.Pow(1 - time, 3));
        if (time >= 1) _animation?.Stop();
        Invalidate();
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible) { _animation?.Stop(); _progress = Checked ? 1 : 0; }
        base.OnVisibleChanged(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = Math.Max(.5f, RenderScale);
        g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        float side = (_pressed ? 20 : 22) * s;
        var box = new RectangleF((Width - side) / 2, (Height - side) / 2, side, side);
        using var shape = AiUsagePresentation.Rounded(box, 6 * s);
        var start = AiUsagePresentation.ButtonFill(Enabled && _pressed, Enabled && _hovered, false);
        Color Blend(Color a, Color b) => Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * _progress),
            (int)Math.Round(a.G + (b.G - a.G) * _progress),
            (int)Math.Round(a.B + (b.B - a.B) * _progress));
        using var fill = new SolidBrush(Blend(start, CalendarPresentation.Accent));
        using var border = new Pen(Blend(_hovered ? Color.FromArgb(180, 199, 222) : Color.FromArgb(98, 115, 136), CalendarPresentation.Accent), 1.3f * s);
        g.FillPath(fill, shape); g.DrawPath(border, shape);
        if (_progress > 0)
        {
            using var tick = new Pen(Color.FromArgb((int)(255 * _progress), AiUsagePresentation.PanelBackground), 2.2f * s)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(tick, new[] { new PointF(box.Left + side * .23f, box.Top + side * .51f),
                new PointF(box.Left + side * .44f, box.Top + side * .72f), new PointF(box.Left + side * .79f, box.Top + side * .29f) });
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _animation?.Dispose(); _animation = null; }
        base.Dispose(disposing);
    }
}
