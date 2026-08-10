using StageManager.Services;
using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal sealed class SettingsForm : Form
{
	private readonly TrackBar _cardSizeSlider;
	private readonly Label _cardSizeValue;
	private readonly CheckBox _animationsEnabled;
	private readonly CheckBox _idleAutoHideEnabled;
	private readonly NumericUpDown _idleSeconds;
	private readonly CheckBox _startWithWindows;
	private readonly CheckBox _hotkeysEnabled;
	private readonly TextBox _toggleSidebarHotkey;
	private readonly TextBox _previousStageHotkey;
	private readonly TextBox _nextStageHotkey;
	private readonly TextBox _ignoredProcesses;

	public SettingsForm(AppSettings draft)
	{
		Draft = draft;
		Text = "Stage_Manager_Lai Settings";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		StartPosition = FormStartPosition.CenterScreen;
		ShowInTaskbar = false;
		MaximizeBox = false;
		MinimizeBox = false;
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size(620, 710);
		BackColor = Color.FromArgb(24, 26, 31);
		ForeColor = Color.FromArgb(244, 246, 250);
		Font = new Font("Segoe UI", 10f);

		Controls.Add(new Label
		{
			Text = "Stage_Manager_Lai v2.2.2",
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
		appearanceGroup.Controls.AddRange(new Control[] { _cardSizeSlider, _cardSizeValue, _animationsEnabled });

		var behaviorGroup = CreateGroup("Behavior", new Rectangle(20, 203, 580, 122));
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
		behaviorGroup.Controls.Add(CreateLabel("seconds", 490, 33, 65));
		_startWithWindows = CreateCheckBox("Start with Windows", draft.StartWithWindows, 18, 76, 220);
		behaviorGroup.Controls.Add(_startWithWindows);

		var shortcutsGroup = CreateGroup("Keyboard shortcuts", new Rectangle(20, 335, 580, 190));
		_hotkeysEnabled = CreateCheckBox("Enable global shortcuts", draft.HotkeysEnabled, 18, 28, 260);
		shortcutsGroup.Controls.Add(_hotkeysEnabled);
		shortcutsGroup.Controls.Add(CreateLabel("Show / hide sidebar", 18, 70, 180));
		shortcutsGroup.Controls.Add(CreateLabel("Previous card", 18, 108, 180));
		shortcutsGroup.Controls.Add(CreateLabel("Next card", 18, 146, 180));
		_toggleSidebarHotkey = CreateTextBox(draft.ToggleSidebarHotkey, 205, 66, 330);
		_previousStageHotkey = CreateTextBox(draft.PreviousStageHotkey, 205, 104, 330);
		_nextStageHotkey = CreateTextBox(draft.NextStageHotkey, 205, 142, 330);
		shortcutsGroup.Controls.AddRange(new Control[] { _toggleSidebarHotkey, _previousStageHotkey, _nextStageHotkey });

		var ignoredGroup = CreateGroup("Ignored applications", new Rectangle(20, 535, 580, 112));
		_ignoredProcesses = new TextBox
		{
			Multiline = true,
			ScrollBars = ScrollBars.Vertical,
			Text = string.Join(Environment.NewLine, draft.IgnoredProcesses),
			Location = new Point(18, 27),
			Size = new Size(540, 66)
		};
		ignoredGroup.Controls.Add(_ignoredProcesses);

		var cancelButton = new Button
		{
			Text = "Cancel",
			DialogResult = DialogResult.Cancel,
			Location = new Point(412, 662),
			Size = new Size(88, 34)
		};
		var saveButton = new Button
		{
			Text = "Save",
			Location = new Point(510, 662),
			Size = new Size(88, 34)
		};
		saveButton.Click += SaveButton_Click;
		Controls.AddRange(new Control[] { appearanceGroup, behaviorGroup, shortcutsGroup, ignoredGroup, cancelButton, saveButton });
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
			var invalid = gestures.FirstOrDefault(item => !HotkeyManager.TryParse(item.Item2, out _, out _));
			if (!string.IsNullOrEmpty(invalid.Item1))
			{
				MessageBox.Show(this, $"'{invalid.Item2}' is not a valid shortcut for {invalid.Item1}.", "Invalid shortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}
		}

		Draft.CardScale = _cardSizeSlider.Value / 100d;
		Draft.AnimationsEnabled = _animationsEnabled.Checked;
		Draft.IdleAutoHideEnabled = _idleAutoHideEnabled.Checked;
		Draft.IdleAutoHideSeconds = (int)_idleSeconds.Value;
		Draft.StartWithWindows = _startWithWindows.Checked;
		Draft.HotkeysEnabled = _hotkeysEnabled.Checked;
		Draft.ToggleSidebarHotkey = _toggleSidebarHotkey.Text.Trim();
		Draft.PreviousStageHotkey = _previousStageHotkey.Text.Trim();
		Draft.NextStageHotkey = _nextStageHotkey.Text.Trim();
		Draft.IgnoredProcesses = _ignoredProcesses.Text
			.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.OrdinalIgnoreCase)
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
}
