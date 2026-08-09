using StageManager.Infrastructure;
using StageManager.Services;
using StageManager.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace StageManager;

public partial class SettingsWindow : Window
{
	private readonly SettingsService _settingsService;

	public SettingsWindow(SettingsService settingsService)
	{
		_settingsService = settingsService;
		Draft = settingsService.CloneCurrent();
		IgnoredProcessesText = string.Join(Environment.NewLine, Draft.IgnoredProcesses);
		InitializeComponent();
		stageModeCombo.ItemsSource = Enum.GetValues<StageMode>();
		windowsModeCombo.ItemsSource = Enum.GetValues<AppWindowsMode>();
		DataContext = this;
		SourceInitialized += (_, _) => BackdropService.Apply(new WindowInteropHelper(this).Handle);
	}

	public AppSettings Draft { get; }
	public string IgnoredProcessesText { get; set; }

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		var gestures = new Dictionary<string, string>
		{
			["Show / hide sidebar"] = Draft.ToggleSidebarHotkey,
			["Previous stage"] = Draft.PreviousStageHotkey,
			["Next stage"] = Draft.NextStageHotkey,
			["Add / remove active window"] = Draft.ToggleWindowInStageHotkey
		};
		if (Draft.HotkeysEnabled)
		{
			var invalid = gestures.FirstOrDefault(pair => !HotkeyManager.TryParse(pair.Value, out _, out _));
			if (!string.IsNullOrEmpty(invalid.Key))
			{
				MessageBox.Show(this, $"'{invalid.Value}' is not a valid shortcut for {invalid.Key}.", "Invalid shortcut", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
		}

		Draft.IgnoredProcesses = IgnoredProcessesText
			.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		_settingsService.Apply(Draft);
		try
		{
			AutoStart.SetStartup(AutoStart.DefaultAppName, Draft.StartWithWindows);
		}
		catch (Exception ex)
		{
			AppLogger.Error("Unable to update Windows startup registration.", ex);
			MessageBox.Show(this, "Settings were saved, but Windows startup could not be updated.", "Startup setting", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
		DialogResult = true;
	}

	private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
