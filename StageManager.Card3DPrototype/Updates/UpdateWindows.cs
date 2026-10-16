using System.Diagnostics;
using System.Text.Json;
using StageManager.Settings;

namespace StageManager.Card3DPrototype.Updates;

internal sealed class UpdateForm : Form
{
    private readonly ReleaseUpdateService _service = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _download;
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 62, Padding = new Padding(12) };
    private readonly TextBox _notes = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 12 };
    private readonly Button _install = new() { AutoSize = true }, _cancel = new() { AutoSize = true }, _page = new() { AutoSize = true };
    private UpdateRelease? _release;
    private bool _installing;
    private bool _disposed;
    private readonly UiLanguage _language;
    public UpdateForm(UiLanguage language)
    {
        _language = language; Text = T("Check for updates", "检查更新");
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(520, 430); MinimumSize = new Size(450, 350);
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(24, 27, 33); ForeColor = Color.White;
        _notes.BackColor = BackColor; _notes.ForeColor = ForeColor; _notes.BorderStyle = BorderStyle.None;
        _install.Text = T("Download and update", "下载并升级"); _install.Enabled = false;
        _cancel.Text = T("Later", "稍后"); _page.Text = T("Release page", "查看发布页");
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(10), FlowDirection = FlowDirection.RightToLeft };
        foreach (var button in new[] { _cancel, _install, _page }) { button.ForeColor = Color.Black; button.BackColor = Color.FromArgb(225, 234, 245); buttons.Controls.Add(button); }
        Controls.Add(_notes); Controls.Add(_status); Controls.Add(_progress); Controls.Add(buttons);
        _page.Click += (_, _) => Process.Start(new ProcessStartInfo(_release?.Page.AbsoluteUri ?? AppVersion.ReleasesUrl) { UseShellExecute = true });
        _cancel.Click += (_, _) => { if (_download is not null) _download.Cancel(); else Close(); };
        _install.Click += async (_, _) => await InstallAsync();
        Shown += async (_, _) => await CheckAsync();
    }
    private string T(string en, string zh) => UiText.Get(_language, en, zh);
    private async Task CheckAsync()
    {
        _status.Text = AppVersion.Label + "\n" + T("Checking…", "正在检查…");
        try
        {
            _release = await _service.CheckAsync(_lifetime.Token);
            if (IsDisposed) return;
            var comparison = _release.Version.CompareTo(AppVersion.Current);
            _status.Text = comparison > 0 ? T($"New version {_release.Tag} · {_release.Published.ToLocalTime():yyyy-MM-dd}", $"发现新版 {_release.Tag} · {_release.Published.ToLocalTime():yyyy-MM-dd}") :
                comparison == 0 ? T("You are up to date.", "当前已是最新版。") : T("Your local version is newer than the public release.", "当前为较新的本机试用版，公开版本较旧。");
            _notes.Text = _release.Notes; _install.Enabled = comparison > 0;
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = T("Check cancelled or timed out.", "检查已取消或超时，未能确认最新版本。"); }
        catch (Exception) { if (!IsDisposed) _status.Text = T("Could not check updates. Open the release page or try again later.", "检查失败，请稍后重试或打开发布页。未能确认最新版本。"); }
    }
    private async Task InstallAsync()
    {
        if (_release is null || _download is not null) return;
        _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _install.Enabled = false; _cancel.Text = T("Cancel download", "取消下载");
        _status.Text = T("Downloading and verifying…", "正在下载并校验…");
        try
        {
            var transaction = await _service.DownloadAsync(_release, new Progress<int>(p => { if (!IsDisposed) _progress.Value = Math.Clamp(p, 0, 100); }), _download.Token);
            _download.Token.ThrowIfCancellationRequested();
            _installing = true; _cancel.Enabled = false;
            _status.Text = T("Verified. Preparing restart…", "校验通过，正在准备重启…");
            await transaction.ArmAsync(CancellationToken.None);
            _installing = false;
            Application.Exit();
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = T("Download cancelled. Current version is unchanged.", "下载已取消，当前程序未改动。"); }
        catch (Exception) { if (!IsDisposed) _status.Text = T("Update stopped. Current version is retained; use the release page for manual installation.", "升级已停止。当前版本保留，可到发布页手动下载。请检查网络、权限及磁盘空间。"); }
        finally
        {
            _installing = false; _download?.Dispose(); _download = null;
            if (!IsDisposed) { _install.Enabled = true; _cancel.Enabled = true; _cancel.Text = T("Later", "稍后"); }
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        _lifetime.Cancel(); base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    { if (disposing && !_disposed) { _disposed = true; _lifetime.Cancel(); _service.Dispose(); _lifetime.Dispose(); } base.Dispose(disposing); }
}

internal static class WhatsNewState
{
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "update-state.json");
    public static bool ShouldShow(string? path = null, string? version = null)
    {
        path ??= PathName; version ??= AppVersion.Current.ToString();
        try { return !File.Exists(path) || JsonSerializer.Deserialize<Seen>(File.ReadAllText(path))?.Version != version; }
        catch { return true; }
    }
    public static void MarkShown(string? path = null, string? version = null)
    {
        path ??= PathName; version ??= AppVersion.Current.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Seen(version)));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed record Seen(string Version);
}

internal sealed class WhatsNewForm : Form
{
    public event EventHandler? OpenAiSettings;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }
    public WhatsNewForm(UiLanguage language, bool recordSeen = true)
    {
        string T(string en, string zh) => UiText.Get(language, en, zh);
        Text = T("What's new", "本次更新") + " · " + AppVersion.Current;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(490, 480); MinimumSize = new Size(440, 440);
        ShowInTaskbar = false; TopMost = true; Font = new Font("Segoe UI", 11);
        BackColor = Color.FromArgb(24, 27, 33); ForeColor = Color.FromArgb(236, 241, 248);
        var title = new Label { Text = AppVersion.Label, Dock = DockStyle.Top, Height = 66, Padding = new Padding(22, 20, 0, 0), Font = new Font("Segoe UI", 18, FontStyle.Bold) };
        var body = new Label { Dock = DockStyle.Fill, Padding = new Padding(22, 8, 22, 8), Text = T(
            "Four-provider AI grid\nCodex / DeepSeek above Gemini / Kimi. Existing choices are preserved.\n\nExperimental Gemini web quota\nOptional browser companion reads subscription quota, not API credits. Install it separately from the release page.\n\nConnection status\nShows missing login and stale browser data. Real-account validation is pending.\n\nMonochrome Codex logo\nBlack-and-white mark and corrected grid selection.",
            "两行两列 AI 额度卡片\n上排 Codex / DeepSeek，下排 Gemini / Kimi，保留已有选择。\n\n实验性 Gemini 网页额度\n读取订阅用量而非 API 余额，需从发布页另装浏览器配套扩展。\n\n连接与过期状态\n明确提示待登录或旧数据，真实账号读取尚待验证。\n\n黑白 Codex 图标\n更新标识，并修正四格平台点击定位。") };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var done = new Button { Text = T("Got it", "知道了"), AutoSize = true, ForeColor = Color.Black, BackColor = Color.FromArgb(225, 234, 245) };
        var settings = new Button { Text = T("AI settings", "打开 AI 设置"), AutoSize = true, ForeColor = Color.Black, BackColor = Color.FromArgb(225, 234, 245) };
        done.Click += (_, _) => Close(); settings.Click += (_, _) => { Close(); OpenAiSettings?.Invoke(this, EventArgs.Empty); };
        footer.Controls.Add(done); footer.Controls.Add(settings);
        Controls.Add(body); Controls.Add(title); Controls.Add(footer);
        Shown += (_, _) => { if (recordSeen) WhatsNewState.MarkShown(); };
    }
}
