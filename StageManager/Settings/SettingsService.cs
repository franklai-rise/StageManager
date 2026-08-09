using StageManager.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StageManager.Settings;

public sealed class SettingsService
{
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};
	private readonly string _settingsPath;

	public SettingsService(string? settingsPath = null)
	{
		var directory = settingsPath is null
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai")
			: Path.GetDirectoryName(settingsPath) ?? throw new ArgumentException("A settings path must include a directory.", nameof(settingsPath));
		Directory.CreateDirectory(directory);
		_settingsPath = settingsPath ?? Path.Combine(directory, "settings.json");
		Current = Load();
	}

	public event EventHandler? SettingsChanged;

	public AppSettings Current { get; private set; }

	public string SettingsPath => _settingsPath;

	public AppSettings CloneCurrent()
	{
		var json = JsonSerializer.Serialize(Current, _jsonOptions);
		return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
	}

	public void Apply(AppSettings settings)
	{
		Current = Normalize(settings);
		Save();
		SettingsChanged?.Invoke(this, EventArgs.Empty);
	}

	public void AddIgnoredProcesses(IEnumerable<string> processNames)
	{
		var ignored = new HashSet<string>(Current.IgnoredProcesses, StringComparer.OrdinalIgnoreCase);
		foreach (var processName in processNames.Where(value => !string.IsNullOrWhiteSpace(value)))
			ignored.Add(processName.Trim());

		Current.IgnoredProcesses = ignored.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
		Save();
		SettingsChanged?.Invoke(this, EventArgs.Empty);
	}

	private AppSettings Load()
	{
		try
		{
			if (File.Exists(_settingsPath))
			{
				var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), _jsonOptions);
				if (loaded is not null)
					return Normalize(loaded);
			}
		}
		catch (Exception ex)
		{
			AppLogger.Error("Settings could not be loaded; defaults will be used.", ex);
		}

		return new AppSettings();
	}

	private void Save()
	{
		try
		{
			var tempPath = _settingsPath + ".tmp";
			File.WriteAllText(tempPath, JsonSerializer.Serialize(Current, _jsonOptions));
			File.Move(tempPath, _settingsPath, true);
		}
		catch (Exception ex)
		{
			AppLogger.Error("Settings could not be saved.", ex);
		}
	}

	private static AppSettings Normalize(AppSettings settings)
	{
		settings.SchemaVersion = 1;
		settings.CardScale = Math.Clamp(settings.CardScale, 0.75, 1.25);
		settings.SidebarOpacity = Math.Clamp(settings.SidebarOpacity, 0.65, 1.0);
		settings.IgnoredProcesses ??= new List<string>();
		settings.IgnoredProcesses = settings.IgnoredProcesses
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
			.ToList();
		return settings;
	}
}
