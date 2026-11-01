using System.Diagnostics;
using StageManager.Card3DPrototype.Calendar;

namespace StageManager.Card3DPrototype;

internal sealed partial class SettingsForm
{
    private readonly CalendarService? _calendarService;
    private CheckBox _calendarEnabled = null!;
    private CheckBox _calendarViaCodex = null!;
    private FlowLayoutPanel _calendarDirectSetup = null!;
    private TextBox _calendarClientId = null!;
    private CheckedListBox _calendarChoices = null!;
    private Label _calendarStatus = null!;
    private Button _calendarConnect = null!, _calendarDisconnect = null!, _calendarRefresh = null!;
    private bool _calendarListLoaded, _calendarBusy, _calendarClosing;
    private string _calendarListClientId = "";
    private string? _calendarActionMessage;
    private sealed record CalendarChoice(CalendarInfo Calendar)
    {
        public override string ToString() => Calendar.Name;
    }

    private void BuildCalendarSettings(FlowLayoutPanel page)
    {
        var display = Section(page, "Outlook calendar card", "Outlook 日历卡片");
        _calendarEnabled = Toggle("Show calendar card", "显示日历卡片", Draft.ShowCalendarCard);
        Add(display, _calendarEnabled);
        _calendarViaCodex = Toggle("Use Codex Outlook plugin · manual refresh", "通过 Codex 的 Outlook 插件读取 · 手动刷新", Draft.CalendarUseCodex);
        Add(display, _calendarViaCodex);
        Add(display, Note("Codex mode needs no Client ID. Only Refresh calls the connected Outlook plugin. Calendar content is cached with Windows user encryption; no model turn or scheduled task is started.",
            "Codex 模式无需注册应用或填写 Client ID。仅点击刷新才读取已连接的 Outlook 插件，日程以 Windows 当前用户加密缓存；不启动模型回复或定时任务。"));
        Add(display, Note("Off by default. Read-only personal Microsoft calendars; no mail, contacts, To Do or calendar edits. Turning off retains your login.",
            "默认关闭。仅读取个人微软账号的日历，不读取邮件、联系人或 To Do，也不修改日程。关闭后保留登录。"));
        Add(display, Note("Completion ticks are saved only on this PC for up to 90 days. They do not change Outlook events or create To Do tasks.",
            "完成勾选仅在本机保存，保留最多 90 天；不会修改 Outlook 日程，也不会创建 To Do 任务。"));
        var connection = Section(page, "Microsoft connection", "微软账号连接");
        _calendarDirectSetup = Section(page, "Direct Microsoft connection (optional)", "微软直连配置（可选）");
        Add(_calendarDirectSetup, Note("First-time setup: the maintainer must register a Microsoft Entra desktop application supporting personal accounts, with a localhost redirect and delegated Calendars.Read permission. Enter its public Application (client) ID below — not a password or client secret.",
            "首次使用需要维护者注册支持个人账号的 Microsoft Entra 桌面应用，配置 localhost 回调及 Calendars.Read 委托权限。下方填写公开的应用程序（客户端）ID，不是密码，也不是客户端密钥。不知道 ID 时，请先按说明完成注册。"));
        _calendarClientId = Input(Draft.OutlookCalendarClientId ?? "");
        _calendarClientId.MaxLength = 64;
        Add(_calendarDirectSetup, SettingRow("Application (client) ID", "应用程序（客户端）ID", _calendarClientId));
        _localized.Add(() => _calendarClientId.PlaceholderText = T("Public GUID · setup required if empty", "公开 GUID · 未填写时需要先注册应用"));
        Add(_calendarDirectSetup, ActionButton("Registration guide", "微软官方注册说明", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app") { UseShellExecute = true }); }
            catch { _calendarActionMessage = T("Could not open the registration guide.", "无法打开注册说明，请检查默认浏览器。"); UpdateCalendarStatus(); }
        }));
        _calendarConnect = ActionButton("Connect account", "连接账号", async (_, _) => await RunCalendarActionAsync("connect"));
        _calendarDisconnect = ActionButton("Disconnect", "断开账号", async (_, _) => await RunCalendarActionAsync("disconnect"));
        _calendarRefresh = ActionButton("Refresh now", "立即刷新", async (_, _) => await RunCalendarActionAsync("refresh"));
        Add(connection, ButtonRow(_calendarConnect, _calendarDisconnect));
        Add(connection, _calendarRefresh);
        _calendarStatus = Note("", "");
        Add(connection, _calendarStatus);
        Add(connection, Note("Save applies these choices to the dock. In Codex mode, Disconnect clears this app's calendar cache, not your Codex connection. Direct Microsoft mode retains its own encrypted login.",
            "保存后将选项应用于卡片栏。Codex 模式的断开只清除本软件日程缓存，不会断开 Codex 中的账号；微软直连模式使用独立加密登录。"));
        var selection = Section(page, "Your calendars", "我的日历");
        _calendarChoices = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Height = 156, BorderStyle = BorderStyle.None };
        _localized.Add(() => _calendarChoices.AccessibleName = T("Select your own calendars", "选择自己拥有的日历"));
        Add(selection, _calendarChoices);
        Add(selection, Note("Connect or refresh to load calendars owned by this account. No selection uses the primary calendar. Shared/delegated calendars are not supported. Existing choices are retained until a list is loaded.",
            "连接或刷新后加载该账号自己拥有的日历。不勾选时使用主日历，暂不支持共享或委派日历。列表尚未加载时保留原有选择。"));
        _calendarClientId.TextChanged += (_, _) => { _calendarActionMessage = null; UpdateCalendarStatus(); };
        _calendarViaCodex.CheckedChanged += (_, _) => { _calendarActionMessage = null; UpdateCalendarStatus(); };
        UpdateCalendarStatus();
    }

    public void ScrollToCalendar() => SelectPage("calendar");

    private List<string> CalendarSelection() => _calendarListLoaded &&
        string.Equals(_calendarListClientId, _calendarClientId.Text.Trim(), StringComparison.OrdinalIgnoreCase)
        ? _calendarChoices.CheckedItems.Cast<CalendarChoice>().Select(c => c.Calendar.Id).ToList()
        : (Draft.OutlookCalendarIds ?? new()).ToList();

    private bool ValidCalendarId(out string id)
    {
        var valid = Guid.TryParse(_calendarClientId.Text.Trim(), out var guid) && guid != Guid.Empty;
        id = valid ? guid.ToString("D") : "";
        return valid;
    }

    private bool SaveCalendarDraft()
    {
        if (_calendarBusy)
        {
            _calendarActionMessage = T("Wait for the calendar operation to finish, or cancel this dialog.", "请等待日历操作完成，或取消设置窗口。");
            UpdateCalendarStatus(); ScrollToCalendar(); return false;
        }
        if (!_calendarViaCodex.Checked && !string.IsNullOrWhiteSpace(_calendarClientId.Text) && !ValidCalendarId(out _))
        {
            _calendarActionMessage = T("The Client ID must be a valid, non-empty GUID; do not enter a secret.", "客户端 ID 必须是有效的非空 GUID，请不要输入密钥。");
            UpdateCalendarStatus(); ScrollToCalendar(); _calendarClientId.Focus(); return false;
        }
        Draft.ShowCalendarCard = _calendarEnabled.Checked;
        Draft.CalendarUseCodex = _calendarViaCodex.Checked;
        Draft.OutlookCalendarClientId = ValidCalendarId(out var id) ? id : "";
        Draft.OutlookCalendarIds = CalendarSelection();
        return true;
    }

    private async Task RunCalendarActionAsync(string action)
    {
        if (_calendarBusy || _calendarClosing || _calendarService is null) return;
        if (action != "disconnect" && !_calendarViaCodex.Checked && !ValidCalendarId(out _))
        {
            _calendarActionMessage = T("Setup required: register an app and enter its public Client ID first.", "需要先注册应用并填写公开的客户端 ID，才能连接或刷新。");
            UpdateCalendarStatus(); return;
        }
        var token = _connectionTests.Token;
        _calendarBusy = true; _calendarActionMessage = null; UpdateCalendarStatus();
        try
        {
            if (action == "disconnect")
            {
                _calendarService.Configure(true, _calendarViaCodex.Checked ? "codex:" + Draft.CodexCliPath : Draft.OutlookCalendarClientId, CalendarSelection());
                await _calendarService.DisconnectAsync(token);
                if (_calendarClosing || IsDisposed || Disposing) return;
                if (_calendarService.Current.Status == CalendarStatus.Error)
                {
                    _calendarActionMessage = T("Could not fully disconnect. Retry before considering the saved login removed.", "断开未完全成功，请重试，暂不能确认已清除保存的登录。");
                    return;
                }
                Draft.OutlookCalendarIds = new();
                _calendarListLoaded = false; _calendarChoices.Items.Clear();
                _calendarActionMessage = T("Disconnected. This app's saved login and calendar selection were cleared.", "已断开，已清除本软件保存的登录及日历选择。");
            }
            else
            {
                var selected = CalendarSelection();
                ValidCalendarId(out var id);
                _calendarService.Configure(true, _calendarViaCodex.Checked ? "codex:" + Draft.CodexCliPath : id, selected);
                if (_calendarViaCodex.Checked) await _calendarService.GetMonthAsync(CalendarDates.Today, manual: true, token: token);
                else if (action == "connect") await _calendarService.ConnectAsync(token);
                else await _calendarService.RefreshAsync(manual: true, token: token);
                if (_calendarClosing || IsDisposed || Disposing) return;
                if (_calendarService.Account is not null && _calendarService.Calendars.Count > 0)
                {
                    _calendarChoices.Items.Clear();
                    foreach (var calendar in _calendarService.Calendars)
                        _calendarChoices.Items.Add(new CalendarChoice(calendar), selected.Contains(calendar.Id, StringComparer.Ordinal));
                    _calendarListLoaded = true; _calendarListClientId = _calendarClientId.Text.Trim();
                }
            }
        }
        catch (OperationCanceledException) { if (!_calendarClosing) _calendarActionMessage = T("Operation cancelled.", "操作已取消。"); }
        catch (Exception) { if (!_calendarClosing) _calendarActionMessage = T("Connection or refresh failed. Retry or reconnect; no credentials were logged.", "连接或刷新失败，请重试或重新连接；未记录任何凭据。"); }
        finally
        {
            _calendarBusy = false;
            if (!_calendarClosing && !IsDisposed && !Disposing) UpdateCalendarStatus();
        }
    }

    private void UpdateCalendarStatus()
    {
        if (_calendarStatus is null || _calendarClosing || IsDisposed) return;
        _calendarConnect.Enabled = !_calendarBusy && _calendarService is not null;
        _calendarDisconnect.Enabled = !_calendarBusy && _calendarService is not null;
        _calendarRefresh.Enabled = !_calendarBusy && _calendarService is not null;
        _calendarClientId.Enabled = !_calendarBusy;
        _calendarViaCodex.Enabled = !_calendarBusy;
        _calendarDirectSetup.Visible = !_calendarViaCodex.Checked;
        _calendarConnect.Visible = !_calendarViaCodex.Checked;
        _calendarChoices.Enabled = !_calendarBusy;
        var text = _calendarActionMessage;
        if (text is null)
        {
            text = _calendarBusy ? T("Connecting / refreshing…", "正在连接／刷新…") : !_calendarViaCodex.Checked && !ValidCalendarId(out _)
                ? T("Setup required · no valid Client ID. See the registration guide above.", "尚未配置 · 缺少有效的客户端 ID，请先查看上方注册说明。")
                : _calendarService is null ? T("Calendar service unavailable in this session.", "本次会话未连接日历服务。")
                : _calendarService.Current.Status switch
                {
                    CalendarStatus.Connected => T("Connected · read-only", "已连接 · 只读"),
                    CalendarStatus.Loading => T("Loading…", "正在加载…"),
                    CalendarStatus.Stale => T("Update failed / pending · showing previous data", "更新失败或等待刷新 · 保留上次数据"),
                    CalendarStatus.AuthRequired => T("Sign-in expired · reconnect", "登录已过期 · 请重新连接"),
                    CalendarStatus.Error => CalendarPresentation.ErrorText(_calendarService.Current.ErrorCode, Draft.UiLanguage),
                    CalendarStatus.Disabled => T("Calendar is off", "日历已关闭"),
                    CalendarStatus.NotConfigured => T("Application setup required", "需要配置应用注册"),
                    _ => T("Not connected · choose Connect account", "尚未连接 · 请点击连接账号")
                };
            if (_calendarService?.Current.LastSuccess is { } last)
                text += T(" · Last updated ", " · 最后更新 ") + last.ToLocalTime().ToString("g");
        }
        _calendarStatus.Text = text;
        _calendarStatus.AccessibleName = text;
    }
}
