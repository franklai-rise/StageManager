using StageManager.Services;
using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal sealed class SettingsForm : Form
{
	private readonly TrackBar _cardSizeSlider;
	private readonly Label _cardSizeValue;
	private readonly CheckBox _animationsEnabled;
	private readonly CheckBox _lowMemoryRendering;
	private readonly CheckBox _idleAutoHideEnabled;
	private readonly NumericUpDown _idleSeconds;
	private readonly NumericUpDown _previewRefreshMinutes;
	private readonly CheckBox _pausePreviewRefreshWhenHidden;
	private readonly CheckBox _startWithWindows;
	private readonly CheckBox _hotkeysEnabled;
	private readonly TextBox _toggleSidebarHotkey;
	private readonly TextBox _previousStageHotkey;
	private readonly TextBox _nextStageHotkey;
	private readonly CheckedListBox _ignoredApplications;
	private readonly TextBox _ignoredProcesses;

	public SettingsForm(AppSettings draft, IReadOnlyList<PrototypeApplicationChoice>? applicationChoices = null)
	{
		Draft = draft;
		Text = "Stage_Manager_Lai Settings";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		StartPosition = FormStartPosition.CenterScreen;
		ShowInTaskbar = false;
		MaximizeBox = false;
		MinimizeBox = false;
		AutoScaleMode = AutoScaleMode.Dpi;
		AutoScroll = true;
		var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
		ClientSize = new Size(620, Math.Min(855, Math.Max(620, workingArea.Height - 80)));
		BackColor = Color.FromArgb(24, 26, 31);
		ForeColor = Color.FromArgb(244, 246, 250);
		Font = new Font("Segoe UI", 10f);

		Controls.Add(new Label
		{
			Text = AppVersionInfo.DisplayName,
			Font = new Font("Segoe UI", 17f, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(22, 18)
		});

		var appearanceGroup = CreateGroup("Appearance", new Rectangle(20, 58, 580, 135));
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
		_animationsEnabled = CreateCheckBox("Use animations", draft.AnimationsEnabled, 18, 82, 220);
		_lowMemoryRendering = CreateCheckBox("Low-memory renderer (restart required)", draft.LowMemoryRendering, 250, 82, 310);
		appearanceGroup.Controls.AddRange(new Control[] { _cardSizeSlider, _cardSizeValue, _animationsEnabled, _lowMemoryRendering });

		var behaviorGroup = CreateGroup("Behavior", new Rectangle(20, 203, 580, 166));
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
		_idleSeconds.Enabled = _idleAutoHideEnabled.Checked;
		_idleAutoHideEnabled.CheckedChanged += (_, _) => _idleSeconds.Enabled = _idleAutoHideEnabled.Checked;
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
		_pausePreviewRefreshWhenHidden = CreateCheckBox(
			"Pause preview refresh while the sidebar is hidden",
			draft.PausePreviewRefreshWhenHidden,
			18,
			116,
			430);
		behaviorGroup.Controls.Add(_pausePreviewRefreshWhenHidden);

		var shortcutsGroup = CreateGroup("Keyboard shortcuts", new Rectangle(20, 379, 580, 190));
		_hotkeysEnabled = CreateCheckBox("Enable global shortcuts", draft.HotkeysEnabled, 18, 28, 260);
		shortcutsGroup.Controls.Add(_hotkeysEnabled);
		shortcutsGroup.Controls.Add(CreateLabel("Show / hide sidebar", 18, 70, 180));
		shortcutsGroup.Controls.Add(CreateLabel("Previous card", 18, 108, 180));
		shortcutsGroup.Controls.Add(CreateLabel("Next card", 18, 146, 180));
		_toggleSidebarHotkey = CreateTextBox(draft.ToggleSidebarHotkey, 205, 66, 330);
		_previousStageHotkey = CreateTextBox(draft.PreviousStageHotkey, 205, 104, 330);
		_nextStageHotkey = CreateTextBox(draft.NextStageHotkey, 205, 142, 330);
		shortcutsGroup.Controls.AddRange(new Control[] { _toggleSidebarHotkey, _previousStageHotkey, _nextStageHotkey });

		var ignoredGroup = CreateGroup("Ignored applications", new Rectangle(20, 579, 580, 192));
		ignoredGroup.Controls.Add(CreateLabel("Check a running app to hide it; no .exe name is required.", 18, 25, 540));
		_ignoredApplications = new CheckedListBox
		{
			CheckOnClick = true,
			FormattingEnabled = true,
			IntegralHeight = false,
			BackColor = Color.FromArgb(34, 37, 44),
			ForeColor = Color.FromArgb(244, 246, 250),
			Location = new Point(18, 51),
			Size = new Size(540, 88)
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
			Size = new Size(540, 20)
		};
		ignoredGroup.Controls.Add(_ignoredProcesses);

		var cancelButton = new Button
		{
			Text = "Cancel",
			DialogResult = DialogResult.Cancel,
			Location = new Point(412, 805),
			Size = new Size(88, 34)
		};
		var resetButton = new Button
		{
			Text = "Reset defaults",
			Location = new Point(20, 805),
			Size = new Size(120, 34)
		};
		resetButton.Click += (_, _) => ResetDefaults();
		var saveButton = new Button
		{
			Text = "Save",
			Location = new Point(510, 805),
			Size = new Size(88, 34)
		};
		saveButton.Click += SaveButton_Click;
		Controls.AddRange(new Control[] { appearanceGroup, behaviorGroup, shortcutsGroup, ignoredGroup, resetButton, cancelButton, saveButton });
		AcceptButton = saveButton;
		CancelButton = cancelButton;
		UpdateCardSizeLabel();
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
			var invalid = gestures.FirstOrDefault(item => !HotkeyGestureParser.TryParse(item.Item2, out _, out _));
			if (!string.IsNullOrEmpty(invalid.Item1))
			{
				MessageBox.Show(this, $"'{invalid.Item2}' is not a valid shortcut for {invalid.Item1}.", "Invalid shortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}
		}

		Draft.CardScale = _cardSizeSlider.Value / 100d;
		Draft.AnimationsEnabled = _animationsEnabled.Checked;
		Draft.LowMemoryRendering = _lowMemoryRendering.Checked;
		Draft.IdleAutoHideEnabled = _idleAutoHideEnabled.Checked;
		Draft.IdleAutoHideSeconds = (int)_idleSeconds.Value;
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

	private void ResetDefaults()
	{
		_cardSizeSlider.Value = 60;
		_animationsEnabled.Checked = true;
		_lowMemoryRendering.Checked = true;
		_idleAutoHideEnabled.Checked = true;
		_idleSeconds.Value = 60;
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
}
