using StageManager.Settings;

namespace StageManager.Card3DPrototype.QuickLaunch;

/// <summary>A private edit buffer: neither Cancel nor late discovery mutates settings.</summary>
internal sealed class QuickLaunchSettingsForm : Form
{
	private readonly List<QuickLaunchDefinition> _selected;
	private readonly List<QuickLaunchDefinition> _installed = new();
	private readonly ListView _cards = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false };
	private readonly LauncherListView _available = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
	private readonly TextBox _search = new() { Dock = DockStyle.Top };
	private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
	private readonly ImageList _icons = new() { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
	private readonly Dictionary<string, QuickLaunchIconRequest> _iconRequests = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<Bitmap> _ownedIcons = new();
	private readonly CancellationTokenSource _cancellation = new();
	private readonly UiLanguage _language;
	private bool _refreshing;
	private bool _closed;

	public QuickLaunchSettingsForm(IEnumerable<QuickLaunchDefinition> selected, UiLanguage language)
	{
		_language = language;
		_selected = selected.Select(app => app with { }).ToList();
		Text = L("Quick-launch cards", "快捷启动卡片");
		StartPosition = FormStartPosition.CenterParent;
		MinimumSize = new Size(760, 560);
		ClientSize = new Size(920, 620);
		AutoScaleMode = AutoScaleMode.Dpi;
		ShowInTaskbar = false;
		_search.PlaceholderText = L("Search installed apps by name…", "按名称搜索已安装应用…");
		_cards.Columns.Add(L("Cards (top to bottom)", "卡片（从上到下）"), 220);
		_cards.Columns.Add(L("Target", "目标"), 300);
		_available.Columns.Add(L("Installed apps", "已安装应用"), 230);
		_available.Columns.Add(L("Source / target", "来源 / 目标"), 340);
		_icons.Images.Add("fallback", SystemIcons.Application);
		_cards.SmallImageList = _icons;
		_available.SmallImageList = _icons;
		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
		layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
		layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
		layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
		layout.Controls.Add(new Label { Text = L("Check to enable. Cards appear below File Explorer.", "勾选启用，卡片显示在文件资源管理器下方。"), Dock = DockStyle.Fill }, 0, 0);
		layout.Controls.Add(_search, 1, 0);
		layout.Controls.Add(_cards, 0, 1);
		layout.Controls.Add(_available, 1, 1);
		var selectedActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
		selectedActions.Controls.Add(MakeButton(L("Up", "上移"), () => MoveSelected(-1)));
		selectedActions.Controls.Add(MakeButton(L("Down", "下移"), () => MoveSelected(1)));
		selectedActions.Controls.Add(MakeButton(L("Remove", "移除"), RemoveSelected));
		layout.Controls.Add(selectedActions, 0, 2);
		var availableActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
		availableActions.Controls.Add(MakeButton(L("Add selected", "添加所选"), AddSelected));
		availableActions.Controls.Add(MakeButton(L("Add EXE / LNK…", "添加 EXE / LNK…"), AddFile));
		layout.Controls.Add(availableActions, 1, 2);
		layout.Controls.Add(_status, 0, 3);
		var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
		var cancel = MakeButton(L("Cancel", "取消"), () => { DialogResult = DialogResult.Cancel; Close(); });
		var save = MakeButton(L("Use these cards", "使用这些卡片"), () => { DialogResult = DialogResult.OK; Close(); });
		footer.Controls.Add(cancel);
		footer.Controls.Add(save);
		layout.Controls.Add(footer, 1, 3);
		Controls.Add(layout);
		CancelButton = cancel;
		AcceptButton = save;
		_search.TextChanged += (_, _) => RefreshAvailable();
		_available.DoubleClick += (_, _) => AddSelected();
		_available.ViewportChanged += (_, _) => RequestVisibleIcons();
		_cards.ItemChecked += (_, e) => { if (!_refreshing && e.Item.Tag is QuickLaunchDefinition app) app.Enabled = e.Item.Checked; };
		Shown += async (_, _) => await DiscoverAsync();
		RefreshCards();
	}

	public List<QuickLaunchDefinition> GetSelection() => _selected.Select(app => app with { }).ToList();
	private string L(string english, string chinese) => UiText.Get(_language, english, chinese);
	private static Button MakeButton(string text, Action action)
	{
		var button = new Button { Text = text, AutoSize = true, Height = 32 };
		button.Click += (_, _) => action();
		return button;
	}

	private async Task DiscoverAsync()
	{
		_status.Text = L("Finding installed apps…", "正在查找已安装应用…");
		RefreshCards(); // Shown guarantees a UI synchronization context for icon delivery.
		try
		{
			var apps = await QuickLaunchCatalog.DiscoverAsync(_cancellation.Token);
			if (_closed) return;
			_installed.AddRange(apps);
			RefreshAvailable();
			_status.Text = L($"{apps.Count} apps found. You can also add a file.", $"找到 {apps.Count} 个应用，也可手动添加文件。");
		}
		catch (OperationCanceledException) { }
		catch (Exception)
		{
			if (!_closed) _status.Text = L("Discovery unavailable. Add an EXE or LNK file.", "无法获取应用列表，请添加 EXE 或 LNK 文件。");
		}
	}

	private ListViewItem Row(QuickLaunchDefinition app) => new(new[] { app.DisplayName, app.Target })
	{
		Tag = app, Checked = app.Enabled, ImageKey = _icons.Images.ContainsKey(app.Id) ? app.Id : "fallback"
	};

	private void RefreshCards(string? selection = null)
	{
		_refreshing = true;
		_cards.BeginUpdate();
		_cards.Items.Clear();
		foreach (var app in _selected)
		{
			var row = Row(app);
			_cards.Items.Add(row);
			row.Selected = app.Id == selection;
			RequestIcon(app);
		}
		_cards.EndUpdate();
		_refreshing = false;
	}

	private void RefreshAvailable()
	{
		_available.BeginUpdate();
		_available.Items.Clear();
		foreach (var app in _installed.Where(app => app.DisplayName.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)))
			_available.Items.Add(Row(app));
		_available.EndUpdate();
		// Load a bounded initial viewport; the rest is hydrated as the user scrolls.
		RequestVisibleIcons();
	}

	private void RequestVisibleIcons()
	{
		if (_closed) return;
		var first = _available.TopItem?.Index ?? 0;
		for (var index = first; index < Math.Min(_available.Items.Count, first + 40); index++)
			RequestIcon((QuickLaunchDefinition)_available.Items[index].Tag!);
	}

	private void RequestIcon(QuickLaunchDefinition app)
	{
		if (_closed || _iconRequests.ContainsKey(app.Id) || SynchronizationContext.Current is not { } context) return;
		_iconRequests.Add(app.Id, new QuickLaunchIconRequest(app, context, icon =>
		{
			// ImageList may defer copying images until its native handle is created.
			_ownedIcons.Add(icon);
			_icons.Images.Add(app.Id, icon);
			foreach (var view in new[] { _cards, _available })
				foreach (ListViewItem row in view.Items)
					if (((QuickLaunchDefinition)row.Tag!).Id == app.Id) row.ImageKey = app.Id;
		}));
	}

	private void AddSelected()
	{
		foreach (ListViewItem row in _available.SelectedItems) Add((QuickLaunchDefinition)row.Tag!);
		RefreshCards();
	}

	private void Add(QuickLaunchDefinition app)
	{
		var existing = _selected.FirstOrDefault(value => value.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
		if (existing is not null) existing.Enabled = true;
		else _selected.Add(app with { Enabled = true });
	}

	private void AddFile()
	{
		using var dialog = new OpenFileDialog { Filter = "Applications and shortcuts (*.exe;*.lnk)|*.exe;*.lnk", CheckFileExists = true, Multiselect = true, DereferenceLinks = false };
		if (dialog.ShowDialog(this) != DialogResult.OK) return;
		foreach (var file in dialog.FileNames) Add(QuickLaunchCatalog.FromFile(file));
		RefreshCards();
	}

	private void MoveSelected(int delta)
	{
		if (_cards.SelectedIndices.Count == 0) return;
		var index = _cards.SelectedIndices[0];
		var next = index + delta;
		if (next < 0 || next >= _selected.Count) return;
		var app = _selected[index];
		_selected.RemoveAt(index);
		_selected.Insert(next, app);
		RefreshCards(app.Id);
		_cards.SelectedItems[0].EnsureVisible();
	}

	private void RemoveSelected()
	{
		if (_cards.SelectedIndices.Count == 0) return;
		_selected.RemoveAt(_cards.SelectedIndices[0]);
		RefreshCards();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && !_closed)
		{
			_closed = true;
			_cancellation.Cancel();
			_cancellation.Dispose();
			foreach (var request in _iconRequests.Values) request.Dispose();
			_cards.SmallImageList = null;
			_available.SmallImageList = null;
			_icons.Dispose();
			foreach (var icon in _ownedIcons) icon.Dispose();
		}
		base.Dispose(disposing);
	}

	private sealed class LauncherListView : ListView
	{
		public event EventHandler? ViewportChanged;
		protected override void WndProc(ref Message message)
		{
			base.WndProc(ref message);
			// Include native scrollbar dragging, keyboard paging and resize, not only wheel input.
			if (message.Msg is 0x0115 or 0x020A or 0x0101 or 0x0005)
				ViewportChanged?.Invoke(this, EventArgs.Empty);
		}
	}
}
