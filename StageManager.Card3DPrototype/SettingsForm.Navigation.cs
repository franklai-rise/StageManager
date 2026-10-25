using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace StageManager.Card3DPrototype;

internal sealed partial class SettingsForm
{
    private readonly Dictionary<string, ModuleNavigationButton> _navigationButtons = new();
    private readonly System.Windows.Forms.Timer _navigationTimer = new() { Interval = 16 };
    private long _navigationStarted;
    private int _navigationFrom, _navigationTo;
    private bool _navigationTick;
    private string? _navigationDestination;
    private static readonly Color[] ModuleColors = {
        Color.FromArgb(55, 113, 211), Color.FromArgb(132, 87, 192), Color.FromArgb(31, 139, 131),
        Color.FromArgb(67, 123, 158), Color.FromArgb(168, 110, 37), Color.FromArgb(53, 137, 88), Color.FromArgb(173, 84, 128)
    };

    private Control BuildNavigation()
    {
        // Separate viewport: this rail stays still while the complete settings document scrolls.
        var rail = new Panel { Dock = DockStyle.Left, Width = 108, Padding = new Padding(0, 3, 16, 0), Name = "module-navigation" };
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Margin = Padding.Empty };
        rail.Controls.Add(list);
        var names = new[] { ("General", "工作区"), ("Prefs", "偏好"), ("Cards", "卡片"), ("Windows", "窗口"), ("Keys", "快捷键"), ("Updates", "更新"), ("AI", "AI 额度") };
        for (var i = 0; i < _moduleOrder.Length; i++)
        {
            var id = _moduleOrder[i];
            var button = Localize(new ModuleNavigationButton { Name = "navigate-" + id, Width = 86, Height = 48,
                Font = OwnFont(9), Margin = new Padding(0, 0, 0, 8), Cursor = Cursors.Hand,
                AccentColor = ModuleColors[i], AccessibleRole = AccessibleRole.PushButton }, names[i].Item1, names[i].Item2);
            button.Click += (_, _) => NavigateToModule(id);
            list.Controls.Add(button); _navigationButtons.Add(id, button);
        }
        list.SizeChanged += (_, _) => {
            // ClientSize already excludes the scrollbar. Subtracting it twice
            // silently clips three-character labels on 125% displays.
            var width = Math.Max(D(58), list.ClientSize.Width - D(2));
            foreach (var button in _navigationButtons.Values) button.Width = width;
        };
        _navigationTimer.Tick += (_, _) => AdvanceNavigation();
        SizeChanged += (_, _) => StopNavigation();
        return rail;
    }

    internal void NavigateToModule(string id)
    {
        if (!_uiReady || !_pages.ContainsKey(id)) return;
        StopNavigation(); UpdateResponsiveLayout();
        _navigationFrom = -_pageHost.AutoScrollPosition.Y;
        _navigationTo = Math.Clamp(_pages[id].Page.Top, 0, Math.Max(0, _allModules.Height - _pageHost.ClientSize.Height));
        _navigationDestination = id; _selectedPage = id; UpdateNavigation();
        if (!_animationsEnabled.Checked || !NativeMethods.ClientAreaAnimationsEnabled)
        { SelectPage(id); return; }
        _navigationStarted = Stopwatch.GetTimestamp(); _navigationTimer.Start();
    }

    private void AdvanceNavigation()
    {
        if (IsDisposed || !_uiReady) { StopNavigation(); return; }
        var progress = Math.Clamp(Stopwatch.GetElapsedTime(_navigationStarted).TotalMilliseconds / 220, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _navigationTick = true;
        try { _pageHost.AutoScrollPosition = new Point(0, (int)Math.Round(_navigationFrom + (_navigationTo - _navigationFrom) * eased)); }
        finally { _navigationTick = false; }
        if (progress >= 1)
        {
            var destination = _navigationDestination;
            StopNavigation();
            if (destination is not null) _selectedPage = destination;
            UpdateNavigation();
        }
    }

    private void StopNavigation() { _navigationTimer.Stop(); _navigationDestination = null; }
    private void UpdateNavigation()
    {
        foreach (var (id, button) in _navigationButtons)
        {
            button.Dark = _dark;
            button.Selected = id == (_navigationDestination ?? _selectedPage);
            button.AccessibleDescription = T("Scroll to this module; other modules remain open.", "滚动到这个模块，其他模块仍保持展开。");
            button.Invalidate();
        }
        for (var i = 0; i < _moduleOrder.Length; i++)
            if (_pages.TryGetValue(_moduleOrder[i], out var page) && page.Page.Controls.Count > 0)
                page.Page.Controls[0].ForeColor = _dark ? ControlPaint.Light(ModuleColors[i], .55f) : ModuleColors[i];
    }

    private sealed class ModuleNavigationButton : Button
    {
        public Color AccentColor { get; set; }
        public bool Selected { get; set; }
        public bool Dark { get; set; }
        private bool _hover, _pressed;
        public ModuleNavigationButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
            using var path = Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 10 * scale);
            var baseColor = Dark ? Color.FromArgb(31, 38, 48) : Color.White;
            var weight = Selected ? .18 : _hover ? .10 : .04;
            if (_pressed) weight += .12;
            var fillColor = Color.FromArgb((int)(baseColor.R * (1-weight) + AccentColor.R * weight),
                (int)(baseColor.G * (1-weight) + AccentColor.G * weight), (int)(baseColor.B * (1-weight) + AccentColor.B * weight));
            using var fill = new SolidBrush(fillColor); g.FillPath(fill, path);
            if (Selected) { using var border = new Pen(Color.FromArgb(155, AccentColor), scale); g.DrawPath(border, path); }
            using var marker = new SolidBrush(AccentColor);
            if (Selected) { using var pill = Rounded(new RectangleF(6*scale, Height/2f-9*scale, 3*scale, 18*scale), 1.5f*scale); g.FillPath(marker, pill); }
            else g.FillEllipse(marker, 6*scale, Height/2f-2*scale, 4*scale, 4*scale);
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(13*scale), 2, Width-(int)(18*scale), Height-4),
                Dark ? Color.FromArgb(225, 233, 245) : Color.FromArgb(40, 54, 74),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}
