using StageManager.Desktop.Lifecycle;
using StageManager.Services;
using StageManager.Settings;
using System.Diagnostics;

namespace StageManager.Desktop;

internal sealed class SettingsForm : Form
{
	private const string ApplicationIdColumn = "ApplicationId";
	private const string RunningColumn = "Running";
	private const string IgnoreColumn = "Ignore";
	private const string PreviewModeColumn = "PreviewMode";

	private readonly IReadOnlyList<PrototypeApplicationChoice> _applicationChoices;
	private readonly IReadOnlyList<DisplayOption> _displayOptions;

	private TabControl _tabs = null!;
	private TabPage _generalPage = null!;
	private TabPage _displaysPage = null!;
	private TabPage _previewsPage = null!;
	private TabPage _applicationRulesPage = null!;
	private TabPage _shortcutsPage = null!;
	private TabPage _diagnosticsPage = null!;

	private ComboBox _stageMode = null!;
	private ComboBox _appWindowsMode = null!;
	private TrackBar _cardSizeSlider = null!;
	private Label _cardSizeValue = null!;
	private CheckBox _animationsEnabled = null!;
	private CheckBox _idleAutoHideEnabled = null!;
	private NumericUpDown _idleSeconds = null!;
	private CheckBox _startWithWindows = null!;

	private ComboBox _sidebarDisplayMode = null!;
	private ComboBox _specificDisplay = null!;
	private Label _displayIdentity = null!;
	private ComboBox _fullScreenSidebarMode = null!;

	private ComboBox _renderProfile = null!;
	private NumericUpDown _previewRefreshMinutes = null!;
	private CheckBox _pausePreviewRefreshWhenHidden = null!;

	private DataGridView _applicationRules = null!;
	private TextBox _manualApplicationId = null!;

	private CheckBox _hotkeysEnabled = null!;
	private TextBox _toggleSidebarHotkey = null!;
	private TextBox _previousStageHotkey = null!;
	private TextBox _nextStageHotkey = null!;
	private TextBox _toggleWindowInStageHotkey = null!;

	private Button _exportDiagnostics = null!;
	private Label _diagnosticStatus = null!;

	public SettingsForm(AppSettings draft, IReadOnlyList<PrototypeApplicationChoice>? applicationChoices = null)
	{
		ArgumentNullException.ThrowIfNull(draft);
		Draft = draft;
		_applicationChoices = applicationChoices ?? Array.Empty<PrototypeApplicationChoice>();
		_displayOptions = LoadDisplayOptions(draft.SidebarDisplayId);

		InitializeWindow();
		BuildInterface();
		PopulateApplicationRules(draft);
		UpdateControlStates();
	}

	public AppSettings Draft { get; }

	private void InitializeWindow()
	{
		Text = $"{AppVersionInfo.DisplayName} Settings";
		AccessibleName = "Stage Manager settings";
		AccessibleDescription = "Configure Stage Manager behavior, displays, previews, application rules, shortcuts, and diagnostics.";
		StartPosition = FormStartPosition.CenterScreen;
		FormBorderStyle = FormBorderStyle.Sizable;
		ShowInTaskbar = false;
		MaximizeBox = true;
		MinimizeBox = false;
		AutoScaleMode = AutoScaleMode.Dpi;
		AutoScaleDimensions = new SizeF(96f, 96f);
		MinimumSize = new Size(720, 590);
		var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
		ClientSize = new Size(
			Math.Min(900, Math.Max(680, workingArea.Width - 120)),
			Math.Min(740, Math.Max(530, workingArea.Height - 120)));
		BackColor = Color.FromArgb(24, 26, 31);
		ForeColor = Color.FromArgb(244, 246, 250);
		Font = new Font("Segoe UI", 10f);
	}

	private void BuildInterface()
	{
		SuspendLayout();
		var root = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 3,
			Padding = new Padding(16, 12, 16, 12),
			BackColor = BackColor
		};
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

		var heading = new Label
		{
			Text = AppVersionInfo.DisplayName,
			Font = new Font("Segoe UI", 17f, FontStyle.Bold),
			AutoSize = true,
			Margin = new Padding(4, 0, 0, 8),
			AccessibleName = "Application version",
			AccessibleDescription = "The installed Stage Manager version."
		};
		root.Controls.Add(heading, 0, 0);

		_tabs = new TabControl
		{
			Dock = DockStyle.Fill,
			Padding = new Point(14, 6),
			AccessibleName = "Settings categories",
			AccessibleDescription = "Six pages of Stage Manager settings."
		};
		_generalPage = BuildGeneralPage();
		_displaysPage = BuildDisplaysPage();
		_previewsPage = BuildPreviewsPage();
		_applicationRulesPage = BuildApplicationRulesPage();
		_shortcutsPage = BuildShortcutsPage();
		_diagnosticsPage = BuildDiagnosticsPage();
		_tabs.TabPages.AddRange(new[]
		{
			_generalPage,
			_displaysPage,
			_previewsPage,
			_applicationRulesPage,
			_shortcutsPage,
			_diagnosticsPage
		});
		root.Controls.Add(_tabs, 0, 1);

		var buttonBar = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 1,
			Margin = new Padding(0, 10, 0, 0)
		};
		buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		var resetButton = CreateButton(
			"Reset defaults",
			"Reset all editable settings to their recommended defaults.",
			new Size(132, 36));
		resetButton.Anchor = AnchorStyles.Left;
		resetButton.Click += (_, _) => ResetDefaults();
		buttonBar.Controls.Add(resetButton, 0, 0);

		var actions = new FlowLayoutPanel
		{
			AutoSize = true,
			FlowDirection = FlowDirection.LeftToRight,
			WrapContents = false,
			Anchor = AnchorStyles.Right
		};
		var cancelButton = CreateButton(
			"Cancel",
			"Close settings without applying changes.",
			new Size(96, 36));
		cancelButton.DialogResult = DialogResult.Cancel;
		var saveButton = CreateButton(
			"Save",
			"Validate and apply all settings.",
			new Size(96, 36));
		saveButton.Click += SaveButton_Click;
		actions.Controls.Add(cancelButton);
		actions.Controls.Add(saveButton);
		buttonBar.Controls.Add(actions, 1, 0);
		root.Controls.Add(buttonBar, 0, 2);

		Controls.Add(root);
		AcceptButton = saveButton;
		CancelButton = cancelButton;
		ResumeLayout(performLayout: true);
	}

	private TabPage BuildGeneralPage()
	{
		var page = CreatePage(
			"General",
			"General stage behavior, card size, animation, idle hiding, and startup settings.");
		var grid = CreateSettingsGrid();
		AddIntroduction(grid, "Choose how stages behave and how the sidebar feels during everyday use.");

		_stageMode = CreateChoiceCombo(
			new ChoiceItem("Coexist — leave other windows visible", StageMode.Coexist),
			new ChoiceItem("Focus — minimize other managed stages", StageMode.Focus));
		SelectChoice(_stageMode, Draft.StageMode);
		ConfigureAccessible(_stageMode, "Stage mode", "Choose whether other managed stages remain visible or are minimized.");
		AddSettingRow(grid, "Stage mode", _stageMode);

		_appWindowsMode = CreateChoiceCombo(
			new ChoiceItem("All at once — restore every window", AppWindowsMode.AllAtOnce),
			new ChoiceItem("One at a time — cycle application windows", AppWindowsMode.OneAtATime));
		SelectChoice(_appWindowsMode, Draft.AppWindowsMode);
		ConfigureAccessible(_appWindowsMode, "Application windows mode", "Choose whether a stage restores all windows or cycles through them one at a time.");
		AddSettingRow(grid, "Application windows", _appWindowsMode);

		_cardSizeSlider = new TrackBar
		{
			Minimum = 55,
			Maximum = 125,
			TickFrequency = 5,
			SmallChange = 5,
			LargeChange = 10,
			Value = Math.Clamp((int)Math.Round(Draft.CardScale * 100), 55, 125),
			Dock = DockStyle.Fill,
			Margin = new Padding(0),
			AccessibleName = "Card size",
			AccessibleDescription = "Adjust card size from 55 to 125 percent."
		};
		_cardSizeValue = new Label
		{
			AutoSize = false,
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter,
			Font = new Font(Font, FontStyle.Bold),
			AccessibleName = "Current card size"
		};
		_cardSizeSlider.ValueChanged += (_, _) => UpdateCardSizeLabel();
		var cardSizePanel = new TableLayoutPanel
		{
			ColumnCount = 2,
			RowCount = 1,
			Dock = DockStyle.Fill,
			Height = 52,
			Margin = new Padding(0, 2, 0, 4)
		};
		cardSizePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		cardSizePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
		cardSizePanel.Controls.Add(_cardSizeSlider, 0, 0);
		cardSizePanel.Controls.Add(_cardSizeValue, 1, 0);
		AddSettingRow(grid, "Card size", cardSizePanel);
		UpdateCardSizeLabel();

		_animationsEnabled = CreateCheckBox(
			"Use card and sidebar animations",
			Draft.AnimationsEnabled,
			"Animations",
			"Enable motion while showing, hiding, and selecting cards.");
		AddSettingRow(grid, "Motion", _animationsEnabled);

		_idleAutoHideEnabled = CreateCheckBox(
			"Hide after no nearby pointer activity",
			Draft.IdleAutoHideEnabled,
			"Idle auto-hide",
			"Automatically hide the sidebar after the configured idle delay.");
		_idleSeconds = new NumericUpDown
		{
			Minimum = 15,
			Maximum = 600,
			Increment = 15,
			Value = Math.Clamp(Draft.IdleAutoHideSeconds, 15, 600),
			Width = 86,
			AccessibleName = "Idle delay in seconds",
			AccessibleDescription = "The number of seconds before the sidebar hides."
		};
		var idlePanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			AutoSize = true,
			WrapContents = true,
			FlowDirection = FlowDirection.LeftToRight,
			Margin = Padding.Empty
		};
		idlePanel.Controls.Add(_idleAutoHideEnabled);
		idlePanel.Controls.Add(_idleSeconds);
		idlePanel.Controls.Add(new Label
		{
			Text = "seconds",
			AutoSize = true,
			Margin = new Padding(2, 7, 0, 0)
		});
		_idleAutoHideEnabled.CheckedChanged += (_, _) => UpdateControlStates();
		AddSettingRow(grid, "Idle behavior", idlePanel);

		_startWithWindows = CreateCheckBox(
			"Start Stage Manager when I sign in",
			Draft.StartWithWindows,
			"Start with Windows",
			"Launch Stage Manager automatically after Windows sign-in.");
		AddSettingRow(grid, "Startup", _startWithWindows);
		AddFiller(grid);
		page.Controls.Add(grid);
		return page;
	}

	private TabPage BuildDisplaysPage()
	{
		var page = CreatePage(
			"Displays",
			"Sidebar monitor placement and behavior over full-screen or maximized windows.");
		var grid = CreateSettingsGrid();
		AddIntroduction(grid, "The monitor list uses stable Windows display identities, so changing the primary monitor does not silently move a specifically assigned sidebar.");

		_sidebarDisplayMode = CreateChoiceCombo(
			new ChoiceItem("Physical leftmost display", SidebarDisplayMode.Leftmost),
			new ChoiceItem("Windows primary display", SidebarDisplayMode.Primary),
			new ChoiceItem("A specific display", SidebarDisplayMode.Specific));
		SelectChoice(_sidebarDisplayMode, Draft.SidebarDisplayMode);
		ConfigureAccessible(_sidebarDisplayMode, "Sidebar display mode", "Choose how Stage Manager selects the monitor that contains the sidebar.");
		_sidebarDisplayMode.SelectedIndexChanged += (_, _) => UpdateControlStates();
		AddSettingRow(grid, "Place sidebar on", _sidebarDisplayMode);

		_specificDisplay = new ComboBox
		{
			DropDownStyle = ComboBoxStyle.DropDownList,
			Dock = DockStyle.Fill,
			IntegralHeight = true
		};
		foreach (var option in _displayOptions)
			_specificDisplay.Items.Add(option);
		var selectedDisplayIndex = _displayOptions
			.Select((option, index) => (option, index))
			.FirstOrDefault(item => string.Equals(item.option.StableId, Draft.SidebarDisplayId, StringComparison.OrdinalIgnoreCase))
			.index;
		if (_specificDisplay.Items.Count > 0)
			_specificDisplay.SelectedIndex = Math.Clamp(selectedDisplayIndex, 0, _specificDisplay.Items.Count - 1);
		ConfigureAccessible(_specificDisplay, "Specific sidebar display", "Choose an active monitor by its stable Windows identity.");
		_specificDisplay.SelectedIndexChanged += (_, _) => UpdateDisplayIdentityLabel();
		AddSettingRow(grid, "Specific display", _specificDisplay);

		_displayIdentity = new Label
		{
			AutoSize = false,
			Dock = DockStyle.Fill,
			Height = 52,
			AutoEllipsis = true,
			ForeColor = Color.FromArgb(175, 181, 194),
			TextAlign = ContentAlignment.MiddleLeft,
			AccessibleName = "Selected stable display identity",
			AccessibleDescription = "The persistent display identifier saved in settings."
		};
		AddSettingRow(grid, "Stable identity", _displayIdentity);
		UpdateDisplayIdentityLabel();

		_fullScreenSidebarMode = CreateChoiceCombo(
			new ChoiceItem("Reveal from the left edge", FullScreenSidebarMode.EdgeReveal),
			new ChoiceItem("Stay hidden over full-screen windows", FullScreenSidebarMode.Disabled));
		SelectChoice(_fullScreenSidebarMode, Draft.FullScreenSidebarMode);
		ConfigureAccessible(_fullScreenSidebarMode, "Full-screen sidebar behavior", "Choose whether the left screen edge can reveal the sidebar over full-screen and maximized windows.");
		AddSettingRow(grid, "Full-screen windows", _fullScreenSidebarMode);
		AddFiller(grid);
		page.Controls.Add(grid);
		return page;
	}

	private TabPage BuildPreviewsPage()
	{
		var page = CreatePage(
			"Previews",
			"Rendering profile and periodic preview refresh behavior.");
		var grid = CreateSettingsGrid();
		AddIntroduction(grid, "Low Memory is recommended. Per-application exceptions such as Icon Only are configured on the Application rules page.");

		_renderProfile = CreateChoiceCombo(
			new ChoiceItem("Low Memory — software rendering", RenderProfile.LowMemory),
			new ChoiceItem("Balanced — prefer an efficient GPU", RenderProfile.Balanced),
			new ChoiceItem("Performance — hardware GPU", RenderProfile.Performance));
		SelectChoice(_renderProfile, Draft.RenderProfile);
		ConfigureAccessible(_renderProfile, "Render profile", "Choose the performance and memory profile used to draw Stage Manager cards.");
		AddSettingRow(grid, "Render profile", _renderProfile);

		_previewRefreshMinutes = new NumericUpDown
		{
			Minimum = 1,
			Maximum = 60,
			Increment = 1,
			Value = Math.Clamp(Draft.PreviewRefreshMinutes, 1, 60),
			Width = 110,
			AccessibleName = "Preview refresh interval",
			AccessibleDescription = "Minutes between automatic static preview refreshes."
		};
		var refreshPanel = new FlowLayoutPanel
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.LeftToRight,
			Margin = Padding.Empty
		};
		refreshPanel.Controls.Add(_previewRefreshMinutes);
		refreshPanel.Controls.Add(new Label
		{
			Text = "minutes",
			AutoSize = true,
			Margin = new Padding(4, 7, 0, 0)
		});
		AddSettingRow(grid, "Refresh static previews", refreshPanel);

		_pausePreviewRefreshWhenHidden = CreateCheckBox(
			"Pause scheduled refreshes while the sidebar is hidden",
			Draft.PausePreviewRefreshWhenHidden,
			"Pause hidden preview refresh",
			"Avoid background preview work while the sidebar is hidden.");
		AddSettingRow(grid, "When hidden", _pausePreviewRefreshWhenHidden);
		AddFiller(grid);
		page.Controls.Add(grid);
		return page;
	}

	private TabPage BuildApplicationRulesPage()
	{
		var page = CreatePage(
			"Application rules",
			"Per-application ignore and preview settings, including applications that are not currently running.");
		var root = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 3,
			Padding = new Padding(14),
			BackColor = page.BackColor
		};
		root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		root.Controls.Add(CreateWrappedLabel(
			"Running applications are merged with saved rules. Ignore removes an application from the sidebar; Icon Only is recommended for programs that react badly to screenshots."), 0, 0);

		_applicationRules = new DataGridView
		{
			Dock = DockStyle.Fill,
			AllowUserToAddRows = false,
			AllowUserToDeleteRows = false,
			AllowUserToResizeRows = false,
			AutoGenerateColumns = false,
			BackgroundColor = Color.FromArgb(28, 31, 37),
			BorderStyle = BorderStyle.FixedSingle,
			CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
			ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
			EnableHeadersVisualStyles = false,
			GridColor = Color.FromArgb(55, 59, 68),
			RowHeadersVisible = false,
			SelectionMode = DataGridViewSelectionMode.FullRowSelect,
			MultiSelect = true,
			EditMode = DataGridViewEditMode.EditOnEnter,
			AccessibleName = "Application rules table",
			AccessibleDescription = "Edit whether each application is ignored and how its preview is generated."
		};
		_applicationRules.DefaultCellStyle.BackColor = Color.FromArgb(34, 37, 44);
		_applicationRules.DefaultCellStyle.ForeColor = ForeColor;
		_applicationRules.DefaultCellStyle.SelectionBackColor = Color.FromArgb(55, 84, 126);
		_applicationRules.DefaultCellStyle.SelectionForeColor = Color.White;
		_applicationRules.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(42, 46, 54);
		_applicationRules.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
		_applicationRules.Columns.Add(new DataGridViewTextBoxColumn
		{
			Name = ApplicationIdColumn,
			HeaderText = "Application / process",
			ReadOnly = true,
			AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
			FillWeight = 52,
			MinimumWidth = 190
		});
		_applicationRules.Columns.Add(new DataGridViewTextBoxColumn
		{
			Name = RunningColumn,
			HeaderText = "Status",
			ReadOnly = true,
			AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
			FillWeight = 22,
			MinimumWidth = 105
		});
		_applicationRules.Columns.Add(new DataGridViewCheckBoxColumn
		{
			Name = IgnoreColumn,
			HeaderText = "Ignore",
			AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader,
			MinimumWidth = 70
		});
		_applicationRules.Columns.Add(new DataGridViewComboBoxColumn
		{
			Name = PreviewModeColumn,
			HeaderText = "Preview",
			DataSource = Enum.GetValues<PreviewMode>(),
			FlatStyle = FlatStyle.Flat,
			AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
			FillWeight = 26,
			MinimumWidth = 115
		});
		_applicationRules.DataError += (_, args) => args.ThrowException = false;
		root.Controls.Add(_applicationRules, 0, 1);

		var manualRow = new TableLayoutPanel
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			RowCount = 2,
			Margin = new Padding(0, 10, 0, 0)
		};
		manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		var manualLabel = new Label
		{
			Text = "Add a process name manually (for example: WeChat or abaqus):",
			AutoSize = true,
			Dock = DockStyle.Fill,
			Margin = new Padding(0, 0, 0, 4)
		};
		manualRow.Controls.Add(manualLabel, 0, 0);
		manualRow.SetColumnSpan(manualLabel, 3);
		_manualApplicationId = new TextBox
		{
			Dock = DockStyle.Fill,
			AccessibleName = "Manual application process name",
			AccessibleDescription = "Type one or more process names separated by commas, semicolons, or new lines."
		};
		_manualApplicationId.KeyDown += (_, args) =>
		{
			if (args.KeyCode != Keys.Enter)
				return;
			AddManualApplicationRules(showEmptyMessage: false);
			args.Handled = true;
			args.SuppressKeyPress = true;
		};
		var addButton = CreateButton("Add", "Add the typed process name to the application rules table.", new Size(82, 32));
		addButton.Click += (_, _) => AddManualApplicationRules(showEmptyMessage: true);
		var removeButton = CreateButton("Remove", "Remove the selected entries from the application rules table.", new Size(92, 32));
		removeButton.Click += (_, _) => RemoveSelectedApplicationRules();
		manualRow.Controls.Add(_manualApplicationId, 0, 1);
		manualRow.Controls.Add(addButton, 1, 1);
		manualRow.Controls.Add(removeButton, 2, 1);
		root.Controls.Add(manualRow, 0, 2);
		page.Controls.Add(root);
		return page;
	}

	private TabPage BuildShortcutsPage()
	{
		var page = CreatePage(
			"Shortcuts",
			"Global keyboard shortcuts for showing the sidebar and navigating stages.");
		var grid = CreateSettingsGrid();
		AddIntroduction(grid, "Use names such as Win, Alt, Ctrl, Shift and a key separated by plus signs. Each enabled shortcut must be valid and unique.");

		_hotkeysEnabled = CreateCheckBox(
			"Enable global keyboard shortcuts",
			Draft.HotkeysEnabled,
			"Enable global shortcuts",
			"Register the four shortcuts below with Windows.");
		_hotkeysEnabled.CheckedChanged += (_, _) => UpdateControlStates();
		AddSettingRow(grid, "Availability", _hotkeysEnabled);

		_toggleSidebarHotkey = CreateShortcutTextBox(
			Draft.ToggleSidebarHotkey,
			"Show or hide sidebar shortcut",
			"Global shortcut that toggles the Stage Manager sidebar.");
		AddSettingRow(grid, "Show / hide sidebar", _toggleSidebarHotkey);

		_previousStageHotkey = CreateShortcutTextBox(
			Draft.PreviousStageHotkey,
			"Previous stage shortcut",
			"Global shortcut that activates the previous stage.");
		AddSettingRow(grid, "Previous stage", _previousStageHotkey);

		_nextStageHotkey = CreateShortcutTextBox(
			Draft.NextStageHotkey,
			"Next stage shortcut",
			"Global shortcut that activates the next stage.");
		AddSettingRow(grid, "Next stage", _nextStageHotkey);

		_toggleWindowInStageHotkey = CreateShortcutTextBox(
			Draft.ToggleWindowInStageHotkey,
			"Add or remove window from stage shortcut",
			"Global shortcut that adds the current window to, or removes it from, the current stage.");
		AddSettingRow(grid, "Add / remove current window", _toggleWindowInStageHotkey);
		AddFiller(grid);
		page.Controls.Add(grid);
		return page;
	}

	private TabPage BuildDiagnosticsPage()
	{
		var page = CreatePage(
			"Diagnostics",
			"Open local logs or create a privacy-redacted diagnostic archive.");
		var grid = CreateSettingsGrid();
		AddIntroduction(grid, "Diagnostic bundles are created locally. By default, window titles, user names, and personal paths are redacted, and nothing is uploaded.");

		var openLogs = CreateButton("Open log directory", "Open the local Stage Manager log directory in File Explorer.", new Size(180, 36));
		openLogs.Click += (_, _) => OpenLogDirectory();
		AddSettingRow(grid, "Logs", openLogs);

		_exportDiagnostics = CreateButton("Export diagnostic bundle...", "Create a local ZIP archive with redacted settings, logs, and system information.", new Size(220, 36));
		_exportDiagnostics.Click += async (_, _) => await ExportDiagnosticsAsync();
		AddSettingRow(grid, "Privacy-safe export", _exportDiagnostics);

		_diagnosticStatus = new Label
		{
			AutoSize = true,
			MaximumSize = new Size(600, 0),
			ForeColor = Color.FromArgb(175, 181, 194),
			AccessibleName = "Diagnostic export status",
			AccessibleDescription = "Reports the result and local path of the most recent diagnostic export."
		};
		AddSettingRow(grid, "Status", _diagnosticStatus);
		AddFiller(grid);
		page.Controls.Add(grid);
		return page;
	}

	private void SaveButton_Click(object? sender, EventArgs e)
	{
		if (!ValidateShortcuts())
			return;

		AddManualApplicationRules(showEmptyMessage: false);
		var displayMode = GetSelectedChoice(_sidebarDisplayMode, SidebarDisplayMode.Leftmost);
		var selectedDisplay = _specificDisplay.SelectedItem as DisplayOption;
		if (displayMode == SidebarDisplayMode.Specific && selectedDisplay is null)
		{
			_tabs.SelectedTab = _displaysPage;
			_specificDisplay.Focus();
			MessageBox.Show(
				this,
				"Select a display before using the specific display mode.",
				"Display required",
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning);
			return;
		}

		Draft.SchemaVersion = SettingsService.CurrentSchemaVersion;
		Draft.StageMode = GetSelectedChoice(_stageMode, StageMode.Coexist);
		Draft.AppWindowsMode = GetSelectedChoice(_appWindowsMode, AppWindowsMode.AllAtOnce);
		Draft.CardScale = _cardSizeSlider.Value / 100d;
		Draft.AnimationsEnabled = _animationsEnabled.Checked;
		Draft.IdleAutoHideEnabled = _idleAutoHideEnabled.Checked;
		Draft.IdleAutoHideSeconds = (int)_idleSeconds.Value;
		Draft.StartWithWindows = _startWithWindows.Checked;

		Draft.SidebarDisplayMode = displayMode;
		Draft.SidebarDisplayId = selectedDisplay?.StableId;
		Draft.FullScreenSidebarMode = GetSelectedChoice(_fullScreenSidebarMode, FullScreenSidebarMode.EdgeReveal);

		Draft.RenderProfile = GetSelectedChoice(_renderProfile, RenderProfile.LowMemory);
		Draft.LowMemoryRendering = Draft.RenderProfile == RenderProfile.LowMemory;
		Draft.PreviewRefreshMinutes = (int)_previewRefreshMinutes.Value;
		Draft.PausePreviewRefreshWhenHidden = _pausePreviewRefreshWhenHidden.Checked;

		Draft.HotkeysEnabled = _hotkeysEnabled.Checked;
		Draft.ToggleSidebarHotkey = _toggleSidebarHotkey.Text.Trim();
		Draft.PreviousStageHotkey = _previousStageHotkey.Text.Trim();
		Draft.NextStageHotkey = _nextStageHotkey.Text.Trim();
		Draft.ToggleWindowInStageHotkey = _toggleWindowInStageHotkey.Text.Trim();

		var rules = ReadApplicationRules();
		Draft.ApplicationRules = rules;
		Draft.IgnoredProcesses = rules
			.Where(rule => rule.Ignore)
			.Select(rule => rule.ApplicationId)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
			.ToList();

		DialogResult = DialogResult.OK;
		Close();
	}

	private bool ValidateShortcuts()
	{
		var shortcuts = new[]
		{
			new ShortcutEntry("Show / hide sidebar", _toggleSidebarHotkey),
			new ShortcutEntry("Previous stage", _previousStageHotkey),
			new ShortcutEntry("Next stage", _nextStageHotkey),
			new ShortcutEntry("Add / remove current window", _toggleWindowInStageHotkey)
		};
		var combinations = new Dictionary<(uint Modifiers, uint Key), string>();
		foreach (var shortcut in shortcuts)
		{
			var gesture = shortcut.TextBox.Text.Trim();
			if (!HotkeyGestureParser.TryParse(gesture, out var modifiers, out var virtualKey))
			{
				ShowShortcutValidationError(shortcut, $"'{gesture}' is not a valid shortcut for {shortcut.Name}.");
				return false;
			}

			var key = (modifiers, virtualKey);
			if (combinations.TryGetValue(key, out var existingName))
			{
				ShowShortcutValidationError(shortcut, $"{shortcut.Name} uses the same shortcut as {existingName}. Choose a unique shortcut.");
				return false;
			}
			combinations.Add(key, shortcut.Name);
		}
		return true;
	}

	private void ShowShortcutValidationError(ShortcutEntry shortcut, string message)
	{
		_tabs.SelectedTab = _shortcutsPage;
		shortcut.TextBox.Focus();
		shortcut.TextBox.SelectAll();
		MessageBox.Show(this, message, "Invalid shortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
	}

	private void PopulateApplicationRules(AppSettings draft)
	{
		var entries = new Dictionary<string, ApplicationRuleSeed>(StringComparer.OrdinalIgnoreCase);
		foreach (var choice in _applicationChoices.Where(choice => !string.IsNullOrWhiteSpace(choice.ProcessName)))
		{
			entries[choice.ProcessName.Trim()] = new ApplicationRuleSeed(
				choice.ProcessName.Trim(),
				choice.WindowCount,
				Ignore: false,
				PreviewMode.Auto);
		}

		foreach (var rule in (draft.ApplicationRules ?? new List<ApplicationRule>())
			.Where(rule => !string.IsNullOrWhiteSpace(rule.ApplicationId)))
		{
			var applicationId = rule.ApplicationId.Trim();
			if (entries.TryGetValue(applicationId, out var existing))
				entries[applicationId] = existing with { Ignore = rule.Ignore, PreviewMode = rule.PreviewMode };
			else
				entries[applicationId] = new ApplicationRuleSeed(applicationId, 0, rule.Ignore, rule.PreviewMode);
		}

		foreach (var ignoredProcess in (draft.IgnoredProcesses ?? new List<string>())
			.Where(value => !string.IsNullOrWhiteSpace(value)))
		{
			var applicationId = ignoredProcess.Trim();
			if (entries.TryGetValue(applicationId, out var existing))
				entries[applicationId] = existing with { Ignore = true };
			else
				entries[applicationId] = new ApplicationRuleSeed(applicationId, 0, Ignore: true, PreviewMode.Auto);
		}

		foreach (var entry in entries.Values.OrderBy(value => value.ApplicationId, StringComparer.CurrentCultureIgnoreCase))
			AddApplicationRuleRow(entry.ApplicationId, entry.WindowCount, entry.Ignore, entry.PreviewMode);
	}

	private void AddManualApplicationRules(bool showEmptyMessage)
	{
		var applicationIds = _manualApplicationId.Text
			.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(NormalizeApplicationId)
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (applicationIds.Length == 0)
		{
			if (showEmptyMessage)
			{
				MessageBox.Show(
					this,
					"Enter a process name first.",
					"Process name required",
					MessageBoxButtons.OK,
					MessageBoxIcon.Information);
				_manualApplicationId.Focus();
			}
			return;
		}

		foreach (var applicationId in applicationIds)
		{
			var existingRow = FindApplicationRuleRow(applicationId);
			if (existingRow is null)
				existingRow = AddApplicationRuleRow(applicationId, 0, ignore: false, PreviewMode.Auto);
			existingRow.Selected = true;
		}
		_manualApplicationId.Clear();
	}

	private static string NormalizeApplicationId(string value)
	{
		var applicationId = value.Trim().Trim('"');
		if (applicationId.Contains(Path.DirectorySeparatorChar) || applicationId.Contains(Path.AltDirectorySeparatorChar))
			applicationId = Path.GetFileName(applicationId);
		if (applicationId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			applicationId = applicationId[..^4];
		return applicationId.Trim();
	}

	private DataGridViewRow AddApplicationRuleRow(
		string applicationId,
		int windowCount,
		bool ignore,
		PreviewMode previewMode)
	{
		var status = windowCount switch
		{
			<= 0 => "Not running",
			1 => "1 window",
			_ => $"{windowCount} windows"
		};
		var index = _applicationRules.Rows.Add(applicationId, status, ignore, previewMode);
		var row = _applicationRules.Rows[index];
		row.Cells[ApplicationIdColumn].ToolTipText = $"Rules for {applicationId}. {status}.";
		return row;
	}

	private DataGridViewRow? FindApplicationRuleRow(string applicationId)
	{
		return _applicationRules.Rows
			.Cast<DataGridViewRow>()
			.FirstOrDefault(row => string.Equals(
				Convert.ToString(row.Cells[ApplicationIdColumn].Value),
				applicationId,
				StringComparison.OrdinalIgnoreCase));
	}

	private void RemoveSelectedApplicationRules()
	{
		var selectedRows = _applicationRules.SelectedRows
			.Cast<DataGridViewRow>()
			.OrderByDescending(row => row.Index)
			.ToArray();
		foreach (var row in selectedRows)
			_applicationRules.Rows.Remove(row);
	}

	private List<ApplicationRule> ReadApplicationRules()
	{
		var rules = new Dictionary<string, ApplicationRule>(StringComparer.OrdinalIgnoreCase);
		foreach (DataGridViewRow row in _applicationRules.Rows)
		{
			var applicationId = NormalizeApplicationId(Convert.ToString(row.Cells[ApplicationIdColumn].Value) ?? string.Empty);
			if (string.IsNullOrWhiteSpace(applicationId))
				continue;
			var ignore = row.Cells[IgnoreColumn].Value is true;
			var previewMode = row.Cells[PreviewModeColumn].Value switch
			{
				PreviewMode value => value,
				string value when Enum.TryParse<PreviewMode>(value, ignoreCase: true, out var parsed) => parsed,
				_ => PreviewMode.Auto
			};

			// Neutral running rows are discovery choices, not persistent rules.
			if (!ignore && previewMode == PreviewMode.Auto)
				continue;
			rules[applicationId] = new ApplicationRule
			{
				ApplicationId = applicationId,
				Ignore = ignore,
				PreviewMode = previewMode
			};
		}
		return rules.Values
			.OrderBy(rule => rule.ApplicationId, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private void ResetDefaults()
	{
		SelectChoice(_stageMode, StageMode.Coexist);
		SelectChoice(_appWindowsMode, AppWindowsMode.AllAtOnce);
		_cardSizeSlider.Value = 60;
		_animationsEnabled.Checked = true;
		_idleAutoHideEnabled.Checked = true;
		_idleSeconds.Value = 60;
		_startWithWindows.Checked = true;

		SelectChoice(_sidebarDisplayMode, SidebarDisplayMode.Leftmost);
		if (_specificDisplay.Items.Count > 0)
			_specificDisplay.SelectedIndex = 0;
		SelectChoice(_fullScreenSidebarMode, FullScreenSidebarMode.EdgeReveal);

		SelectChoice(_renderProfile, RenderProfile.LowMemory);
		_previewRefreshMinutes.Value = 5;
		_pausePreviewRefreshWhenHidden.Checked = true;

		_hotkeysEnabled.Checked = true;
		_toggleSidebarHotkey.Text = "Win+Alt+S";
		_previousStageHotkey.Text = "Win+Alt+[";
		_nextStageHotkey.Text = "Win+Alt+]";
		_toggleWindowInStageHotkey.Text = "Win+Alt+G";

		foreach (DataGridViewRow row in _applicationRules.Rows)
		{
			row.Cells[IgnoreColumn].Value = false;
			row.Cells[PreviewModeColumn].Value = PreviewMode.Auto;
		}
		_manualApplicationId.Clear();
		UpdateControlStates();
	}

	private void UpdateControlStates()
	{
		_idleSeconds.Enabled = _idleAutoHideEnabled.Checked;
		var specificDisplayEnabled = GetSelectedChoice(_sidebarDisplayMode, SidebarDisplayMode.Leftmost) == SidebarDisplayMode.Specific;
		_specificDisplay.Enabled = specificDisplayEnabled && _specificDisplay.Items.Count > 0;
		_displayIdentity.Enabled = specificDisplayEnabled;
		foreach (var textBox in new[]
		{
			_toggleSidebarHotkey,
			_previousStageHotkey,
			_nextStageHotkey,
			_toggleWindowInStageHotkey
		})
			textBox.Enabled = _hotkeysEnabled.Checked;
	}

	private void UpdateCardSizeLabel()
	{
		_cardSizeValue.Text = $"{_cardSizeSlider.Value}%";
		_cardSizeValue.AccessibleDescription = $"Cards are {_cardSizeSlider.Value} percent of the base size.";
	}

	private void UpdateDisplayIdentityLabel()
	{
		if (_specificDisplay.SelectedItem is not DisplayOption option)
		{
			_displayIdentity.Text = "No active display was found.";
			_displayIdentity.AccessibleDescription = _displayIdentity.Text;
			return;
		}

		_displayIdentity.Text = option.IsAvailable
			? option.StableId
			: $"{option.StableId} (currently unavailable)";
		_displayIdentity.AccessibleDescription = $"Selected stable display identifier: {option.StableId}";
	}

	private async Task ExportDiagnosticsAsync()
	{
		using var dialog = new SaveFileDialog
		{
			Title = "Export Stage Manager diagnostics",
			Filter = "ZIP archive (*.zip)|*.zip",
			AddExtension = true,
			DefaultExt = "zip",
			RestoreDirectory = true,
			FileName = $"Stage_Manager_Lai-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
			return;

		_exportDiagnostics.Enabled = false;
		_diagnosticStatus.Text = "Creating a local redacted diagnostic bundle...";
		try
		{
			var exporter = new DiagnosticBundleExporter();
			var result = await exporter.ExportAsync(dialog.FileName);
			_diagnosticStatus.Text = $"Exported locally: {result.ArchivePath}";
			_diagnosticStatus.AccessibleDescription = _diagnosticStatus.Text;
			MessageBox.Show(
				this,
				$"The redacted diagnostic bundle was saved locally.\n\n{result.ArchivePath}",
				"Diagnostics exported",
				MessageBoxButtons.OK,
				MessageBoxIcon.Information);
		}
		catch (Exception exception)
		{
			_diagnosticStatus.Text = $"Export failed: {exception.Message}";
			_diagnosticStatus.AccessibleDescription = _diagnosticStatus.Text;
			MessageBox.Show(this, exception.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			_exportDiagnostics.Enabled = true;
		}
	}

	private void OpenLogDirectory()
	{
		try
		{
			var directory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Stage_Manager_Lai",
				"Logs");
			Directory.CreateDirectory(directory);
			Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
			_diagnosticStatus.Text = $"Opened: {directory}";
			_diagnosticStatus.AccessibleDescription = _diagnosticStatus.Text;
		}
		catch (Exception exception)
		{
			MessageBox.Show(this, exception.Message, "Unable to open logs", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private static IReadOnlyList<DisplayOption> LoadDisplayOptions(string? selectedStableId)
	{
		var options = new List<DisplayOption>();
		try
		{
			foreach (var display in new DisplayIdentityService().GetActiveDisplays())
			{
				var primary = display.IsPrimary ? " (Primary)" : string.Empty;
				var label = $"{display.FriendlyName}{primary} — {display.Bounds.Width}×{display.Bounds.Height}";
				options.Add(new DisplayOption(display.StableId, label, IsAvailable: true));
			}
		}
		catch (Exception exception)
		{
			Debug.WriteLine($"Display enumeration failed: {exception}");
		}

		if (!string.IsNullOrWhiteSpace(selectedStableId) &&
			!options.Any(option => string.Equals(option.StableId, selectedStableId, StringComparison.OrdinalIgnoreCase)))
		{
			options.Add(new DisplayOption(
				selectedStableId.Trim(),
				"Previously selected display (currently unavailable)",
				IsAvailable: false));
		}

		return options
			.OrderByDescending(option => option.IsAvailable)
			.ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}

	private TabPage CreatePage(string title, string description)
	{
		return new TabPage
		{
			Text = title,
			BackColor = BackColor,
			ForeColor = ForeColor,
			Padding = new Padding(4),
			AccessibleName = $"{title} settings",
			AccessibleDescription = description,
			UseVisualStyleBackColor = false
		};
	}

	private TableLayoutPanel CreateSettingsGrid()
	{
		var grid = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			AutoScroll = true,
			ColumnCount = 2,
			RowCount = 0,
			Padding = new Padding(18, 14, 18, 14),
			BackColor = BackColor
		};
		grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
		grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		return grid;
	}

	private static void AddIntroduction(TableLayoutPanel grid, string text)
	{
		var row = grid.RowCount++;
		grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		var label = CreateWrappedLabel(text);
		label.ForeColor = Color.FromArgb(188, 194, 207);
		label.Margin = new Padding(0, 0, 0, 18);
		grid.Controls.Add(label, 0, row);
		grid.SetColumnSpan(label, 2);
	}

	private static void AddSettingRow(TableLayoutPanel grid, string labelText, Control control)
	{
		var row = grid.RowCount++;
		grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		var label = new Label
		{
			Text = labelText,
			AutoSize = true,
			Anchor = AnchorStyles.Left,
			Margin = new Padding(0, 9, 18, 13),
			AccessibleName = $"{labelText} setting label"
		};
		control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
		control.Margin = new Padding(0, 4, 0, 10);
		grid.Controls.Add(label, 0, row);
		grid.Controls.Add(control, 1, row);
	}

	private static void AddFiller(TableLayoutPanel grid)
	{
		var row = grid.RowCount++;
		grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		var filler = new Panel { Dock = DockStyle.Fill };
		grid.Controls.Add(filler, 0, row);
		grid.SetColumnSpan(filler, 2);
	}

	private static Label CreateWrappedLabel(string text)
	{
		return new Label
		{
			Text = text,
			AutoSize = true,
			Dock = DockStyle.Fill,
			MaximumSize = new Size(760, 0),
			AccessibleName = "Information",
			AccessibleDescription = text
		};
	}

	private static ComboBox CreateChoiceCombo(params ChoiceItem[] choices)
	{
		var comboBox = new ComboBox
		{
			DropDownStyle = ComboBoxStyle.DropDownList,
			Dock = DockStyle.Fill,
			IntegralHeight = true
		};
		comboBox.Items.AddRange(choices);
		if (comboBox.Items.Count > 0)
			comboBox.SelectedIndex = 0;
		return comboBox;
	}

	private static void SelectChoice<T>(ComboBox comboBox, T value) where T : struct, Enum
	{
		for (var index = 0; index < comboBox.Items.Count; index++)
		{
			if (comboBox.Items[index] is ChoiceItem item && item.Value is T candidate && EqualityComparer<T>.Default.Equals(candidate, value))
			{
				comboBox.SelectedIndex = index;
				return;
			}
		}
	}

	private static T GetSelectedChoice<T>(ComboBox comboBox, T fallback) where T : struct, Enum
	{
		return comboBox.SelectedItem is ChoiceItem item && item.Value is T value ? value : fallback;
	}

	private static CheckBox CreateCheckBox(
		string text,
		bool isChecked,
		string accessibleName,
		string accessibleDescription)
	{
		return new CheckBox
		{
			Text = text,
			Checked = isChecked,
			AutoSize = true,
			AccessibleName = accessibleName,
			AccessibleDescription = accessibleDescription
		};
	}

	private static TextBox CreateShortcutTextBox(string text, string accessibleName, string accessibleDescription)
	{
		return new TextBox
		{
			Text = text,
			Dock = DockStyle.Fill,
			AccessibleName = accessibleName,
			AccessibleDescription = accessibleDescription
		};
	}

	private static Button CreateButton(string text, string accessibleDescription, Size size)
	{
		return new Button
		{
			Text = text,
			AutoSize = false,
			Size = size,
			AccessibleName = text,
			AccessibleDescription = accessibleDescription
		};
	}

	private static void ConfigureAccessible(Control control, string name, string description)
	{
		control.AccessibleName = name;
		control.AccessibleDescription = description;
	}

	private sealed record ChoiceItem(string Label, object Value)
	{
		public override string ToString() => Label;
	}

	private sealed record DisplayOption(string StableId, string Label, bool IsAvailable)
	{
		public override string ToString() => Label;
	}

	private sealed record ApplicationRuleSeed(
		string ApplicationId,
		int WindowCount,
		bool Ignore,
		PreviewMode PreviewMode);

	private sealed record ShortcutEntry(string Name, TextBox TextBox);
}
