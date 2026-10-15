using StageManager.Services;
using StageManager.Settings;
using StageManager.Card3DPrototype.QuickLaunch;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Card3DPrototype.Updates;
using System.Security.Cryptography;

namespace StageManager.Card3DPrototype;

internal sealed class SettingsForm : Form
{
	private readonly TrackBar _cardSizeSlider;
	private readonly Label _cardSizeValue;
	private readonly NumericUpDown _sidebarVerticalOffset;
	private readonly CheckBox _showExplorerButton;
	private readonly Button _quickLaunchButton;
	private readonly Label _quickLaunchSummary;
	private List<QuickLaunchDefinition> _quickLaunchApps;
	private readonly CheckBox _showExpandedPinButton;
	private readonly CheckBox _showNotificationAreaCard;
	private readonly CheckBox _showDesktopButton;
	private readonly CheckBox _showDesktopIconsButton;
	private readonly CheckBox _animationsEnabled;
	private readonly CheckBox _lowMemoryRendering;
	private readonly CheckBox _idleAutoHideEnabled;
	private readonly NumericUpDown _idleSeconds;
	private readonly CheckBox _focusEnhancedMode;
	private readonly NumericUpDown _previewRefreshMinutes;
	private readonly CheckBox _pausePreviewRefreshWhenHidden;
	private readonly CheckBox _startWithWindows;
	private readonly CheckBox _hotkeysEnabled;
	private readonly TextBox _toggleSidebarHotkey;
	private readonly TextBox _previousStageHotkey;
	private readonly TextBox _nextStageHotkey;
	private readonly CheckedListBox _ignoredApplications;
	private readonly TextBox _ignoredProcesses;
	private readonly Button _languageButton;
	private readonly ComboBox _interfaceLanguage;
	private readonly CheckBox _aiUsageEnabled;
	private readonly CheckBox _codexEnabled;
	private readonly TextBox _codexPath;
	private readonly CheckBox _deepSeekEnabled;
	private readonly TextBox _deepSeekKey;
	private readonly CheckBox _kimiEnabled;
	private readonly TextBox _kimiKey;
	private readonly ComboBox _kimiRegion;
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
		Text = "Stage_Manager_Lai Settings";
		FormBorderStyle = FormBorderStyle.Sizable;
		StartPosition = FormStartPosition.CenterScreen;
		ShowInTaskbar = false;
		MaximizeBox = true;
		MinimizeBox = false;
		MinimumSize = new Size(640, 700);
		AutoScaleMode = AutoScaleMode.Dpi;
		AutoScroll = true;
		var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
		ClientSize = new Size(620, Math.Min(900, Math.Max(660, workingArea.Height - 80)));
		BackColor = Color.FromArgb(24, 26, 31);
		ForeColor = Color.FromArgb(244, 246, 250);
		Font = new Font("Segoe UI", 10f);

		Controls.Add(new Label
		{
			Text = AppVersion.Label,
			Font = new Font("Segoe UI", 17f, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(22, 18)
		});
		_languageButton = new Button
		{
			Location = new Point(448, 14),
			Size = new Size(150, 34),
			Anchor = AnchorStyles.Top | AnchorStyles.Right,
			FlatStyle = FlatStyle.Flat
		};
		_languageButton.FlatAppearance.BorderColor = Color.FromArgb(83, 89, 102);
		_languageButton.Click += (_, _) => ToggleLanguage();
		Controls.Add(_languageButton);

		var appearanceGroup = CreateGroup("Appearance", new Rectangle(20, 58, 580, 421));
		appearanceGroup.Controls.Add(CreateLabel("Card size", 18, 31, 105));
		_cardSizeSlider = new TrackBar
		{
			Minimum = 55,
			Maximum = 125,
			TickFrequency = 5,
			SmallChange = 5,
			LargeChange = 10,
			Value = Math.Clamp((int)Math.Round(draft.CardScale * 100), 55, 125),
			Location = new Point(125, 24),
			Size = new Size(350, 48)
		};
		_cardSizeValue = CreateLabel(string.Empty, 485, 31, 65);
		_cardSizeValue.TextAlign = ContentAlignment.MiddleCenter;
		_cardSizeValue.Font = new Font(Font, FontStyle.Bold);
		_cardSizeSlider.ValueChanged += (_, _) => UpdateCardSizeLabel();
		appearanceGroup.Controls.Add(CreateLabel("Interface language", 18, 77, 130));
		_interfaceLanguage = new ComboBox
		{
			DropDownStyle = ComboBoxStyle.DropDownList,
			Location = new Point(150, 76),
			Size = new Size(325, 30),
			Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
		};
		_interfaceLanguage.Items.AddRange(new object[] { "English", "Simplified Chinese" });
		_interfaceLanguage.SelectedItem = draft.UiLanguage == UiLanguage.SimplifiedChinese
			? "Simplified Chinese"
			: "English";
		_interfaceLanguage.SelectedIndexChanged += (_, _) => SetLanguageFromSelection();
		_animationsEnabled = CreateCheckBox("Use animations", draft.AnimationsEnabled, 18, 119, 220);
		_lowMemoryRendering = CreateCheckBox("Low-memory renderer (restart required)", draft.LowMemoryRendering, 250, 119, 310);
		appearanceGroup.Controls.Add(CreateLabel("Vertical position", 18, 160, 125));
		_sidebarVerticalOffset = new NumericUpDown
		{
			Minimum = -400,
			Maximum = 400,
			Increment = 10,
			Value = Math.Clamp(draft.SidebarVerticalOffset, -400, 400),
			Location = new Point(150, 157),
			Size = new Size(75, 30)
		};
		appearanceGroup.Controls.Add(_sidebarVerticalOffset);
		appearanceGroup.Controls.Add(CreateLabel("px (negative = up)", 235, 160, 270));
		_showExplorerButton = CreateCheckBox(
			"Show File Explorer button above cards",
			draft.ShowExplorerButton,
			18,
			195,
			520);
		appearanceGroup.Controls.Add(_showExplorerButton);
		_quickLaunchButton = new Button { Location = new Point(18, 225), Size = new Size(270, 30) };
		_quickLaunchButton.Click += (_, _) =>
		{
			using var picker = new QuickLaunchSettingsForm(_quickLaunchApps, Draft.UiLanguage);
			if (picker.ShowDialog(this) != DialogResult.OK) return;
			_quickLaunchApps = picker.GetSelection();
			UpdateQuickLaunchSummary();
		};
		_quickLaunchSummary = CreateLabel(string.Empty, 18, 255, 540);
		appearanceGroup.Controls.AddRange(new Control[] { _quickLaunchButton, _quickLaunchSummary });
		_showExpandedPinButton = CreateCheckBox(
			"Show pin button when multi-window cards are expanded",
			draft.ShowExpandedPinButton,
			18,
			285,
			540);
		appearanceGroup.Controls.Add(_showExpandedPinButton);
		_showNotificationAreaCard = CreateCheckBox(
			"Show Windows hidden icons in a bottom card",
			draft.ShowNotificationAreaCard,
			18,
			315,
			540);
		appearanceGroup.Controls.Add(_showNotificationAreaCard);
		_showDesktopButton = CreateCheckBox(
			"Show minimize / restore shortcut below window cards",
			draft.ShowDesktopButton,
			18,
			345,
			540);
		appearanceGroup.Controls.Add(_showDesktopButton);
		_showDesktopIconsButton = CreateCheckBox(
			"Show desktop-icons toggle below minimize / restore",
			draft.ShowDesktopIconsButton,
			18,
			375,
			540);
		appearanceGroup.Controls.Add(_showDesktopIconsButton);
		appearanceGroup.Controls.AddRange(new Control[] { _cardSizeSlider, _cardSizeValue, _interfaceLanguage, _animationsEnabled, _lowMemoryRendering });

		var aiGroup = CreateGroup("AI usage", new Rectangle(20, 489, 580, 252));
		aiGroup.Name = "AiUsage";
		_aiUsageEnabled = CreateCheckBox("Show AI usage card", draft.ShowAiUsageCard && draft.AiUsageEnabled, 18, 22, 280);
		_codexEnabled = CreateCheckBox("Codex", draft.AiUsageCodexEnabled, 18, 55, 104);
		_codexPath = CreateTextBox(draft.CodexCliPath, 124, 55, 282);
		_codexPath.PlaceholderText = UiText.Get(draft.UiLanguage, "Auto-detect local Codex login", "自动检测本机 Codex，复用已有登录");
		_codexPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		var browseCodex = new Button { Text = "Browse...", Location = new Point(411, 55), Size = new Size(70, 30) };
		browseCodex.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		browseCodex.Click += (_, _) => BrowseCodexCli();
		var testCodex = new Button { Text = "Test", Location = new Point(487, 55), Size = new Size(70, 30) };
		testCodex.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		testCodex.Click += async (_, _) => await TestCodexAsync(testCodex);
		_deepSeekEnabled = CreateCheckBox("DeepSeek", draft.AiUsageDeepSeekEnabled, 18, 89, 104);
		_deepSeekKey = CreateTextBox(string.Empty, 124, 89, 282);
		_deepSeekKey.UseSystemPasswordChar = true;
		_deepSeekKey.PlaceholderText = "API key (encrypted on this PC)";
		_deepSeekKey.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(_deepSeekKey.Text)) _clearDeepSeekKey = false; };
		_deepSeekKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		var clearDeepSeek = new Button { Text = "Clear", Location = new Point(411, 89), Size = new Size(70, 30) };
		clearDeepSeek.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		clearDeepSeek.Click += (_, _) => { _deepSeekKey.Clear(); _clearDeepSeekKey = true; };
		var testDeepSeek = new Button { Text = "Test", Location = new Point(487, 89), Size = new Size(70, 30) };
		testDeepSeek.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		testDeepSeek.Click += async (_, _) => await TestApiAsync("deepseek", _deepSeekKey, testDeepSeek);
		_kimiEnabled = CreateCheckBox("Kimi", draft.AiUsageKimiEnabled, 18, 123, 104);
		_kimiKey = CreateTextBox(string.Empty, 124, 123, 282);
		_kimiKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		_kimiKey.UseSystemPasswordChar = true;
		_kimiKey.PlaceholderText = "API key";
		_kimiKey.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(_kimiKey.Text)) _clearKimiKey = false; };
		var regionLabel = CreateLabel("Region", 18, 161, 90);
		_kimiRegion = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(124, 158), Size = new Size(180, 30) };
		_kimiRegion.Items.AddRange(new object[] { "CN", "Global" });
		_kimiRegion.SelectedIndex = draft.AiUsageKimiInternational ? 1 : 0;
		var clearKimi = new Button { Text = "Clear", Location = new Point(411, 123), Size = new Size(70, 30) };
		clearKimi.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		clearKimi.Click += (_, _) => { _kimiKey.Clear(); _clearKimiKey = true; };
		var testKimi = new Button { Text = "Test", Location = new Point(487, 123), Size = new Size(70, 30) };
		testKimi.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		testKimi.Click += async (_, _) => await TestApiAsync("kimi", _kimiKey, testKimi);
		var aiNote = CreateLabel("Blank keys keep the saved key. Keys are protected for this Windows user only.", 18, 201, 540);
		aiNote.Height = 40;
		aiNote.ForeColor = Color.FromArgb(174, 185, 202);
		aiGroup.Controls.AddRange(new Control[]
		{
			_aiUsageEnabled, _codexEnabled, _codexPath, browseCodex, testCodex,
			_deepSeekEnabled, _deepSeekKey, clearDeepSeek, testDeepSeek,
			_kimiEnabled, _kimiKey, regionLabel, _kimiRegion, clearKimi, testKimi, aiNote
		});
		var behaviorGroup = CreateGroup("Behavior", new Rectangle(20, 757, 580, 204));
		_idleAutoHideEnabled = CreateCheckBox("Auto-hide after no pointer activity", draft.IdleAutoHideEnabled, 18, 31, 300);
		behaviorGroup.Controls.Add(_idleAutoHideEnabled);
		behaviorGroup.Controls.Add(CreateLabel("Idle delay", 325, 33, 78));
		_idleSeconds = new NumericUpDown
		{
			Minimum = 15,
			Maximum = 600,
			Value = Math.Clamp(draft.IdleAutoHideSeconds, 15, 600),
			Increment = 15,
			Location = new Point(408, 29),
			Size = new Size(75, 30)
		};
		behaviorGroup.Controls.Add(_idleSeconds);
		_idleAutoHideEnabled.CheckedChanged += (_, _) => UpdateBehaviorControlState();
		behaviorGroup.Controls.Add(CreateLabel("seconds", 490, 33, 65));
		_startWithWindows = CreateCheckBox("Start with Windows", draft.StartWithWindows, 18, 76, 220);
		behaviorGroup.Controls.Add(_startWithWindows);
		behaviorGroup.Controls.Add(CreateLabel("Preview refresh", 325, 78, 112));
		_previewRefreshMinutes = new NumericUpDown
		{
			Minimum = 1,
			Maximum = 60,
			Value = Math.Clamp(draft.PreviewRefreshMinutes, 1, 60),
			Increment = 1,
			Location = new Point(440, 74),
			Size = new Size(65, 30)
		};
		behaviorGroup.Controls.Add(_previewRefreshMinutes);
		behaviorGroup.Controls.Add(CreateLabel("min", 510, 78, 45));
		_focusEnhancedMode = CreateCheckBox(
			"Focus enhanced mode (reserve the card column)",
			draft.StageMode == StageMode.Focus,
			18,
			116,
			520);
		_focusEnhancedMode.CheckedChanged += (_, _) => UpdateBehaviorControlState();
		behaviorGroup.Controls.Add(_focusEnhancedMode);
		_pausePreviewRefreshWhenHidden = CreateCheckBox(
			"Pause preview refresh while the sidebar is hidden",
			draft.PausePreviewRefreshWhenHidden,
			18,
			154,
			430);
		behaviorGroup.Controls.Add(_pausePreviewRefreshWhenHidden);

		var shortcutsGroup = CreateGroup("Keyboard shortcuts", new Rectangle(20, 971, 580, 190));
		_hotkeysEnabled = CreateCheckBox("Enable global shortcuts", draft.HotkeysEnabled, 18, 28, 260);
		shortcutsGroup.Controls.Add(_hotkeysEnabled);
		shortcutsGroup.Controls.Add(CreateLabel("Show / hide sidebar", 18, 70, 180));
		shortcutsGroup.Controls.Add(CreateLabel("Previous card", 18, 108, 180));
		shortcutsGroup.Controls.Add(CreateLabel("Next card", 18, 146, 180));
		_toggleSidebarHotkey = CreateTextBox(draft.ToggleSidebarHotkey, 205, 66, 330);
		_previousStageHotkey = CreateTextBox(draft.PreviousStageHotkey, 205, 104, 330);
		_nextStageHotkey = CreateTextBox(draft.NextStageHotkey, 205, 142, 330);
		_toggleSidebarHotkey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		_previousStageHotkey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		_nextStageHotkey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		shortcutsGroup.Controls.AddRange(new Control[] { _toggleSidebarHotkey, _previousStageHotkey, _nextStageHotkey });

		var ignoredGroup = CreateGroup("Ignored applications", new Rectangle(20, 1171, 580, 192));
		ignoredGroup.Controls.Add(CreateLabel("Check an app to hide its card only; the app remains fully usable.", 18, 25, 540));
		_ignoredApplications = new CheckedListBox
		{
			CheckOnClick = true,
			FormattingEnabled = true,
			IntegralHeight = false,
			BackColor = Color.FromArgb(34, 37, 44),
			ForeColor = Color.FromArgb(244, 246, 250),
			Location = new Point(18, 51),
			Size = new Size(540, 88),
			Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
		};
		var choices = applicationChoices ?? Array.Empty<PrototypeApplicationChoice>();
		var choiceNames = choices
			.Select(choice => choice.ProcessName)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var choice in choices)
		{
			var index = _ignoredApplications.Items.Add(choice);
			_ignoredApplications.SetItemChecked(index, draft.IgnoredProcesses.Contains(choice.ProcessName, StringComparer.OrdinalIgnoreCase));
		}
		ignoredGroup.Controls.Add(_ignoredApplications);
		ignoredGroup.Controls.Add(CreateLabel("Advanced: process names (optional)", 18, 138, 250));
		_ignoredProcesses = new TextBox
		{
			Multiline = true,
			ScrollBars = ScrollBars.Vertical,
			Text = string.Join(Environment.NewLine, draft.IgnoredProcesses.Where(name => !choiceNames.Contains(name))),
			Location = new Point(18, 166),
			Size = new Size(540, 20),
			Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
		};
		ignoredGroup.Controls.Add(_ignoredProcesses);

		var cancelButton = new Button
		{
			Text = "Cancel",
			DialogResult = DialogResult.Cancel,
			Location = new Point(412, 1397),
			Size = new Size(88, 34),
			Anchor = AnchorStyles.Top | AnchorStyles.Right
		};
		var resetButton = new Button
		{
			Text = "Reset defaults",
			Location = new Point(20, 1397),
			Size = new Size(120, 34)
		};
		resetButton.Click += (_, _) => ResetDefaults();
		var saveButton = new Button
		{
			Text = "Save",
			Location = new Point(510, 1397),
			Size = new Size(88, 34),
			Anchor = AnchorStyles.Top | AnchorStyles.Right
		};
		saveButton.Click += SaveButton_Click;
		Controls.AddRange(new Control[] { appearanceGroup, aiGroup, behaviorGroup, shortcutsGroup, ignoredGroup, resetButton, cancelButton, saveButton });
		// Reserve a separate row, retaining the existing resizable settings layout.
		foreach (Control control in Controls) if (control.Top >= 58) control.Top += 44;
		var updateButton = new Button { Text = "Check for updates", Bounds = new Rectangle(20, 58, 180, 32) };
		var newsButton = new Button { Text = "What's new", Bounds = new Rectangle(214, 58, 150, 32) };
		updateButton.Click += (_, _) => { using var dialog = new UpdateForm(Draft.UiLanguage); dialog.ShowDialog(this); };
		newsButton.Click += (_, _) =>
		{
			var news = new WhatsNewForm(Draft.UiLanguage);
			news.OpenAiSettings += (_, _) => ScrollToAiUsage();
			news.Show(this);
		};
		Controls.Add(updateButton); Controls.Add(newsButton);
		AcceptButton = saveButton;
		CancelButton = cancelButton;
		UpdateCardSizeLabel();
		UpdateBehaviorControlState();
		ApplyLanguage();
	}

	public AppSettings Draft { get; }

	private void SaveButton_Click(object? sender, EventArgs e)
	{
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
		Draft.ShowExplorerButton = _showExplorerButton.Checked;
		Draft.QuickLaunchApps = _quickLaunchApps.Select(app => app with { }).ToList();
		Draft.SchemaVersion = Math.Max(Draft.SchemaVersion, 18);
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

	private GroupBox CreateGroup(string text, Rectangle bounds)
	{
		return new GroupBox
		{
			Text = text,
			Bounds = bounds,
			Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
			ForeColor = ForeColor,
			BackColor = BackColor
		};
	}

	private static Label CreateLabel(string text, int x, int y, int width) => new()
	{
		Text = text,
		Location = new Point(x, y),
		Size = new Size(width, 28),
		TextAlign = ContentAlignment.MiddleLeft
	};

	private static CheckBox CreateCheckBox(string text, bool isChecked, int x, int y, int width) => new()
	{
		Text = text,
		Checked = isChecked,
		Location = new Point(x, y),
		Size = new Size(width, 30)
	};

	private static TextBox CreateTextBox(string text, int x, int y, int width) => new()
	{
		Text = text,
		Location = new Point(x, y),
		Size = new Size(width, 30)
	};

	private void UpdateCardSizeLabel() => _cardSizeValue.Text = $"{_cardSizeSlider.Value}%";

	private void UpdateBehaviorControlState()
	{
		_idleAutoHideEnabled.Enabled = !_focusEnhancedMode.Checked;
		_idleSeconds.Enabled = !_focusEnhancedMode.Checked && _idleAutoHideEnabled.Checked;
	}

	private void ToggleLanguage()
	{
		SetLanguage(Draft.UiLanguage == UiLanguage.SimplifiedChinese
			? UiLanguage.English
			: UiLanguage.SimplifiedChinese);
	}

	private void SetLanguageFromSelection()
	{
		if (_applyingLanguage)
			return;
		SetLanguage(string.Equals(_interfaceLanguage.SelectedItem as string, "Simplified Chinese", StringComparison.Ordinal)
			? UiLanguage.SimplifiedChinese
			: UiLanguage.English);
	}

	private void SetLanguage(UiLanguage language)
	{
		Draft.UiLanguage = language;
		ApplyLanguage();
	}

	private void ApplyLanguage()
	{
		_applyingLanguage = true;
		_interfaceLanguage.SelectedItem = Draft.UiLanguage == UiLanguage.SimplifiedChinese
			? "Simplified Chinese"
			: "English";
		UiText.Apply(this, Draft.UiLanguage);
		_deepSeekKey.PlaceholderText = UiText.Get(Draft.UiLanguage, "API key (encrypted on this PC)", "API Key（在本机加密保存）");
		_kimiKey.PlaceholderText = UiText.Get(Draft.UiLanguage, "API key", "API Key");
		_languageButton.Text = UiText.Get(Draft.UiLanguage, "简体中文", "English");
		_languageButton.AccessibleName = UiText.Get(Draft.UiLanguage, "Switch to Simplified Chinese", "切换到英文");
		UpdateQuickLaunchSummary();
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
		_sidebarVerticalOffset.Value = -80;
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
		if (Controls.Find("AiUsage", false).FirstOrDefault() is { } group) ScrollControlIntoView(group);
	}
	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_connectionTests.Cancel();
		base.OnFormClosed(e);
	}
	protected override void Dispose(bool disposing)
	{
		if (disposing && !_connectionTestsDisposed)
		{ _connectionTestsDisposed = true; _connectionTests.Cancel(); _connectionTests.Dispose(); }
		base.Dispose(disposing);
	}
}
