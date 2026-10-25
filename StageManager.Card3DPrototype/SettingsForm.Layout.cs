using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using StageManager.Settings;
using StageManager.Card3DPrototype.QuickLaunch;
using StageManager.Card3DPrototype.Updates;

namespace StageManager.Card3DPrototype;

internal sealed partial class SettingsForm
{
    private readonly List<Action> _localized = new();
    private readonly List<Font> _ownedFonts = new();
    private readonly Dictionary<string, (Panel Page, string En, string Zh, string HintEn, string HintZh)> _pages = new();
    private Panel _pageHost = null!, _footer = null!;
    private SettingsPageStack _allModules = null!;
    private TextBox _search = null!;
    private bool _fittingModules;
    private readonly string[] _moduleOrder = { "general", "preferences", "cards", "windows", "shortcuts", "updates", "ai" };
    private Label _pageTitle = null!, _pageHint = null!;
    private ComboBox _settingsTheme = null!, _settingsTextSize = null!;
    private CheckBox _rememberSettingsPage = null!, _showWhatsNew = null!, _automaticUpdates = null!;
    private string _selectedPage = "general";
    private bool _uiReady;
    private bool _dark;
    private float _appliedTextSize;
    private Color Surface => _dark ? Color.FromArgb(32, 37, 46) : Color.White;
    private Color Muted => _dark ? Color.FromArgb(166, 179, 198) : Color.FromArgb(91, 107, 128);
    private Color Accent => _dark ? Color.FromArgb(118, 181, 255) : Color.FromArgb(31, 99, 197);
    private string T(string en, string zh) => UiText.Get(Draft.UiLanguage, en, zh);
    private int D(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private TControl Localize<TControl>(TControl control, string en, string zh) where TControl : Control
    {
        _localized.Add(() => { control.Text = T(en, zh); control.AccessibleName = control.Text; });
        control.Text = T(en, zh); control.AccessibleName = control.Text;
        return control;
    }

    private void BuildSettingsUi(IReadOnlyList<PrototypeApplicationChoice> choices)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = true;
        MinimumSize = new Size(760, 560);
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(1060, screen.Width - 60), Math.Min(860, screen.Height - 80));
        Font = OwnFont(11);
        _footer = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(20, 14, 20, 14), Tag = "footer" };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 260, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = ActionButton("Save changes", "保存设置", (_, _) => SaveButton_Click(this, EventArgs.Empty), primary: true);
        save.Width = 132;
        var cancel = ActionButton("Cancel", "取消", (_, _) => Close()); cancel.DialogResult = DialogResult.Cancel; cancel.Width = 100;
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        var reset = ActionButton("Reset defaults", "恢复默认", (_, _) =>
        {
            if (MessageBox.Show(this, T("Reset these options? Changes take effect only after saving. Saved API keys are retained.", "恢复这些选项的默认值？保存后才会生效，已保存的 API 密钥会保留。"), T("Reset defaults", "恢复默认"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) ResetDefaults();
        });
        reset.Dock = DockStyle.Left; reset.Width = 140;
        _footer.Controls.Add(actions); _footer.Controls.Add(reset);
        AcceptButton = save; CancelButton = cancel;
        var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 20, 0) };
        var heading = new Panel { Dock = DockStyle.Top, Height = 110 };
        var brand = new SmoothLabel { Text = "STAGE MANAGER  /  LAI  " + AppVersion.Current, Dock = DockStyle.Top, Height = 28, Font = OwnFont(9, FontStyle.Bold), Tag = "muted" };
        _pageTitle = new SmoothLabel { Dock = DockStyle.Fill, Font = OwnFont(23, FontStyle.Bold) };
        _pageHint = new SmoothLabel { Dock = DockStyle.Fill, Tag = "muted" };
        var titleRow = new Panel { Dock = DockStyle.Top, Height = 54 };
        var searchHost = new Panel { Dock = DockStyle.Right, Width = 252, Padding = new Padding(0, 0, 8, 0) };
        _search = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle };
        searchHost.Controls.Add(_search);
        searchHost.Controls.Add(Localize(new SmoothLabel { Dock = DockStyle.Top, Height = 20, Font = OwnFont(9), Tag = "muted" }, "FIND A SETTING · ENTER", "查找设置 · 按回车定位"));
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { FindSetting(); e.SuppressKeyPress = true; } };
        _localized.Add(() => { _search.PlaceholderText = T("Find a setting · Enter", "查找设置 · 按回车定位"); _search.AccessibleName = T("Find a setting", "查找设置"); });
        titleRow.Controls.Add(_pageTitle); titleRow.Controls.Add(searchHost);
        heading.Controls.Add(_pageHint); heading.Controls.Add(titleRow); heading.Controls.Add(brand);
        _pageHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Name = "all-settings" };
        _allModules = new SettingsPageStack { FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 0, 0, 24), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _pageHost.Controls.Add(_allModules);
        _pageHost.SizeChanged += (_, _) => UpdateResponsiveLayout();
        _pageHost.Scroll += (_, _) => { if (!_navigationTick) StopNavigation(); RememberVisibleModule(); };
        _pageHost.MouseWheel += (_, _) => StopNavigation();
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_pageHost); body.Controls.Add(BuildNavigation());
        main.Controls.Add(body); main.Controls.Add(heading);
        Controls.Add(main); Controls.Add(_footer);

        var general = Page("general", "Workspace", "工作区", "Your everyday controls: mode, position and card size.", "常用的模式、停靠位置和卡片大小，优先放在这里。");
        var preferences = Page("preferences", "Preferences", "偏好设置", "Appearance, reading comfort and startup habits.", "调整阅读体验、界面外观与启动习惯。");
        var cards = Page("cards", "Cards & launchers", "卡片与快捷启动", "Choose the apps and tools in your dock.", "选择卡片栏里的快捷应用和辅助工具。");
        var windows = Page("windows", "Windows & previews", "窗口与预览", "Control visibility, resource use and ignored apps.", "管理隐藏行为、预览刷新和忽略的应用。");
        var shortcuts = Page("shortcuts", "Keyboard", "键盘快捷键", "Make common actions easy to reach.", "让常用操作触手可及。");
        var updates = Page("updates", "Updates & about", "更新与关于", "Check releases. You decide when to install.", "了解新版本，由你决定何时下载和安装。");
        var ai = Page("ai", "AI usage", "AI 额度", "Optional connections. Keys stay encrypted on this PC.", "按需连接账户，密钥仅在本机加密保存。");

        var layout = Section(general, "Your workspace", "你的工作区");
        _focusEnhancedMode = Toggle("Focus enhanced mode", "Focus 增强模式", Draft.StageMode == StageMode.Focus);
        Add(layout, _focusEnhancedMode);
        Add(layout, Note("Reserve space for the dock. Full-screen apps can still use the whole screen.", "为卡片栏预留空间；全屏应用仍可覆盖整个屏幕。"));
        _sidebarEdge = Choice(new[] { "Left", "Top", "Right", "Bottom" }, (int)Draft.SidebarEdge);
        Add(layout, SettingRow("Dock edge", "停靠位置", _sidebarEdge));
        _cardSizeSlider = new TrackBar { Minimum = 55, Maximum = 125, TickStyle = TickStyle.None, SmallChange = 1, LargeChange = 5, AutoSize = false, Height = 38, Dock = DockStyle.Fill, Value = Math.Clamp((int)Math.Round(Draft.CardScale * 100), 55, 125), AccessibleName = T("Card size", "卡片大小") };
        _cardSizeValue = new Label { Width = 58, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight };
        var size = new Panel { Height = 42 }; size.Controls.Add(_cardSizeSlider); size.Controls.Add(_cardSizeValue);
        Add(layout, SettingRow("Card size", "卡片大小", size));
        _sidebarVerticalOffset = Number(-400, 400, Draft.SidebarVerticalOffset, 10);
        Add(layout, SettingRow("Position offset", "排列偏移", _sidebarVerticalOffset));
        Add(layout, Note("Top/bottom: horizontal offset. Left/right: vertical offset.", "顶部／底部为左右偏移，左侧／右侧为上下偏移。"));
        _cardSizeSlider.ValueChanged += (_, _) => UpdateCardSizeLabel();

        var launches = Section(cards, "Launch apps", "应用快捷启动");
        _showExplorerButton = Toggle("File Explorer shortcut", "资源管理器快捷按钮", Draft.ShowExplorerButton); Add(launches, _showExplorerButton);
        _quickLaunchButton = ActionButton("Choose apps…", "选择应用…", (_, _) =>
        {
            using var picker = new QuickLaunchSettingsForm(_quickLaunchApps, Draft.UiLanguage);
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            _quickLaunchApps = picker.GetSelection(); UpdateQuickLaunchSummary();
        });
        _quickLaunchSummary = Note("", "");
        Add(launches, _quickLaunchButton); Add(launches, _quickLaunchSummary);
        var extras = Section(cards, "Additional cards", "辅助卡片");
        _showExpandedPinButton = Toggle("FIX button for expanded groups (left/right)", "多窗口展开后的 FIX 按钮（左右模式）", Draft.ShowExpandedPinButton);
        _showNotificationAreaCard = Toggle("Windows hidden tray icons", "Windows 隐藏图标", Draft.ShowNotificationAreaCard);
        _showDesktopButton = Toggle("Minimize / restore all windows", "一键最小化／恢复窗口", Draft.ShowDesktopButton);
        _showDesktopIconsButton = Toggle("Show / hide desktop icons", "显示／隐藏桌面图标", Draft.ShowDesktopIconsButton);
        foreach (var toggle in new[] { _showExpandedPinButton, _showNotificationAreaCard, _showDesktopButton, _showDesktopIconsButton }) Add(extras, toggle);

        var aiMain = Section(ai, "AI usage card", "AI 额度卡片");
        _aiUsageEnabled = Toggle("Show AI usage card", "显示 AI 额度卡片", Draft.ShowAiUsageCard && Draft.AiUsageEnabled); Add(aiMain, _aiUsageEnabled);
        Add(aiMain, Note("Turning this off stops quota queries. Provider choices and keys are kept.", "关闭后停止额度查询，保留平台选择和已保存的密钥。"));
        var local = Section(ai, "Local accounts", "本机账户");
        _codexEnabled = Toggle("Codex", "Codex", Draft.AiUsageCodexEnabled); Add(local, _codexEnabled);
        _codexPath = Input(Draft.CodexCliPath); Add(local, _codexPath);
        var browse = ActionButton("Browse CLI…", "选择 CLI…", (_, _) => BrowseCodexCli());
        var testCodex = ActionButton("Test connection", "测试连接", null); testCodex.Click += async (_, _) => await TestCodexAsync(testCodex);
        Add(local, ButtonRow(browse, testCodex));
        _geminiEnabled = Toggle("Gemini · web plan", "Gemini · 网页订阅", Draft.AiUsageGeminiEnabled); Add(local, _geminiEnabled);
        _antigravityEnabled = Toggle("Antigravity · local client", "Antigravity · 本机客户端", Draft.AiUsageAntigravityEnabled); Add(local, _antigravityEnabled);
        var deep = Section(ai, "DeepSeek", "DeepSeek");
        _deepSeekEnabled = Toggle("Enable DeepSeek", "启用 DeepSeek", Draft.AiUsageDeepSeekEnabled); Add(deep, _deepSeekEnabled);
        _deepSeekKey = Input(""); _deepSeekKey.UseSystemPasswordChar = true; Add(deep, _deepSeekKey);
        _deepSeekKey.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(_deepSeekKey.Text)) _clearDeepSeekKey = false; };
        var clearDeep = ActionButton("Clear saved key", "清除已保存密钥", (_, _) => { _deepSeekKey.Clear(); _clearDeepSeekKey = true; });
        var testDeep = ActionButton("Test connection", "测试连接", null); testDeep.Click += async (_, _) => await TestApiAsync("deepseek", _deepSeekKey, testDeep);
        Add(deep, ButtonRow(testDeep, clearDeep));
        var kimi = Section(ai, "Kimi", "Kimi");
        _kimiEnabled = Toggle("Enable Kimi", "启用 Kimi", Draft.AiUsageKimiEnabled); Add(kimi, _kimiEnabled);
        _kimiRegion = Choice(new[] { "China", "International" }, Draft.AiUsageKimiInternational ? 1 : 0); Add(kimi, SettingRow("Service region", "服务区域", _kimiRegion));
        _kimiKey = Input(""); _kimiKey.UseSystemPasswordChar = true; Add(kimi, _kimiKey);
        _kimiKey.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(_kimiKey.Text)) _clearKimiKey = false; };
        var clearKimi = ActionButton("Clear saved key", "清除已保存密钥", (_, _) => { _kimiKey.Clear(); _clearKimiKey = true; });
        var testKimi = ActionButton("Test connection", "测试连接", null); testKimi.Click += async (_, _) => await TestApiAsync("kimi", _kimiKey, testKimi);
        Add(kimi, ButtonRow(testKimi, clearKimi));
        Add(kimi, Note("Leave keys blank to keep existing ones. Clear takes effect only after saving.", "密钥留空即保留原值；清除操作在保存设置后生效。"));

        var visibility = Section(windows, "Visibility & performance", "显示与性能");
        _idleAutoHideEnabled = Toggle("Hide after pointer inactivity", "鼠标无活动后隐藏", Draft.IdleAutoHideEnabled); Add(visibility, _idleAutoHideEnabled);
        _idleSeconds = Number(15, 600, Draft.IdleAutoHideSeconds, 15); Add(visibility, SettingRow("Idle delay · seconds", "空闲延迟 · 秒", _idleSeconds));
        Add(visibility, Note("Focus keeps the dock visible; idle hiding applies to Coexist mode.", "Focus 模式保持常驻；空闲自动隐藏仅在共存模式生效。"));
        _previewRefreshMinutes = Number(1, 60, Draft.PreviewRefreshMinutes); Add(visibility, SettingRow("Preview refresh · minutes", "预览刷新 · 分钟", _previewRefreshMinutes));
        _pausePreviewRefreshWhenHidden = Toggle("Pause preview refresh while hidden", "卡片栏隐藏时暂停预览刷新", Draft.PausePreviewRefreshWhenHidden); Add(visibility, _pausePreviewRefreshWhenHidden);
        _lowMemoryRendering = Toggle("Low-memory rendering · restart required", "低内存渲染 · 需重启程序", Draft.LowMemoryRendering); Add(visibility, _lowMemoryRendering);
        _idleAutoHideEnabled.CheckedChanged += (_, _) => { if (_uiReady) UpdateBehaviorControlState(); };
        _focusEnhancedMode.CheckedChanged += (_, _) => { if (_uiReady) UpdateBehaviorControlState(); };
        var ignored = Section(windows, "Ignored applications", "忽略的应用");
        Add(ignored, Note("Checked apps only lose their cards. Uncheck to bring them back.", "勾选只会隐藏卡片，不会影响应用；取消勾选即可恢复。"));
        _ignoredApplications = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Height = 190, BorderStyle = BorderStyle.None, AccessibleName = T("Ignored applications", "忽略的应用") };
        var known = choices.Select(c => c.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in choices) _ignoredApplications.Items.Add(choice, Draft.IgnoredProcesses.Contains(choice.ProcessName, StringComparer.OrdinalIgnoreCase));
        Add(ignored, _ignoredApplications);
        Add(ignored, Note("Additional process names · one per line (optional)", "额外进程名称 · 每行一个（可选）"));
        _ignoredProcesses = Input(string.Join(Environment.NewLine, Draft.IgnoredProcesses.Where(name => !known.Contains(name))));
        _ignoredProcesses.Multiline = true; _ignoredProcesses.Height = 92; _ignoredProcesses.ScrollBars = ScrollBars.Vertical; Add(ignored, _ignoredProcesses);

        var keys = Section(shortcuts, "Global shortcuts", "全局快捷键");
        _hotkeysEnabled = Toggle("Enable global shortcuts", "启用全局快捷键", Draft.HotkeysEnabled); Add(keys, _hotkeysEnabled);
        _toggleSidebarHotkey = Input(Draft.ToggleSidebarHotkey); Add(keys, SettingRow("Show / hide dock", "显示／隐藏卡片栏", _toggleSidebarHotkey));
        _previousStageHotkey = Input(Draft.PreviousStageHotkey); Add(keys, SettingRow("Previous card", "上一张卡片", _previousStageHotkey));
        _nextStageHotkey = Input(Draft.NextStageHotkey); Add(keys, SettingRow("Next card", "下一张卡片", _nextStageHotkey));
        Add(keys, Note("For example: Win+Alt+S. Save validates the shortcuts before applying.", "示例：Win+Alt+S。保存前会检查快捷键格式。"));
        _hotkeysEnabled.CheckedChanged += (_, _) => { foreach (var input in new[] { _toggleSidebarHotkey, _previousStageHotkey, _nextStageHotkey }) input.Enabled = _hotkeysEnabled.Checked; };
        foreach (var input in new[] { _toggleSidebarHotkey, _previousStageHotkey, _nextStageHotkey }) input.Enabled = _hotkeysEnabled.Checked;

        var reading = Section(preferences, "Language & reading", "语言与阅读");
        _interfaceLanguage = Choice(new[] { "English", "简体中文" }, Draft.UiLanguage == UiLanguage.SimplifiedChinese ? 1 : 0);
        Add(reading, SettingRow("Interface language", "界面语言", _interfaceLanguage));
        _settingsTheme = Choice(new[] { "System", "Light", "Dark" }, (int)Draft.SettingsTheme); Add(reading, SettingRow("Settings appearance", "设置界面主题", _settingsTheme));
        _settingsTextSize = Choice(new[] { "Standard", "Large" }, Draft.SettingsTextSize >= 12 ? 1 : 0); Add(reading, SettingRow("Settings text", "设置文字大小", _settingsTextSize));
        Add(reading, Note("Preview theme and text size here. These options do not change your dock layout.", "主题与字号在本窗口即时预览，不会改变卡片栏布局。"));
        var habits = Section(preferences, "Startup & habits", "启动与使用习惯");
        _startWithWindows = Toggle("Start with Windows", "开机自动启动", Draft.StartWithWindows); Add(habits, _startWithWindows);
        _rememberSettingsPage = Toggle("Remember the last viewed module", "记住上次查看的设置模块", Draft.RememberSettingsPage); Add(habits, _rememberSettingsPage);
        _showWhatsNew = Toggle("Show what's new after an upgrade", "升级后展示新功能介绍", Draft.ShowWhatsNewOnStartup); Add(habits, _showWhatsNew);
        _animationsEnabled = Toggle("Use dock animations", "启用卡片栏动画", Draft.AnimationsEnabled); Add(habits, _animationsEnabled);
        _interfaceLanguage.SelectedIndexChanged += (_, _) => { if (_uiReady && !_applyingLanguage) { Draft.UiLanguage = _interfaceLanguage.SelectedIndex == 1 ? UiLanguage.SimplifiedChinese : UiLanguage.English; ApplyLanguage(); } };
        _settingsTheme.SelectedIndexChanged += (_, _) => { if (_uiReady && !_applyingLanguage) ApplyPreferences(); };
        _settingsTextSize.SelectedIndexChanged += (_, _) => { if (_uiReady && !_applyingLanguage) ApplyPreferences(); };

        var release = Section(updates, "Keep up to date", "获取更新");
        Add(release, new Label { Text = AppVersion.Label, AutoSize = true, Font = OwnFont(14, FontStyle.Bold) });
        _automaticUpdates = Toggle("Automatically check for updates", "自动检查更新", Draft.AutomaticUpdateChecks); Add(release, _automaticUpdates);
        Add(release, Note("Checks the official GitHub release once a day, starting 45 seconds after launch. Only notifies you; downloads and installation need your confirmation. Full-screen notifications are delayed.", "启动 45 秒后检查，此后每天最多检查一次官方 GitHub 正式版。只提示新版，下载与安装需你确认；全屏时延后提示。"));
        Add(release, ActionButton("Check now", "立即检查更新", (_, _) => OpenUpdateDialog(), true));
        Add(release, ActionButton("What's new in this version", "查看本次更新", (_, _) =>
        {
            var news = new WhatsNewForm(Draft.UiLanguage, recordSeen: false);
            news.OpenSettings += (_, _) => SelectPage("preferences"); news.Show(this);
        }));
        Add(release, Note("Original project: Andreas Wäscher · Lai modifications: Frank Lai\nNo account credentials are sent when checking updates.", "原作：Andreas Wäscher · Lai 修改版：Frank Lai\n检查更新不发送账户凭据。"));
        _uiReady = true;
        Resize += (_, _) => UpdateResponsiveLayout();
        UpdatePageHeading();
        UpdateResponsiveLayout();
        _selectedPage = Draft.RememberSettingsPage && _pages.ContainsKey(Draft.LastSettingsPage) ? Draft.LastSettingsPage : "general";
        ResumeLayout(true);
    }

    private FlowLayoutPanel Page(string id, string en, string zh, string hintEn, string hintZh)
    {
        var stack = new SettingsPageStack { Name = id, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 20) };
        var number = (_pages.Count + 1).ToString("00");
        Add(stack, Localize(new SmoothLabel { AutoSize = true, Font = OwnFont(17, FontStyle.Bold), Margin = new Padding(2, 6, 0, 6) }, number + "   " + en, number + "   " + zh));
        Add(stack, Note(hintEn, hintZh));
        stack.SizeChanged += (_, _) => FitChildren(stack);
        _pages.Add(id, (stack, en, zh, hintEn, hintZh));
        _allModules.Controls.Add(stack);
        return stack;
    }
    private FlowLayoutPanel Section(FlowLayoutPanel page, string en, string zh)
    {
        var section = new SettingsSection { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(22, 20, 22, 14), Margin = new Padding(0, 0, 0, 14), Tag = "section" };
        section.SizeChanged += (_, _) => FitChildren(section);
        Add(section, Localize(new SmoothLabel { AutoSize = true, Font = OwnFont(12, FontStyle.Bold), Margin = new Padding(0, 0, 0, 14) }, en, zh));
        Add(page, section); return section;
    }

    private static void FitChildren(FlowLayoutPanel panel)
    {
        var width = Math.Max(180, panel.ClientSize.Width - panel.Padding.Horizontal);
        foreach (Control child in panel.Controls)
        {
            var available = Math.Max(120, width - child.Margin.Horizontal);
            if (child is Label { AutoSize: true } or FlowLayoutPanel or TableLayoutPanel) child.MaximumSize = new Size(available, 0);
            if (child is FlowLayoutPanel or TableLayoutPanel) child.MinimumSize = new Size(available, child.MinimumSize.Height);
            child.Width = available;
        }
    }
    private static void Add(FlowLayoutPanel parent, Control child) { parent.Controls.Add(child); FitChildren(parent); }
    private Label Note(string en, string zh) => Localize(new SmoothLabel { AutoSize = true, Tag = "muted", Margin = new Padding(0, 0, 0, 12) }, en, zh);
    private CheckBox Toggle(string en, string zh, bool value) => Localize(new SmoothToggle { Checked = value, AutoSize = false, Height = 48, Margin = new Padding(0, 0, 0, 3), Cursor = Cursors.Hand }, en, zh);
    private static TextBox Input(string text) => new() { Text = text, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 0, 12) };
    private static NumericUpDown Number(int min, int max, int value, int step = 1) => new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Increment = step, TextAlign = HorizontalAlignment.Right, BorderStyle = BorderStyle.FixedSingle };
    private static ComboBox Choice(string[] options, int selected)
    {
        var combo = new SmoothChoice { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, IntegralHeight = false, DropDownHeight = 220 };
        combo.Items.AddRange(options.Cast<object>().ToArray()); combo.SelectedIndex = Math.Clamp(selected, 0, options.Length - 1); return combo;
    }
    private Control SettingRow(string en, string zh, Control editor)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, MinimumSize = new Size(0, 52), Margin = new Padding(0, 2, 0, 5) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        var label = Localize(new SmoothLabel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) }, en, zh);
        editor.Anchor = AnchorStyles.Left | AnchorStyles.Right; editor.Margin = new Padding(0, 6, 0, 6);
        _localized.Add(() => editor.AccessibleName = T(en, zh));
        row.Controls.Add(label, 0, 0); row.Controls.Add(editor, 1, 0);
        row.SizeChanged += (_, _) => label.MaximumSize = new Size(Math.Max(80, (int)(row.Width * .48) - D(12)), 0);
        return row;
    }
    private Button ActionButton(string en, string zh, EventHandler? action, bool primary = false)
    {
        var button = Localize(new SmoothButton { Height = 44, FlatStyle = FlatStyle.Flat, Tag = primary ? "primary" : "button", Margin = new Padding(0, 0, 8, 10), Cursor = Cursors.Hand }, en, zh);
        button.FlatAppearance.BorderSize = 1; if (action is not null) button.Click += action; return button;
    }
    private static Control ButtonRow(params Button[] buttons)
    {
        var row = new TableLayoutPanel { ColumnCount = buttons.Length, RowCount = 1, AutoSize = true, Margin = new Padding(0) };
        for (var i = 0; i < buttons.Length; i++) { row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length)); buttons[i].Dock = DockStyle.Fill; row.Controls.Add(buttons[i], i, 0); }
        return row;
    }
    private void UpdateCombo(ComboBox combo, string[] en, string[] zh)
    {
        var index = combo.SelectedIndex; combo.Items.Clear(); combo.Items.AddRange((Draft.UiLanguage == UiLanguage.SimplifiedChinese ? zh : en).Cast<object>().ToArray()); combo.SelectedIndex = Math.Clamp(index, 0, en.Length - 1);
    }
    // Compatibility for deep links: navigate within the one continuous document.
    // No module is hidden, collapsed or replaced.
    internal void SelectPage(string id)
    {
        StopNavigation();
        if (!_pages.ContainsKey(id)) id = "general";
        _selectedPage = id;
        if (!_uiReady) return;
        UpdateResponsiveLayout();
        _pageHost.AutoScrollPosition = new Point(0, _pages[id].Page.Top);
        UpdateNavigation();
    }
    private void RememberVisibleModule()
    {
        if (!_uiReady) return;
        var y = -_pageHost.AutoScrollPosition.Y + D(28);
        _selectedPage = _moduleOrder.LastOrDefault(id => _pages[id].Page.Top <= y) ?? "general";
        UpdateNavigation();
    }
    private void FindSetting()
    {
        var query = _search.Text.Trim();
        if (query.Length == 0) return;
        foreach (var id in _moduleOrder)
        {
            var page = _pages[id];
            var match = new[] { page.En, page.Zh }.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            var control = Descendants(page.Page).FirstOrDefault(c =>
                c is Label or CheckBox or Button && c.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            if (!match && control is null) continue;
            SelectPage(id);
            if (control is not null)
            {
                var y = _allModules.PointToClient(control.PointToScreen(Point.Empty)).Y;
                _pageHost.AutoScrollPosition = new Point(0, Math.Max(0, y - D(24)));
                if (control.CanSelect) control.Focus();
            }
            return;
        }
        _search.AccessibleDescription = T("No matching setting.", "没有找到匹配的设置。");
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private void UpdatePageHeading()
    {
        _pageTitle.Text = T("Settings", "设置");
        _pageHint.Text = T("All modules stay open. Use the left rail to jump, or scroll to explore.", "所有模块保持展开，点击左侧彩色导航快速定位，也可直接滚动浏览。");
        if (_uiReady) UpdateResponsiveLayout();
    }
    private Font OwnFont(float size, FontStyle style = FontStyle.Regular)
    {
        // Use a local CJK sans-serif family when available; no fonts are installed or bundled.
        var font = new Font(SettingsFontFamily, size, style, GraphicsUnit.Point); _ownedFonts.Add(font); return font;
    }
    private static readonly string SettingsFontFamily = ChooseSettingsFont();
    private static string ChooseSettingsFont()
    {
        using var installed = new InstalledFontCollection();
        var families = installed.Families;
        var names = families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families) family.Dispose();
        return names.Contains("Noto Sans SC") ? "Noto Sans SC" : "Microsoft YaHei UI";
    }
    private void ApplyPreferences()
    {
        if (!_uiReady) return;
        _dark = _settingsTheme.SelectedIndex == 2 || (_settingsTheme.SelectedIndex == 0 && SystemUsesDarkTheme());
        var fontSize = _settingsTextSize.SelectedIndex == 1 ? 12f : 11f;
        if (_appliedTextSize != fontSize) { _appliedTextSize = fontSize; Font = OwnFont(fontSize); }
        ApplyColors(this); UpdateNavigation(); UpdateResponsiveLayout(); Invalidate(true);
    }
    private static bool SystemUsesDarkTheme()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
        catch { return false; }
    }
    private void ApplyColors(Control root)
    {
        var background = _dark ? Color.FromArgb(22, 27, 35) : Color.FromArgb(244, 247, 251);
        var text = _dark ? Color.FromArgb(234, 240, 249) : Color.FromArgb(30, 44, 64);
        root.BackColor = root is TextBoxBase or ListBox or ComboBox or NumericUpDown || root.Tag is "section" or "footer" ? Surface : root.Parent?.BackColor ?? background;
        if (root == this) root.BackColor = background;
        root.ForeColor = root.Tag is "muted" ? Muted : text;
        if (root is Button button)
        {
            button.BackColor = button.Tag is "primary" ? (_dark ? Color.FromArgb(63, 125, 221) : Color.FromArgb(40, 107, 209)) : Surface;
            button.ForeColor = button.Tag is "primary" ? Color.White : text;
            button.FlatAppearance.BorderColor = _dark ? Color.FromArgb(70, 82, 99) : Color.FromArgb(210, 220, 232);
            button.FlatAppearance.MouseOverBackColor = button.Tag is "primary" ? Color.FromArgb(54, 123, 226) : (_dark ? Color.FromArgb(52, 66, 85) : Color.FromArgb(230, 239, 251));
        }
        if (root is SettingsSection section) section.BorderColor = _dark ? Color.FromArgb(55, 64, 78) : Color.FromArgb(222, 228, 237);
        if (root is SmoothToggle toggle) toggle.AccentColor = _dark ? Color.FromArgb(101, 169, 255) : Color.FromArgb(45, 111, 212);
        foreach (Control child in root.Controls) ApplyColors(child);
    }
    private void OpenUpdateDialog() { using var dialog = new UpdateForm(Draft.UiLanguage); dialog.ShowDialog(this); }

    private void UpdateResponsiveLayout()
    {
        if (!_uiReady || _fittingModules) return;
        _fittingModules = true;
        try
        {
            if (_search.Parent is { Parent: { } titleRow } searchHost)
                searchHost.Width = Math.Min(D(252), Math.Max(D(140), titleRow.ClientSize.Width * 2 / 5));
            if (_pageHint.Parent is { } heading)
            {
                var hintHeight = TextRenderer.MeasureText(_pageHint.Text, _pageHint.Font,
                    new Size(Math.Max(D(200), heading.ClientSize.Width), int.MaxValue), TextFormatFlags.WordBreak).Height;
                heading.Height = D(82) + Math.Max(D(28), hintHeight + D(10));
            }
            _allModules.Width = Math.Max(D(200), _pageHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - D(8));
            FitChildren(_allModules);
            foreach (var page in _pages.Values) FitChildren((FlowLayoutPanel)page.Page);
        }
        finally { _fittingModules = false; }
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); ConstrainToWorkingArea(); SelectPage(_selectedPage);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e); ConstrainToWorkingArea(); UpdateResponsiveLayout();
    }
    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        if (_uiReady && _settingsTheme.SelectedIndex == 0) ApplyPreferences();
    }
    private void ConstrainToWorkingArea()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = Math.Max(480, area.Width - 32); var height = Math.Max(400, area.Height - 32);
        MinimumSize = new Size(Math.Min(D(760), width), Math.Min(D(560), height));
        Size = new Size(Math.Min(Width, width), Math.Min(Height, height));
        if (Location.X > -10000 && Location.Y > -10000)
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); path.CloseFigure();
        return path;
    }
    private sealed class SettingsSection : FlowLayoutPanel
    {
        public Color BorderColor { get; set; } = Color.LightGray;
        public SettingsSection() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new RectangleF(.5f, .5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 12f * DeviceDpi / 96);
            using var fill = new SolidBrush(BackColor); using var pen = new Pen(BorderColor);
            e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
        }
    }
    private sealed class SmoothLabel : Label
    {
        public SmoothLabel() { UseCompatibleTextRendering = false; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; base.OnPaint(e); }
    }
    private sealed class SmoothChoice : ComboBox
    {
        public SmoothChoice() { DrawMode = DrawMode.OwnerDrawFixed; UpdateItemHeight(); }
        private void UpdateItemHeight() { ItemHeight = Math.Max(28 * DeviceDpi / 96, Font.Height + 10 * DeviceDpi / 96); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateItemHeight(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateItemHeight(); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using var fill = new SolidBrush(selected ? Color.FromArgb(46, 107, 199) : BackColor);
            e.Graphics.FillRectangle(fill, e.Bounds);
            if (e.Index >= 0 && e.Index < Items.Count)
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                    Rectangle.Inflate(e.Bounds, -8 * DeviceDpi / 96, 0),
                    !Enabled ? SystemColors.GrayText : selected ? Color.White : ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && (e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
    }
    private sealed class SmoothToggle : CheckBox
    {
        public Color AccentColor { get; set; } = Color.RoyalBlue;
        private bool _hovered;
        public SmoothToggle() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); AccessibleRole = AccessibleRole.CheckButton; }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f; var w = 42 * scale; var h = 24 * scale;
            var rect = new RectangleF(Width - w - 3 * scale, (Height - h) / 2, w, h);
            using var track = Rounded(rect, h / 2);
            var off = BackColor.GetBrightness() < .3 ? Color.FromArgb(83, 94, 111) : Color.FromArgb(187, 197, 210);
            using var fill = new SolidBrush(!Enabled ? ControlPaint.Light(off) : Checked ? AccentColor : _hovered ? ControlPaint.Dark(off, .08f) : off);
            g.FillPath(fill, track);
            var knob = h - 6 * scale;
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, Checked ? rect.Right - knob - 3 * scale : rect.Left + 3 * scale, rect.Top + 3 * scale, knob, knob);
            var textRect = new Rectangle(0, 0, Math.Max(1, (int)rect.Left - (int)(16 * scale)), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }
    private sealed class SmoothButton : Button
    {
        private bool _hovered, _pressed;
        public SmoothButton() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = new RectangleF(.5f, .5f, Width - 1, Height - 1); var radius = Math.Min(9f * DeviceDpi / 96, Height / 4f); var d = radius * 2;
            using var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); path.CloseFigure();
            var color = _pressed ? ControlPaint.Dark(BackColor, .06f) : _hovered ? FlatAppearance.MouseOverBackColor : BackColor;
            using var fill = new SolidBrush(color); g.FillPath(fill, path);
            if (FlatAppearance.BorderSize > 0) { using var pen = new Pen(FlatAppearance.BorderColor); g.DrawPath(pen, path); }
            using var brush = new SolidBrush(Enabled ? ForeColor : SystemColors.GrayText);
            using var format = new StringFormat { Alignment = TextAlign == ContentAlignment.MiddleLeft ? StringAlignment.Near : StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            var text = new RectangleF(Padding.Left + 5, 3, Width - Padding.Horizontal - 10, Height - 6);
            g.DrawString(Text, Font, brush, text, format);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -5, -5), ForeColor, BackColor);
        }
    }

    // Width follows the viewport; only height follows the content. FlowLayoutPanel's
    // two-axis AutoSize otherwise retains a wide preferred size after a DPI/size change.
    private sealed class SettingsPageStack : FlowLayoutPanel
    {
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var height = Controls.Count == 0 ? 0 : Controls.Cast<Control>().Max(c => c.Bottom + c.Margin.Bottom) + Padding.Bottom;
            if (Height != height) Height = height;
        }
    }
}
