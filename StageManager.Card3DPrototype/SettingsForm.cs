using StageManager.Services;
using StageManager.Settings;
using StageManager.Card3DPrototype.QuickLaunch;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Card3DPrototype.Updates;
using System.Security.Cryptography;

namespace StageManager.Card3DPrototype;

internal sealed partial class SettingsForm : Form
{
	private TrackBar _cardSizeSlider = null!;
	private Label _cardSizeValue = null!;
	private NumericUpDown _sidebarVerticalOffset = null!;
	private ComboBox _sidebarEdge = null!;
	private CheckBox _showExplorerButton = null!;
	private Button _quickLaunchButton = null!;
	private Label _quickLaunchSummary = null!;
	private List<QuickLaunchDefinition> _quickLaunchApps;
	private CheckBox _showExpandedPinButton = null!;
	private CheckBox _showNotificationAreaCard = null!;
	private CheckBox _showDesktopButton = null!;
	private CheckBox _showDesktopIconsButton = null!;
	private CheckBox _animationsEnabled = null!;
	private CheckBox _lowMemoryRendering = null!;
	private CheckBox _idleAutoHideEnabled = null!;
	private NumericUpDown _idleSeconds = null!;
	private CheckBox _focusEnhancedMode = null!;
	private NumericUpDown _previewRefreshMinutes = null!;
	private CheckBox _pausePreviewRefreshWhenHidden = null!;
	private CheckBox _startWithWindows = null!;
	private CheckBox _hotkeysEnabled = null!;
	private TextBox _toggleSidebarHotkey = null!;
	private TextBox _previousStageHotkey = null!;
	private TextBox _nextStageHotkey = null!;
	private CheckedListBox _ignoredApplications = null!;
	private TextBox _ignoredProcesses = null!;
	private ComboBox _interfaceLanguage = null!;
	private CheckBox _aiUsageEnabled = null!;
	private CheckBox _codexEnabled = null!;
	private TextBox _codexPath = null!;
	private CheckBox _deepSeekEnabled = null!;
	private TextBox _deepSeekKey = null!;
	private CheckBox _kimiEnabled = null!;
	private CheckBox _geminiEnabled = null!;
	private CheckBox _antigravityEnabled = null!;
	private TextBox _kimiKey = null!;
	private ComboBox _kimiRegion = null!;
	private readonly AiUsageService? _aiUsageService;
	private bool _clearDeepSeekKey;
	private bool _clearKimiKey;
	private bool _applyingLanguage;
	private readonly CancellationTokenSource _connectionTests = new();
	private bool _connectionTestsDisposed;

	public SettingsForm(AppSettings draft, IReadOnlyList<PrototypeApplicationChoice>? applicationChoices = null, AiUsageService? aiUsageService = null)
	{
		Draft = draft;
		_aiUsageService = aiUsageService;
		_quickLaunchApps = (draft.QuickLaunchApps ?? QuickLaunchDefinition.LegacyDefaults(draft.ShowChromeQuickLaunch, draft.ShowEdgeQuickLaunch))
			.Select(app => app with { }).ToList();
		BuildSettingsUi(applicationChoices ?? Array.Empty<PrototypeApplicationChoice>());
		UpdateCardSizeLabel();
		UpdateBehaviorControlState();
		ApplyLanguage();
		ApplyPreferences();
	}

	public AppSettings Draft { get; }

	private void SaveButton_Click(object? sender, EventArgs e)
	{
		RememberVisibleModule();
		var gestures = new[]
		{
			("Show / hide sidebar", _toggleSidebarHotkey.Text),
			("Previous card", _previousStageHotkey.Text),
			("Next card", _nextStageHotkey.Text)
		};
		if (_hotkeysEnabled.Checked)
		{
			var invalid = gestures.FirstOrDefault(item => !HotkeyManager.TryParse(item.Item2, out _, out _));
			if (!string.IsNullOrEmpty(invalid.Item1))
			{
				MessageBox.Show(
					this,
					UiText.Get(Draft.UiLanguage,
						$"'{invalid.Item2}' is not a valid shortcut for {invalid.Item1}.",
						$"“{invalid.Item2}”不是“{UiText.Translate(Draft.UiLanguage, invalid.Item1)}”的有效快捷键。"),
					UiText.Get(Draft.UiLanguage, "Invalid shortcut", "快捷键无效"),
					MessageBoxButtons.OK,
					MessageBoxIcon.Warning);
				return;
			}
		}

		Draft.CardScale = _cardSizeSlider.Value / 100d;
		Draft.SidebarVerticalOffset = (int)_sidebarVerticalOffset.Value;
		Draft.SidebarEdge = (SidebarEdge)_sidebarEdge.SelectedIndex;
		Draft.ShowExplorerButton = _showExplorerButton.Checked;
		Draft.QuickLaunchApps = _quickLaunchApps.Select(app => app with { }).ToList();
		Draft.SchemaVersion = Math.Max(Draft.SchemaVersion, 21);
		Draft.SettingsTheme = (SettingsTheme)_settingsTheme.SelectedIndex;
		Draft.SettingsTextSize = _settingsTextSize.SelectedIndex == 1 ? 12 : 11;
		Draft.RememberSettingsPage = _rememberSettingsPage.Checked;
		Draft.LastSettingsPage = _selectedPage;
		Draft.ShowWhatsNewOnStartup = _showWhatsNew.Checked;
		Draft.AutomaticUpdateChecks = _automaticUpdates.Checked;
		Draft.ShowChromeQuickLaunch = _quickLaunchApps.Any(app => app.Id == "browser:chrome" && app.Enabled);
		Draft.ShowEdgeQuickLaunch = _quickLaunchApps.Any(app => app.Id == "browser:edge" && app.Enabled);
		Draft.ShowExpandedPinButton = _showExpandedPinButton.Checked;
		Draft.ShowNotificationAreaCard = _showNotificationAreaCard.Checked;
		Draft.ShowDesktopButton = _showDesktopButton.Checked;
		Draft.ShowDesktopIconsButton = _showDesktopIconsButton.Checked;
		Draft.ShowAiUsageCard = _aiUsageEnabled.Checked;
		Draft.AiUsageEnabled = _aiUsageEnabled.Checked;
		Draft.AiUsageCodexEnabled = _codexEnabled.Checked;
		Draft.AiUsageDeepSeekEnabled = _deepSeekEnabled.Checked;
		Draft.AiUsageKimiEnabled = _kimiEnabled.Checked;
		Draft.AiUsageGeminiEnabled = _geminiEnabled.Checked;
		Draft.AiUsageAntigravityEnabled = _antigravityEnabled.Checked;
		Draft.AiUsageKimiInternational = _kimiRegion.SelectedIndex == 1;
		Draft.CodexCliPath = _codexPath.Text.Trim();
		if (_aiUsageService is not null)
		{
			try
			{
			var deepSeekKey = _clearDeepSeekKey ? string.Empty :
				string.IsNullOrWhiteSpace(_deepSeekKey.Text) ? _aiUsageService.GetApiKey("deepseek") ?? string.Empty : _deepSeekKey.Text.Trim();
			var kimiKey = _clearKimiKey ? string.Empty :
				string.IsNullOrWhiteSpace(_kimiKey.Text) ? _aiUsageService.GetApiKey("kimi") ?? string.Empty : _kimiKey.Text.Trim();
			_aiUsageService.SaveApiKeys(deepSeekKey, kimiKey);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
			{
				MessageBox.Show(this,
					UiText.Get(Draft.UiLanguage, "Could not securely save the API keys. Settings were not applied.", "无法安全保存 API Key，本次设置未应用。"),
					UiText.Get(Draft.UiLanguage, "Credential storage error", "密钥存储错误"),
					MessageBoxButtons.OK,
					MessageBoxIcon.Error);
				return;
			}
		}
		Draft.AnimationsEnabled = _animationsEnabled.Checked;
		Draft.LowMemoryRendering = _lowMemoryRendering.Checked;
		Draft.IdleAutoHideEnabled = _idleAutoHideEnabled.Checked;
		Draft.IdleAutoHideSeconds = (int)_idleSeconds.Value;
		Draft.StageMode = _focusEnhancedMode.Checked ? StageMode.Focus : StageMode.Coexist;
		Draft.PreviewRefreshMinutes = (int)_previewRefreshMinutes.Value;
		Draft.PausePreviewRefreshWhenHidden = _pausePreviewRefreshWhenHidden.Checked;
		Draft.StartWithWindows = _startWithWindows.Checked;
		Draft.HotkeysEnabled = _hotkeysEnabled.Checked;
		Draft.ToggleSidebarHotkey = _toggleSidebarHotkey.Text.Trim();
		Draft.PreviousStageHotkey = _previousStageHotkey.Text.Trim();
		Draft.NextStageHotkey = _nextStageHotkey.Text.Trim();
		var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < _ignoredApplications.Items.Count; index++)
		{
			if (_ignoredApplications.GetItemChecked(index) && _ignoredApplications.Items[index] is PrototypeApplicationChoice choice)
				ignored.Add(choice.ProcessName);
		}
		foreach (var processName in _ignoredProcesses.Text
			.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(value => !string.IsNullOrWhiteSpace(value)))
			ignored.Add(processName);
		Draft.IgnoredProcesses = ignored
			.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
			.ToList();
		DialogResult = DialogResult.OK;
		Close();
	}

	private void UpdateCardSizeLabel() => _cardSizeValue.Text = $"{_cardSizeSlider.Value}%";

	private void UpdateBehaviorControlState()
	{
		_idleAutoHideEnabled.Enabled = !_focusEnhancedMode.Checked;
		_idleSeconds.Enabled = !_focusEnhancedMode.Checked && _idleAutoHideEnabled.Checked;
	}

	private void ApplyLanguage()
	{
		_applyingLanguage = true;
		Text = T("Stage_Manager_Lai Settings", "Stage_Manager_Lai 设置");
		foreach (var localize in _localized) localize();
		_interfaceLanguage.SelectedIndex = Draft.UiLanguage == UiLanguage.SimplifiedChinese ? 1 : 0;
		UpdateCombo(_sidebarEdge, new[] { "Left", "Top", "Right", "Bottom" }, new[] { "左侧", "顶部", "右侧", "底部" });
		UpdateCombo(_settingsTheme, new[] { "System", "Light", "Dark" }, new[] { "跟随系统", "浅色", "深色" });
		UpdateCombo(_settingsTextSize, new[] { "Standard", "Large" }, new[] { "标准", "大号" });
		UpdateCombo(_kimiRegion, new[] { "China", "International" }, new[] { "中国区", "国际区" });
		_deepSeekKey.PlaceholderText = T("Leave blank to keep the saved key", "留空则保留已保存的密钥");
		_kimiKey.PlaceholderText = _deepSeekKey.PlaceholderText;
		_codexPath.PlaceholderText = T("Automatically detect Codex CLI", "自动检测 Codex CLI");
		UpdateQuickLaunchSummary();
		UpdatePageHeading();
		_applyingLanguage = false;
	}
	private void UpdateQuickLaunchSummary()
	{
		_quickLaunchButton.Text = UiText.Get(Draft.UiLanguage, "Choose quick-launch apps…", "选择快捷启动应用…");
		var count = _quickLaunchApps.Count(app => app.Enabled);
		_quickLaunchSummary.Text = UiText.Get(Draft.UiLanguage,
			$"{count} enabled • below File Explorer • select, reorder or add EXE / LNK",
			$"已启用 {count} 个 · 位于文件资源管理器下方 · 可选择、排序或添加 EXE / LNK");
	}

	private void ResetDefaults()
	{
		_cardSizeSlider.Value = 60;
		_settingsTheme.SelectedIndex = 0;
		_settingsTextSize.SelectedIndex = 0;
		_rememberSettingsPage.Checked = true;
		_showWhatsNew.Checked = true;
		_automaticUpdates.Checked = true;
		_geminiEnabled.Checked = true;
		_antigravityEnabled.Checked = true;
		_sidebarVerticalOffset.Value = -80;
		_sidebarEdge.SelectedIndex = 0;
		_showExplorerButton.Checked = true;
		_quickLaunchApps = QuickLaunchDefinition.LegacyDefaults();
		UpdateQuickLaunchSummary();
		_showExpandedPinButton.Checked = true;
		_showNotificationAreaCard.Checked = true;
		_showDesktopButton.Checked = true;
		_showDesktopIconsButton.Checked = true;
		_aiUsageEnabled.Checked = false;
		_codexEnabled.Checked = true;
		_deepSeekEnabled.Checked = true;
		_kimiEnabled.Checked = false;
		_kimiRegion.SelectedIndex = 0;
		_codexPath.Clear();
		_animationsEnabled.Checked = true;
		_lowMemoryRendering.Checked = true;
		_idleAutoHideEnabled.Checked = true;
		_idleSeconds.Value = 60;
		_focusEnhancedMode.Checked = false;
		_previewRefreshMinutes.Value = 5;
		_pausePreviewRefreshWhenHidden.Checked = true;
		_startWithWindows.Checked = true;
		_hotkeysEnabled.Checked = true;
		_toggleSidebarHotkey.Text = "Win+Alt+S";
		_previousStageHotkey.Text = "Win+Alt+[";
		_nextStageHotkey.Text = "Win+Alt+]";
		for (var index = 0; index < _ignoredApplications.Items.Count; index++)
			_ignoredApplications.SetItemChecked(index, false);
		_ignoredProcesses.Clear();
	}

	private void BrowseCodexCli()
	{
		using var dialog = new OpenFileDialog
		{
			Title = UiText.Get(Draft.UiLanguage, "Select Codex CLI", "选择 Codex CLI"),
			Filter = "Codex CLI|codex.exe;codex.cmd;codex.bat;codex.ps1|All files|*.*",
			CheckFileExists = true,
			FileName = _codexPath.Text
		};
		if (dialog.ShowDialog(this) == DialogResult.OK)
			_codexPath.Text = dialog.FileName;
	}

	private async Task TestCodexAsync(Button button)
	{
		button.Enabled = false;
		try
		{
			var status = _aiUsageService is null
				? "AI usage service is unavailable"
				: await _aiUsageService.TestCodexAsync(_codexPath.Text.Trim(), _connectionTests.Token);
			ShowConnectionResult("Codex", status);
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { ShowConnectionResult("Codex", exception.GetType().Name); }
		finally { if (!IsDisposed) button.Enabled = true; }
	}

	private async Task TestApiAsync(string provider, TextBox field, Button button)
	{
		button.Enabled = false;
		try
		{
			var key = field.Text.Trim();
			if (string.IsNullOrWhiteSpace(key) && !((provider == "deepseek" && _clearDeepSeekKey) || (provider == "kimi" && _clearKimiKey)))
				key = _aiUsageService?.GetApiKey(provider) ?? string.Empty;
			var status = string.IsNullOrWhiteSpace(key)
				? "API key is not configured"
				: _aiUsageService is null
					? "AI usage service is unavailable"
					: await _aiUsageService.TestApiKeyAsync(provider, key, _kimiRegion.SelectedIndex == 1, _connectionTests.Token);
			ShowConnectionResult(provider == "deepseek" ? "DeepSeek" : "Kimi", status);
		}
		catch (OperationCanceledException) { }
		catch (Exception exception) { ShowConnectionResult(provider, exception.GetType().Name); }
		finally { if (!IsDisposed) button.Enabled = true; }
	}

	private void ShowConnectionResult(string provider, string status)
	{
		if (IsDisposed || Disposing) return;
		var connected = string.Equals(status, "Connected", StringComparison.Ordinal);
		MessageBox.Show(this,
			$"{provider}: {UiText.Get(Draft.UiLanguage, status, TranslateStatus(status))}",
			UiText.Get(Draft.UiLanguage, "Connection test", "连接测试"),
			MessageBoxButtons.OK,
			connected ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
	}

	private string TranslateStatus(string status) => status switch
	{
		"Connected" => "已连接",
		"Waiting for API key" => "等待 API Key",
		"API key is not configured" => "尚未填写 API Key",
		"Codex CLI not found" => "未找到 Codex CLI",
		"Codex login required" => "Codex 尚未登录",
		"Codex account is not connected" => "Codex 账户未连接",
		"Codex quota is unavailable" => "无法读取 Codex 额度",
		"No Codex rate limits returned" => "Codex 未返回额度窗口",
		"API key rejected" => "API Key 无效",
		"Request timed out" or "Codex query timed out" => "请求超时",
		_ => status
	};

	public void ScrollToAiUsage()
	{
		SelectPage("ai");
	}
	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_connectionTests.Cancel();
		base.OnFormClosed(e);
	}
	protected override void Dispose(bool disposing)
	{
		if (disposing) _navigationTimer?.Dispose();
		if (disposing && !_connectionTestsDisposed)
		{ _connectionTestsDisposed = true; _connectionTests.Cancel(); _connectionTests.Dispose(); }
		base.Dispose(disposing);
		if (disposing) { foreach (var font in _ownedFonts) font.Dispose(); _ownedFonts.Clear(); }
	}
}
