using System.Diagnostics;
using StageManager.Card3DPrototype;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Settings;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		try
		{
			if (args.Contains("--antigravity"))
			{
				var result = AntigravityUsageClient.ReadAsync(default).GetAwaiter().GetResult();
				Console.WriteLine($"Antigravity: {result.Status}");
				foreach (var window in result.Windows ?? Array.Empty<AiQuotaWindow>())
					Console.WriteLine($"{window.Label}: {window.RemainingPercent:0.##}% remaining, reset {window.ResetsAt?.ToLocalTime():yyyy-MM-dd HH:mm}");
				using var icons = new AiProviderIcons();
				using var bitmap = new Bitmap(300, 360);
				using var graphics = Graphics.FromImage(bitmap);
				graphics.Clear(Color.FromArgb(22, 27, 36));
				AiUsagePresentation.PaintCard(graphics, bitmap.Size, 2, new[] { AiUsageRegressionTests.CodexFixture(), AiUsageService.Empty("deepseek", "Waiting for API key"), AiUsageService.Empty("gemini", "Gemini browser connection required"), AiUsageService.Empty("kimi", "Waiting for API key"), result }, UiLanguage.SimplifiedChinese, icons);
				bitmap.Save(Path.Combine(AppContext.BaseDirectory, "antigravity-grid.png"));
				Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
				using var previewService = new AiUsageService(dataDirectory: Path.Combine(AppContext.BaseDirectory, "visual-fixtures"));
				foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
				{
					using var panel = new AiUsageDetailsForm(previewService, UiLanguage.SimplifiedChinese, preview: true) { FormBorderStyle = FormBorderStyle.None };
					panel.SetSnapshots(new[] { AiUsageRegressionTests.CodexFixture(), AiUsageService.Empty("deepseek", "Waiting for API key"), AiUsageService.Empty("gemini", "Gemini browser connection required"), AiUsageService.Empty("kimi", "Waiting for API key"), result });
					panel.SelectProvider("antigravity"); panel.FitToWorkingArea(new Rectangle(0, 0, 2400, 2400), scale);
					panel.Show(); Application.DoEvents();
					var rows = panel.Controls.OfType<AiUsageRows>().Single();
					if (rows.VerticalScroll.Visible) throw new Exception($"Antigravity rows require scrolling at {scale} DPI.");
					using var rendered = new Bitmap(panel.Width, panel.Height);
					panel.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
					rendered.Save(Path.Combine(AppContext.BaseDirectory, $"antigravity-details-{scale * 100:0}.png"));
					Console.WriteLine($"Details {scale * 100:0}%: all quota rows visible without scrolling.");
					panel.Close();
				}
				return result.Status == "Connected" ? 0 : 1;
			}
			if (args.Contains("--check-update"))
			{
				using var service = new StageManager.Card3DPrototype.Updates.ReleaseUpdateService();
				var release = service.CheckAsync(default).GetAwaiter().GetResult();
				Console.WriteLine($"Current={StageManager.Card3DPrototype.Updates.AppVersion.Current}; public={release.Tag}; assets={release.Assets.Count}");
				return 0;
			}
			if (args.Contains("--render-updates"))
			{
				Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
				foreach (var language in new[] { UiLanguage.SimplifiedChinese, UiLanguage.English })
				{
					using var news = new StageManager.Card3DPrototype.Updates.WhatsNewForm(language, recordSeen: false);
					news.Show(); Application.DoEvents();
					using var bitmap = new Bitmap(news.Width, news.Height);
					news.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
					var path = Path.Combine(AppContext.BaseDirectory, "whats-new-" + language + ".png"); bitmap.Save(path); Console.WriteLine(path);
				}
				return 0;
			}
			if (args.Contains("--popup-smoke"))
			{
				Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
				Application.EnableVisualStyles();
				using var service = new AiUsageService(dataDirectory: Path.Combine(AppContext.BaseDirectory, "visual-fixtures"));
				using var owner = new Form();
				owner.Show();
				using var panel = new AiUsageDetailsForm(service, UiLanguage.SimplifiedChinese);
				panel.SetSnapshots(new[] { AiUsageRegressionTests.CodexFixture() });
				panel.Show(owner);
				var clock = Stopwatch.StartNew();
				while(clock.ElapsedMilliseconds < 500) { Application.DoEvents(); Thread.Sleep(10); }
				Console.WriteLine($"Popup visible={panel.Visible} opacity={panel.Opacity} topmost={panel.TopMost} bounds={panel.Bounds}");
				return panel.Visible && panel.Opacity > .95 && panel.TopMost ? 0 : 1;
			}
			if (args.Contains("--render-details"))
			{
				Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
				Application.EnableVisualStyles();
				using var service = new AiUsageService(dataDirectory: Path.Combine(AppContext.BaseDirectory, "visual-fixtures"));
				foreach (var scale in new[] { 1f, 1.5f, 2f })
				{
					using var panel = new AiUsageDetailsForm(service, UiLanguage.SimplifiedChinese, preview: true)
					{ FormBorderStyle = FormBorderStyle.None };
					panel.SetSnapshots(new[] { AiUsageRegressionTests.CodexFixture(), AiUsageService.Empty("deepseek", "Waiting for API key") });
					panel.FitToWorkingArea(new Rectangle(0, 0, 2400, 2400), scale);
					panel.Show();
					Application.DoEvents();
					using var bitmap = new Bitmap(panel.Width, panel.Height);
					panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
					var output = Path.Combine(AppContext.BaseDirectory, $"details-{scale * 100:0}.png");
					bitmap.Save(output); Console.WriteLine(output);
				}
				return 0;
			}
			if (args.Contains("--native-ui"))
			{
				Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
				Application.Run(new NativeAiPreview()); return 0;
			}
			if (args.Contains("--live-codex"))
			{
				var watch = Stopwatch.StartNew();
				var result = CodexUsageClient.ReadAsync("", CancellationToken.None).GetAwaiter().GetResult();
				Console.WriteLine($"Codex: {result.Status}; windows={result.Windows?.Count ?? 0}; elapsed={watch.Elapsed.TotalSeconds:F2}s");
				foreach (var window in result.Windows ?? Array.Empty<AiQuotaWindow>())
					Console.WriteLine($"{window.Label} {AiUsagePresentation.Duration(window.DurationMinutes)} remaining={window.RemainingPercent}% reset={window.ResetsAt:O}");
				return result.Status == "Connected" ? 0 : 1;
			}
			if (args.Contains("--ui"))
			{
				Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
				Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
				Application.Run(new AiPreviewForm(args.Contains("--live")));
				return 0;
			}
			AiUsageRegressionTests.RunAll();
			return 0;
		}
		catch (Exception e) { Console.WriteLine(e); return 1; }
	}
}

internal sealed class AiPreviewForm : Form
{
	private readonly AiUsageService _service = new(dataDirectory: Path.Combine(AppContext.BaseDirectory, "preview-data"));
	private readonly CardCanvas _cards = new();
	private readonly AiUsageDetailsForm _details;
	private readonly bool _live;
	private UiLanguage _language = UiLanguage.SimplifiedChinese;
	private IReadOnlyList<AiUsageSnapshot> _items = Array.Empty<AiUsageSnapshot>();

	public AiPreviewForm(bool live)
	{
		_live = live;
		Text = live ? "StageManager AI — live account verification" : "StageManager AI — visual test fixtures (not account data)";
		StartPosition = FormStartPosition.CenterScreen;
		ClientSize = new Size(730, 640);
		BackColor = Color.FromArgb(45, 61, 79); ForeColor = Color.White;
		_details = new AiUsageDetailsForm(_service, _language, preview: true) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Location = new Point(330, 75) };
		Controls.Add(_details);
		_cards.Bounds = new Rectangle(20, 80, 285, 500); Controls.Add(_cards);
		var two = new Button { Text = "2 providers", Bounds = new Rectangle(20, 20, 110, 32) };
		var three = new Button { Text = "3 providers", Bounds = new Rectangle(140, 20, 110, 32) };
		var language = new Button { Text = "EN / 中文", Bounds = new Rectangle(260, 20, 110, 32) };
		var settings = new Button { Text = "Settings", Bounds = new Rectangle(380, 20, 110, 32) };
		var popup = new Button { Text = "Popup", Bounds = new Rectangle(500, 20, 110, 32) };
		foreach (var b in new[] { two, three, language, settings, popup }) { b.ForeColor = Color.Black; Controls.Add(b); }
		two.Click += (_, _) => SetItems(_items.Where(i => i.ProviderId != "kimi").ToArray());
		three.Click += (_, _) => SetItems(_items.Where(i => i.ProviderId != "kimi").Append(AiUsageService.Empty("kimi", "Waiting for API key")).ToArray());
		language.Click += (_, _) => { _language = _language == UiLanguage.English ? UiLanguage.SimplifiedChinese : UiLanguage.English; SetItems(_items); };
		settings.Click += (_, _) => OpenSettings();
		popup.Click += (_, _) =>
		{
			var panel = new AiUsageDetailsForm(_service, _language) { Location = new Point(Right - 300, Top + 80) };
			panel.SetSnapshots(_items); panel.CloseRequested += (_, _) => panel.Close(); panel.Show(this);
		};
		_details.SettingsRequested += (_, _) => OpenSettings();
		_details.RefreshRequested += async (_, _) => { if (_live) { await _service.RefreshAsync(new AppSettings(), true); SetItems(_service.Current); } };
	}
	private void OpenSettings()
	{
		using var dialog = new SettingsForm(new AppSettings { UiLanguage = _language }, aiUsageService: _service) { ShowInTaskbar = true };
		dialog.Shown += (_, _) => dialog.ScrollToAiUsage();
		dialog.ShowDialog(this); // Isolated draft: never applies production settings.
	}
	protected override async void OnShown(EventArgs e)
	{
		base.OnShown(e);
		SetItems(new[] { AiUsageService.Empty("codex", "Loading"), AiUsageService.Empty("deepseek", "Waiting for API key") });
		_details.Show();
		if (_live)
		{
			await _service.RefreshAsync(new AppSettings(), false); SetItems(_service.Current);
		}
		else
		{
			SetItems(new[] { AiUsageRegressionTests.CodexFixture(), AiUsageService.Empty("deepseek", "Waiting for API key") });
		}
	}
	private void SetItems(IReadOnlyList<AiUsageSnapshot> items)
	{
		_items = items; _cards.Items = items; _cards.Language = _language; _cards.Invalidate();
		_details.SetLanguage(_language); _details.SetSnapshots(items);
	}
	protected override void OnFormClosed(FormClosedEventArgs e) { _service.Dispose(); base.OnFormClosed(e); }
}

internal sealed class CardCanvas : Control
{
	private readonly AiProviderIcons _icons = new();
	public IReadOnlyList<AiUsageSnapshot> Items { get; set; } = Array.Empty<AiUsageSnapshot>();
	public UiLanguage Language { get; set; }
	public CardCanvas() { DoubleBuffered = true; }
	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		var y = 10;
		foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
		{
			using var label = new SolidBrush(Color.White);
			e.Graphics.DrawString($"59% card · {dpi * 100:0}% DPI", Font, label, 0, y);
			y += 25;
			var size = new Size((int)(196 * .59 * dpi), (int)(AiUsagePresentation.CardHeight * dpi));
			var saved = e.Graphics.Save(); e.Graphics.TranslateTransform(0, y);
			using var rounded = AiUsagePresentation.Rounded(new RectangleF(PointF.Empty, size), 8 * dpi);
			using var bg = new SolidBrush(Color.FromArgb(180, 22, 27, 36)); e.Graphics.FillPath(bg, rounded);
			AiUsagePresentation.PaintCard(e.Graphics, size, dpi, Items, Language, _icons);
			e.Graphics.Restore(saved); y += size.Height + 18;
		}
	}
	protected override void Dispose(bool disposing) { if (disposing) _icons.Dispose(); base.Dispose(disposing); }
}

