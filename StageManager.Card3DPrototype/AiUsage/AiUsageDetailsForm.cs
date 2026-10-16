using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using StageManager.Settings;

namespace StageManager.Card3DPrototype.AiUsage;

internal sealed class AiUsageDetailsForm : Form
{
	private readonly AiUsageService _service;
	private readonly AiProviderIcons _icons = new();
	private readonly Button[] _tabs;
	private readonly Button _refresh, _settings, _close;
	private readonly AiUsageRows _rows = new();
	private readonly AiUsageTrendControl _trend = new();
	private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 16 };
	private readonly bool _preview;
	private IReadOnlyList<AiUsageSnapshot> _snapshots = Array.Empty<AiUsageSnapshot>();
	private UiLanguage _language;
	private string? _selectedProvider;
	private long _fadeStarted;
	private double _fadeFrom, _fadeTo;
	private bool _refreshing;
	private DateTimeOffset _lastRefreshClick = DateTimeOffset.MinValue;

	public AiUsageDetailsForm(AiUsageService service, UiLanguage language, bool preview = false)
	{
		_service = service; _language = language; _preview = preview;
		Text = "StageManager · AI usage";
		FormBorderStyle = preview ? FormBorderStyle.FixedSingle : FormBorderStyle.None;
		ShowInTaskbar = preview;
		// The sidebar itself deliberately is not topmost. Its owned flyout must
		// nevertheless appear over the adjacent foreground application's window.
		TopMost = !preview;
		StartPosition = FormStartPosition.Manual;
		AutoScaleMode = AutoScaleMode.None;
		ClientSize = new Size(376, 550);
		BackColor = Color.FromArgb(24, 27, 33);
		ForeColor = Color.FromArgb(239, 245, 252);
		DoubleBuffered = true;
		Opacity = preview ? 1 : 0;

		_tabs = AiUsagePresentation.ProviderOrder.Select(id =>
		{
			var button = MakeButton(id == "codex" ? "Codex" : id == "deepseek" ? "DeepSeek" : id == "gemini" ? "Gemini" : "Kimi");
			button.Tag = id;
			button.Click += (_, _) => SelectProvider(id);
			return button;
		}).ToArray();
		_refresh = MakeButton(""); _settings = MakeButton(""); _close = MakeButton("×");
		_close.AccessibleName = "Close AI usage";
		_close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
		_refresh.Click += (_, _) =>
		{
			if (_refreshing || DateTimeOffset.UtcNow - _lastRefreshClick < TimeSpan.FromSeconds(10)) return;
			_lastRefreshClick = DateTimeOffset.UtcNow;
			RefreshRequested?.Invoke(this, EventArgs.Empty);
		};
		_settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
		Controls.AddRange(_tabs);
		Controls.AddRange(new Control[] { _refresh, _settings, _close, _rows, _trend });
		_fadeTimer.Tick += (_, _) => AdvanceFade();
		ApplyContent();
	}

	public event EventHandler? RefreshRequested, SettingsRequested, CloseRequested;
	public bool IsPointerInside(Point screenPoint) => Visible && Bounds.Contains(screenPoint);
	public string? SelectedProvider => _selectedProvider;
	protected override bool ShowWithoutActivation => !_preview;
	protected override CreateParams CreateParams
	{
		get { var p = base.CreateParams; if (!_preview) p.ExStyle |= 0x08000000 | 0x00000080; return p; }
	}
	protected override void WndProc(ref Message m)
	{
		if (!_preview && m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE
		base.WndProc(ref m);
	}
	protected override void OnShown(EventArgs e)
	{
		base.OnShown(e);
		LayoutControls();
		if (!_preview) FadeTo(1);
	}
	protected override void OnResize(EventArgs e)
	{
		base.OnResize(e);
		if (_tabs is null) return;
		LayoutControls();
	}
	public void FitToWorkingArea(Rectangle area, float scale)
	{
		var s = Math.Clamp(scale, .75f, 3);
		ClientSize = new Size(Math.Min((int)(376 * s), area.Width), Math.Min((int)(550 * s), area.Height));
	}
	private float ScaleFactor => ClientSize.Width / 376f;
	private void LayoutControls()
	{
		var s = ScaleFactor;
		foreach (var button in _tabs.Append(_refresh).Append(_settings).Append(_close))
			((AiUsageButton)button).RenderScale = s;
		Rectangle R(float x, float y, float w, float h) => Rectangle.Round(new RectangleF(x * s, y * s, w * s, h * s));
		_close.Bounds = R(326, 14, 32, 32);
		var active = _tabs.Where(t => t.Visible).ToArray();
		var tabWidth = active.Length == 0 ? 108 : 340f / active.Length;
		for (var i = 0; i < active.Length; i++) active[i].Bounds = R(18 + tabWidth * i, 57, tabWidth - 6, 34);
		var bottom = ClientSize.Height;
		_refresh.Bounds = new Rectangle((int)(18 * s), bottom - (int)(46 * s), (int)(112 * s), (int)(30 * s));
		_settings.Bounds = new Rectangle((int)(246 * s), bottom - (int)(46 * s), (int)(112 * s), (int)(30 * s));
		_trend.Bounds = new Rectangle((int)(18 * s), bottom - (int)(184 * s), (int)(340 * s), (int)(122 * s));
		_rows.Bounds = new Rectangle((int)(18 * s), (int)(200 * s), (int)(340 * s),
			Math.Max((int)(48 * s), _trend.Top - (int)(212 * s)));
		_rows.ScaleFactor = s;
		if (!_preview)
		{
			using var path = AiUsagePresentation.Rounded(new RectangleF(0, 0, Width, Height), 16 * s);
			var old = Region; Region = new Region(path); old?.Dispose();
		}
		Invalidate();
	}
	public void SetLanguage(UiLanguage language)
	{
		if (_language == language) return;
		_language = language; ApplyContent();
	}
	public void SetSnapshots(IReadOnlyList<AiUsageSnapshot> snapshots)
	{
		_snapshots = snapshots.ToArray();
		if (!_snapshots.Any(s => s.ProviderId == _selectedProvider)) _selectedProvider = _snapshots.FirstOrDefault()?.ProviderId;
		ApplyContent();
	}
	public void SelectProvider(string? id)
	{
		if (id is null || !_snapshots.Any(s => s.ProviderId == id)) return;
		if (_selectedProvider == id) return;
		if (_selectedProvider != id) _rows.AutoScrollPosition = Point.Empty;
		_selectedProvider = id; ApplyContent();
	}
	public void SetRefreshing(bool refreshing)
	{
		_refreshing = refreshing;
		_refresh.Enabled = !refreshing;
		_refresh.Text = refreshing ? T("Refreshing…", "刷新中…") : T("Refresh", "刷新");
	}
	private AiUsageSnapshot? Selected => _snapshots.FirstOrDefault(s => s.ProviderId == _selectedProvider);
	private void ApplyContent()
	{
		if (_tabs is null) return;
		foreach (var tab in _tabs)
		{
			tab.Visible = _snapshots.Any(s => s.ProviderId == (string)tab.Tag!);
			((AiUsageButton)tab).Selected = (string)tab.Tag! == _selectedProvider;
		}
		_settings.Text = Selected?.NeedsConnection == true ? T("Connect", "连接设置") : T("Settings", "设置");
		SetRefreshing(_refreshing);
		_rows.SetSnapshot(Selected, _language);
		_trend.SetHistory(_selectedProvider is { } id ? _service.GetHistory(id) : new(Array.Empty<AiUsagePoint>(), ""), _language,
			AiUsagePresentation.Accent(_selectedProvider ?? ""));
		LayoutControls(); Invalidate();
	}
	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		var g = e.Graphics; var s = ScaleFactor;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		RectangleF R(float x, float y, float w, float h) => new(x * s, y * s, w * s, h * s);
		using var border = new Pen(Color.FromArgb(45, 190, 200, 215), Math.Max(1, s));
		using var outline = AiUsagePresentation.Rounded(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1), 16 * s);
		g.DrawPath(border, outline);
		Draw(g, T("AI usage", "AI 额度"), R(20, 16, 250, 28), 18 * s, ForeColor, true);
		if (Selected is not { } item)
		{
			Draw(g, T("Select services in Settings", "在设置中选择要显示的平台"), R(18, 98, 340, 48), 13 * s, ForeColor);
			return;
		}
		_icons.Draw(g, item.ProviderId, R(20, 108, 32, 32));
		var (value, caption) = AiUsagePresentation.Compact(item, _language);
		Draw(g, value, R(64, 99, 140, 46), 34 * s, ForeColor, true);
		Draw(g, item.HasData ? item.Windows is { Count: > 0 } ? T($"{caption} remaining", $"{caption} 剩余额度") : T($"{caption} API balance", $"{caption} API 余额")
			: T("Not connected", "尚未连接"), R(205, 112, 152, 25), 12 * s, Color.FromArgb(172, 181, 195));
		Draw(g, AiUsagePresentation.Status(item, _language), R(20, 144, 340, 22), 12 * s,
			item.Status == "Connected" ? AiUsagePresentation.Accent(item.ProviderId) : Color.FromArgb(223, 187, 128));
		var updated = item.UpdatedUtc is { } at ? T($"Updated {at.ToLocalTime():MM-dd HH:mm:ss}", $"更新于 {at.ToLocalTime():MM-dd HH:mm:ss}")
			: T("No successful update yet", "尚无有效数据");
		Draw(g, updated, R(20, 167, 340, 20), 11 * s, Color.FromArgb(153, 164, 180));
	}
	private static void Draw(Graphics g, string text, RectangleF bounds, float pixels, Color color, bool bold = false) =>
		AiUsagePresentation.DrawText(g, text, bounds, pixels, color, bold, StringAlignment.Near);
	private string T(string en, string zh) => UiText.Get(_language, en, zh);
	public void BeginFadeOut() => FadeTo(0);
	public void KeepOpen() { if (_fadeTo == 0) FadeTo(1); }
	private void FadeTo(double target)
	{
		if (IsDisposed || (_fadeTimer.Enabled && _fadeTo == target)) return;
		_fadeFrom = Opacity; _fadeTo = target; _fadeStarted = Stopwatch.GetTimestamp(); _fadeTimer.Start();
	}
	private void AdvanceFade()
	{
		var t = Math.Clamp(Stopwatch.GetElapsedTime(_fadeStarted).TotalMilliseconds / 120, 0, 1);
		Opacity = Math.Clamp(_fadeFrom + (_fadeTo - _fadeFrom) * (1 - Math.Pow(1 - t, 3)), 0, 1);
		if (t < 1) return;
		_fadeTimer.Stop(); if (_fadeTo == 0) Close();
	}
	private static Button MakeButton(string text)
	{
		var b = new AiUsageButton { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(24, 27, 33),
			ForeColor = Color.FromArgb(227, 237, 248), Cursor = Cursors.Hand, TabStop = true, UseVisualStyleBackColor = false };
		b.FlatAppearance.BorderSize = 0;
		b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 77, 96);
		b.FlatAppearance.MouseDownBackColor = Color.FromArgb(73, 96, 116);
		return b;
	}
	protected override void Dispose(bool disposing)
	{
		if (disposing) { _fadeTimer.Dispose(); _icons.Dispose(); }
		base.Dispose(disposing);
	}
}

// Draw at the current device size instead of scaling a small button bitmap.
internal sealed class AiUsageButton : Button
{
	private bool _hovered, _pressed, _selected;
	public float RenderScale { get; set; } = 1;
	public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }
	public AiUsageButton() { DoubleBuffered = true; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); }
	protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { _hovered = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
	protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
	protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics; var s = RenderScale;
		g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		using var shape = AiUsagePresentation.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 9 * s);
		var color = _pressed ? Color.FromArgb(66, 77, 91) : _hovered ? Color.FromArgb(53, 61, 73) :
			Selected ? Color.FromArgb(54, 65, 80) : Color.FromArgb(34, 39, 47);
		using var fill = new SolidBrush(color); g.FillPath(fill, shape);
		if (Selected || Focused && ShowFocusCues)
		{
			using var outline = new Pen(Color.FromArgb(100, 155, 183, 211), s); g.DrawPath(outline, shape);
		}
		AiUsagePresentation.DrawText(g, Text, new RectangleF(6 * s, 0, Width - 12 * s, Height),
			Text == "×" ? 20 * s : 12 * s, Enabled ? ForeColor : Color.FromArgb(135, 145, 160), Selected);
	}
}

internal sealed class AiUsageRows : ScrollableControl
{
	private AiUsageSnapshot? _snapshot;
	private UiLanguage _language;
	private float _scale = 1;
	public float ScaleFactor { get => _scale; set { _scale = value; UpdateExtent(); } }
	public AiUsageRows() { DoubleBuffered = true; AutoScroll = true; BackColor = Color.FromArgb(24, 27, 33); }
	public void SetSnapshot(AiUsageSnapshot? snapshot, UiLanguage language)
	{
		_snapshot = snapshot; _language = language;
		AccessibleName = snapshot is null ? "No AI data" : snapshot.Name + ": " + string.Join(", ", snapshot.Details.Select(d => d.Label + " " + d.Value));
		UpdateExtent(); Invalidate();
	}
	private void UpdateExtent()
	{
		var count = _snapshot?.Windows?.Count ?? 0;
		AutoScrollMinSize = new Size(0, (int)((count > 0 ? count * 66 : Math.Max(1, _snapshot?.Details.Count ?? 0) * 29 + 12) * _scale));
		Invalidate();
	}
	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		var g = e.Graphics; var s = _scale;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		var state = g.Save(); g.TranslateTransform(0, AutoScrollPosition.Y);
		var width = ClientSize.Width - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
		void Text(string text, float x, float y, float w, float h, float font, Color color, bool bold = false) =>
			AiUsagePresentation.DrawText(g, text, new RectangleF(x * s, y * s, w * s, h * s), font * s, color, bold, StringAlignment.Near);
		var inner = width / s - 20;
		if (_snapshot?.Windows is { Count: > 0 } windows)
		{
			for (var i = 0; i < windows.Count; i++)
			{
				var w = windows[i]; var y = i * 66f;
				Text($"{w.Label} · {AiUsagePresentation.Duration(w.DurationMinutes)}", 10, y + 3, inner - 65, 20, 12, ForeColor);
				Text($"{w.RemainingPercent:0}%", width / s - 60, y + 3, 50, 20, 12, AiUsagePresentation.Accent("codex"), true);
				using var track = new SolidBrush(Color.FromArgb(54, 70, 84));
				using var fill = new SolidBrush(AiUsagePresentation.Accent("codex"));
				using var trackPath = AiUsagePresentation.Rounded(new RectangleF(10 * s, (y + 29) * s, inner * s, 5 * s), 2.5f * s);
				g.FillPath(track, trackPath);
				if (w.RemainingPercent > 0)
				{
					using var fillPath = AiUsagePresentation.Rounded(new RectangleF(10 * s, (y + 29) * s, (float)w.RemainingPercent / 100 * inner * s, 5 * s), 2.5f * s);
					g.FillPath(fill, fillPath);
				}
				Text(w.ResetsAt is { } at ? UiText.Get(_language, $"Resets {at.ToLocalTime():MM-dd HH:mm}", $"重置于 {at.ToLocalTime():MM-dd HH:mm}")
					: UiText.Get(_language, "Reset time not supplied", "未提供重置时间"), 10, y + 38, inner, 20, 11, Color.FromArgb(156, 177, 198));
			}
		}
		else if (_snapshot?.Details is { Count: > 0 } rows)
		{
			for (var i = 0; i < rows.Count; i++)
			{
				var label = rows[i].Label switch { "Topped up" => UiText.Get(_language, "Topped up", "充值余额"),
					"Granted" => UiText.Get(_language, "Granted", "赠金"), "Available" => UiText.Get(_language, "Available", "可用余额"),
					"Cash" => UiText.Get(_language, "Cash", "现金余额"), "Voucher" => UiText.Get(_language, "Voucher", "代金券"), _ => rows[i].Label };
				Text(label, 10, 6 + i * 29, inner * .56f, 23, 12, Color.FromArgb(170, 190, 211));
				Text(rows[i].Value, 10 + inner * .56f, 6 + i * 29, inner * .44f, 23, 12, Color.White, true);
			}
		}
		else
			Text(UiText.Get(_language, "Connect a provider to see real usage.", "连接平台后显示真实额度。"), 10, 10, inner, 50, 11, Color.FromArgb(166, 186, 207));
		g.Restore(state);
	}
}

internal sealed class AiUsageTrendControl : Control
{
	private AiUsageHistory _history = new(Array.Empty<AiUsagePoint>(), "");
	private UiLanguage _language;
	private Color _accent;
	public AiUsageTrendControl() { DoubleBuffered = true; BackColor = Color.FromArgb(24, 27, 33); }
	public void SetHistory(AiUsageHistory history, UiLanguage language, Color accent)
	{ _history = history; _language = language; _accent = accent; Invalidate(); }
	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		var g = e.Graphics; var s = Width / 340f;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		var labelColor = Color.FromArgb(150, 173, 194);
		void Text(string text, RectangleF r, float size = 11) => AiUsagePresentation.DrawText(g, text, r, size * s, labelColor, alignment: StringAlignment.Near);
		Text(UiText.Get(_language, "LAST 24 HOURS", "最近 24 小时"), new(10 * s, 6 * s, Width - 20 * s, 18 * s));
		var plot = new RectangleF(34 * s, 33 * s, Width - 46 * s, Height - 54 * s);
		if (plot.Height <= 0) return;
		using var grid = new Pen(Color.FromArgb(40, 154, 178, 202), s);
		for (var i = 0; i < 3; i++) g.DrawLine(grid, plot.Left, plot.Top + i * plot.Height / 2, plot.Right, plot.Top + i * plot.Height / 2);
		var now = DateTimeOffset.UtcNow;
		var points = _history.Points.Where(p => p.At >= now.AddHours(-24) && p.At <= now).OrderBy(p => p.At).ToArray();
		Text("-24h", new(plot.Left, plot.Bottom + 4 * s, 45 * s, 16 * s), 10);
		Text(UiText.Get(_language, "Now", "现在"), new(plot.Right - 30 * s, plot.Bottom + 4 * s, 30 * s, 16 * s), 10);
		if (points.Length == 0)
		{
			Text(UiText.Get(_language, "No samples yet · no estimated data", "暂无采样 · 不生成估算数据"), plot, 10); return;
		}
		var min = _history.Unit == "percent" ? 0 : Math.Min(0, points.Min(p => p.Value));
		var max = _history.Unit == "percent" ? 100 : Math.Max(1, points.Max(p => p.Value) * 1.1m);
		Text(max.ToString("0.#"), new(4 * s, plot.Top - 7 * s, 29 * s, 16 * s), 10);
		Text(min.ToString("0.#"), new(4 * s, plot.Bottom - 7 * s, 29 * s, 16 * s), 10);
		using var pen = new Pen(_accent, 1.8f * s) { LineJoin = LineJoin.Round };
		using var dot = new SolidBrush(_accent);
		PointF? last = null;
		foreach (var p in points)
		{
			var point = new PointF(plot.Left + (float)((p.At - now.AddHours(-24)).TotalHours / 24) * plot.Width,
				plot.Bottom - (float)((p.Value - min) / (max - min)) * plot.Height);
			if (!p.BreakBefore && last is { } prior) g.DrawLine(pen, prior, point);
			g.FillEllipse(dot, point.X - 2 * s, point.Y - 2 * s, 4 * s, 4 * s);
			last = point;
		}
		if (points.Length == 1)
			Text(UiText.Get(_language, "First sample · updated every 5 min", "已有首次采样 · 每 5 分钟更新"), new(plot.Left, plot.Top, plot.Width - 10 * s, 20 * s));
	}
}

