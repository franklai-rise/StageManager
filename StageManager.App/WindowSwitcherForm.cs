using StageManager.Native.Window;

namespace StageManager.Desktop;

internal sealed record WindowSwitcherEntry(
	string StageKey,
	string StageTitle,
	IWindow Window,
	string StateDescription)
{
	public string SearchText => $"{Window.ProcessName} {Window.Title} {StageTitle} {StateDescription}";
	public override string ToString()
	{
		var title = string.IsNullOrWhiteSpace(Window.Title) ? Window.ProcessName : Window.Title;
		var state = string.IsNullOrWhiteSpace(StateDescription) ? string.Empty : $"  ·  {StateDescription}";
		return $"{Window.ProcessName}  —  {title}{state}";
	}
}

internal static class WindowSwitcherSearch
{
	public static IReadOnlyList<WindowSwitcherEntry> Filter(
		IEnumerable<WindowSwitcherEntry> entries,
		string? query)
	{
		ArgumentNullException.ThrowIfNull(entries);
		var terms = (query ?? string.Empty)
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return entries
			.Where(entry => terms.All(term => entry.SearchText.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
			.OrderByDescending(entry => entry.Window.IsFocused)
			.ThenBy(entry => entry.Window.IsMinimized)
			.ThenBy(entry => entry.Window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(entry => entry.Window.Title, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}
}

internal sealed class WindowSwitcherForm : Form
{
	private readonly IReadOnlyList<WindowSwitcherEntry> _entries;
	private readonly Action<WindowSwitcherEntry> _activate;
	private readonly TextBox _search = new();
	private readonly ListBox _results = new();
	private bool _selectionCommitted;

	public WindowSwitcherForm(
		IReadOnlyList<WindowSwitcherEntry> entries,
		Action<WindowSwitcherEntry> activate)
	{
		_entries = entries ?? throw new ArgumentNullException(nameof(entries));
		_activate = activate ?? throw new ArgumentNullException(nameof(activate));
		InitializeInterface();
		ApplyFilter();
	}

	protected override void OnShown(EventArgs e)
	{
		base.OnShown(e);
		_search.Focus();
	}

	protected override void OnDeactivate(EventArgs e)
	{
		base.OnDeactivate(e);
		if (!_selectionCommitted)
			Close();
	}

	private void InitializeInterface()
	{
		Text = "Stage Manager window switcher";
		AccessibleName = "Stage Manager window switcher";
		AccessibleDescription = "Search running applications and window titles, then choose a specific window.";
		FormBorderStyle = FormBorderStyle.FixedSingle;
		StartPosition = FormStartPosition.Manual;
		ShowInTaskbar = false;
		TopMost = true;
		MaximizeBox = false;
		MinimizeBox = false;
		KeyPreview = true;
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size(680, 430);
		var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
		Location = new Point(
			workArea.Left + Math.Max(0, (workArea.Width - Width) / 2),
			workArea.Top + Math.Max(0, (workArea.Height - Height) / 3));
		Font = new Font("Segoe UI", 11f);
		BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(30, 32, 38);
		ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(242, 244, 248);

		var root = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding(18),
			ColumnCount = 1,
			RowCount = 3,
			BackColor = BackColor
		};
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
		root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

		_search.Dock = DockStyle.Fill;
		_search.BorderStyle = BorderStyle.FixedSingle;
		_search.PlaceholderText = "Type an application or window title…";
		_search.AccessibleName = "Search applications and windows";
		_search.AccessibleDescription = "Filters the running windows listed below.";
		_search.TextChanged += (_, _) => ApplyFilter();
		_search.KeyDown += Search_KeyDown;

		_results.Dock = DockStyle.Fill;
		_results.IntegralHeight = false;
		_results.HorizontalScrollbar = true;
		_results.AccessibleName = "Matching windows";
		_results.AccessibleDescription = "Search results. Press Enter to activate the selected window.";
		_results.DoubleClick += (_, _) => CommitSelection();
		_results.KeyDown += Results_KeyDown;
		_results.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(39, 42, 50);
		_results.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : ForeColor;

		var hint = new Label
		{
			Dock = DockStyle.Fill,
			Text = "↑ ↓ select    Enter open    Esc close",
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(166, 173, 188),
			AccessibleName = "Keyboard instructions"
		};

		root.Controls.Add(_search, 0, 0);
		root.Controls.Add(_results, 0, 1);
		root.Controls.Add(hint, 0, 2);
		Controls.Add(root);
	}

	private void ApplyFilter()
	{
		var filtered = WindowSwitcherSearch.Filter(_entries, _search.Text);

		_results.BeginUpdate();
		try
		{
			_results.Items.Clear();
			_results.Items.AddRange(filtered.Cast<object>().ToArray());
			if (_results.Items.Count > 0)
				_results.SelectedIndex = 0;
		}
		finally
		{
			_results.EndUpdate();
		}
	}

	private void Search_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Escape)
		{
			e.SuppressKeyPress = true;
			Close();
			return;
		}
		if (e.KeyCode == Keys.Enter)
		{
			e.SuppressKeyPress = true;
			CommitSelection();
			return;
		}
		if (e.KeyCode is not (Keys.Down or Keys.Up) || _results.Items.Count == 0)
			return;
		e.SuppressKeyPress = true;
		var delta = e.KeyCode == Keys.Down ? 1 : -1;
		_results.SelectedIndex = Math.Clamp(_results.SelectedIndex + delta, 0, _results.Items.Count - 1);
	}

	private void Results_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Enter)
		{
			e.SuppressKeyPress = true;
			CommitSelection();
		}
		else if (e.KeyCode == Keys.Escape)
		{
			e.SuppressKeyPress = true;
			Close();
		}
	}

	private void CommitSelection()
	{
		if (_results.SelectedItem is not WindowSwitcherEntry selected)
			return;
		_selectionCommitted = true;
		Close();
		_activate(selected);
	}
}
