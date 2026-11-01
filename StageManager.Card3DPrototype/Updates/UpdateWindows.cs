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
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(640, 500); MinimumSize = new Size(570, 420);
        Font = new Font("Microsoft YaHei UI", 11); BackColor = Color.FromArgb(24, 27, 33); ForeColor = Color.White;
        _status.Height = 84; _notes.Margin = new Padding(18);
        _notes.BackColor = BackColor; _notes.ForeColor = ForeColor; _notes.BorderStyle = BorderStyle.None;
        _install.Text = T("Download and update", "下载并升级"); _install.Enabled = false;
        _cancel.Text = T("Later", "稍后"); _page.Text = T("Release page", "查看发布页");
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 74, Padding = new Padding(12), FlowDirection = FlowDirection.RightToLeft };
        foreach (var button in new[] { _cancel, _install, _page }) { button.MinimumSize = new Size(145, 44); button.FlatStyle = FlatStyle.Flat; button.ForeColor = Color.Black; button.BackColor = Color.FromArgb(225, 234, 245); buttons.Controls.Add(button); }
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
    public event EventHandler? OpenSettings;
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
            "Choose your display\nSettings → Workspace → Display: automatic leftmost, primary, or a connected monitor. A disconnected selection temporarily falls back to the primary screen and is remembered for reconnection. All four dock edges are supported.\n\nSmoother settings & AI refresh\nWheel-scroll across settings controls without changing their values. Compact sections, configurable AI refresh (30–3600 s) and automatic Codex CLI discovery.\n\nOptional Outlook calendar\nA compact date card with a month/agenda flyout and local completion ticks. Off by default. Manual sync via a connected Codex Outlook plugin; an encrypted local cache keeps data on this PC.",
            "选择卡片栏所在屏幕\n设置 → 工作区 → 显示器，可选自动最左屏、主显示器或指定屏幕。断开时临时回到主屏并记住选择，重连后返回；支持四边停靠。\n\n设置滚动与额度刷新\n鼠标在设置控件上也可滚动整页，不会误改数值。布局更紧凑，AI 刷新间隔可设 30–3600 秒，Codex CLI 支持自动查找。\n\n可选 Outlook 日历\n紧凑日期卡、月历与日程详情及本机完成勾选，默认关闭。通过 Codex 已连接的 Outlook 插件手动刷新，本机缓存由当前 Windows 用户加密保护。") };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var done = new Button { Text = T("Got it", "知道了"), AutoSize = true, ForeColor = Color.Black, BackColor = Color.FromArgb(225, 234, 245) };
        var settings = new Button { Text = T("Open settings", "打开设置"), AutoSize = true, ForeColor = Color.Black, BackColor = Color.FromArgb(225, 234, 245) };
        done.Click += (_, _) => Close(); settings.Click += (_, _) => { Close(); OpenSettings?.Invoke(this, EventArgs.Empty); };
        footer.Controls.Add(done); footer.Controls.Add(settings);
        Controls.Add(body); Controls.Add(title); Controls.Add(footer);
        Shown += (_, _) => { if (recordSeen) WhatsNewState.MarkShown(); };
    }
}
