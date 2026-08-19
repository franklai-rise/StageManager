using StageManager.Infrastructure;
using StageManager.Settings;
using System.Diagnostics;

namespace StageManager.Card3DPrototype.Lifecycle;

/// <summary>
/// Minimal recovery shell. It deliberately creates no window catalog, capture,
/// Composition or D3D resources.
/// </summary>
internal sealed class SafeModeForm : Form
{
	private readonly SettingsService _settings = new();
	private readonly DiagnosticBundleExporter _diagnostics;
	private readonly NotifyIcon _trayIcon;
	private readonly ContextMenuStrip _trayMenu = new();
	private bool _closing;

	public SafeModeForm(int recentFailureCount)
	{
		Text = $"{AppVersionInfo.DisplayName} - Safe mode";
		StartPosition = FormStartPosition.CenterScreen;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = true;
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size(520, 270);
		BackColor = Color.FromArgb(25, 27, 32);
		ForeColor = Color.FromArgb(242, 244, 248);
		Font = new Font("Segoe UI", 10f);

		_diagnostics = new DiagnosticBundleExporter(new DiagnosticBundleOptions
		{
			SettingsPath = _settings.SettingsPath,
			IncludeUserPaths = false,
			IncludeWindowTitles = false
		});

		Controls.Add(new Label
		{
			Text = "Stage Manager started in safe mode",
			Font = new Font(Font.FontFamily, 17f, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(24, 22)
		});
		Controls.Add(new Label
		{
			Text = $"The previous {recentFailureCount} starts ended unexpectedly. Window tracking, previews and 3D rendering are disabled so you can recover settings or export a local diagnostic bundle.",
			AutoSize = false,
			Location = new Point(27, 68),
			Size = new Size(465, 72)
		});

		var retryButton = CreateButton("Try normal mode", 27, 167, 145);
		retryButton.Click += (_, _) => RestartNormallyRequested?.Invoke(this, EventArgs.Empty);
		var settingsButton = CreateButton("Settings", 184, 167, 95);
		settingsButton.Click += (_, _) => ShowSettings();
		var exportButton = CreateButton("Export diagnostics", 291, 167, 174);
		exportButton.Click += async (_, _) => await ExportDiagnosticsAsync();
		var exitButton = CreateButton("Exit", 370, 220, 95);
		exitButton.Click += (_, _) => Close();
		Controls.AddRange(new Control[] { retryButton, settingsButton, exportButton, exitButton });

		BuildTrayMenu();
		var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
		_trayIcon = new NotifyIcon
		{
			Text = "Stage_Manager_Lai safe mode",
			Icon = icon,
			ContextMenuStrip = _trayMenu,
			Visible = true
		};
		_trayIcon.MouseClick += (_, eventArgs) =>
		{
			if (eventArgs.Button == MouseButtons.Left)
				ShowSafeModePanel();
		};
	}

	public event EventHandler? RestartNormallyRequested;

	public void ShowSafeModePanel()
	{
		if (_closing)
			return;
		Show();
		WindowState = FormWindowState.Normal;
		Activate();
	}

	protected override void OnFormClosed(FormClosedEventArgs e)
	{
		_closing = true;
		_trayIcon.Visible = false;
		_trayIcon.Icon?.Dispose();
		_trayIcon.Dispose();
		_trayMenu.Dispose();
		base.OnFormClosed(e);
	}

	private void BuildTrayMenu()
	{
		var header = new ToolStripMenuItem($"{AppVersionInfo.DisplayName} - Safe mode") { Enabled = false };
		var show = new ToolStripMenuItem("Show safe-mode panel");
		show.Click += (_, _) => ShowSafeModePanel();
		var settings = new ToolStripMenuItem("Settings...");
		settings.Click += (_, _) => ShowSettings();
		var logs = new ToolStripMenuItem("Open logs folder");
		logs.Click += (_, _) => OpenLogsFolder();
		var export = new ToolStripMenuItem("Export diagnostics...");
		export.Click += async (_, _) => await ExportDiagnosticsAsync();
		var retry = new ToolStripMenuItem("Try normal mode");
		retry.Click += (_, _) => RestartNormallyRequested?.Invoke(this, EventArgs.Empty);
		var exit = new ToolStripMenuItem("Exit");
		exit.Click += (_, _) => Close();
		_trayMenu.Items.AddRange(new ToolStripItem[]
		{
			header, new ToolStripSeparator(), show, settings, logs, export,
			new ToolStripSeparator(), retry, exit
		});
	}

	private void ShowSettings()
	{
		using var dialog = new SettingsForm(_settings.CloneCurrent());
		if (dialog.ShowDialog(this) == DialogResult.OK)
			_settings.Apply(dialog.Draft);
	}

	private async Task ExportDiagnosticsAsync()
	{
		using var dialog = new SaveFileDialog
		{
			Title = "Export Stage_Manager_Lai diagnostics",
			Filter = "ZIP archive (*.zip)|*.zip",
			AddExtension = true,
			DefaultExt = "zip",
			FileName = $"Stage_Manager_Lai-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
			return;

		try
		{
			var result = await _diagnostics.ExportAsync(dialog.FileName);
			MessageBox.Show(this, $"Diagnostic bundle saved to:\n{result.ArchivePath}", "Diagnostics exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
		}
		catch (Exception exception)
		{
			AppLogger.Error("The diagnostic bundle could not be exported.", exception);
			MessageBox.Show(this, exception.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private static void OpenLogsFolder()
	{
		var directory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Stage_Manager_Lai",
			"Logs");
		Directory.CreateDirectory(directory);
		Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
	}

	private static Button CreateButton(string text, int x, int y, int width) => new()
	{
		Text = text,
		Location = new Point(x, y),
		Size = new Size(width, 36),
		FlatStyle = FlatStyle.Flat,
		BackColor = Color.FromArgb(48, 52, 61),
		ForeColor = Color.White
	};
}
