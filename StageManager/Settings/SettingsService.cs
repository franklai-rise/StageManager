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
	private const int CurrentSchemaVersion = 7;
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
				{
					var loadedSchemaVersion = loaded.SchemaVersion;
					var normalized = Normalize(loaded);
					if (loadedSchemaVersion != normalized.SchemaVersion)
						Save(normalized);
					return normalized;
				}
			}
		}
		catch (Exception ex)
		{
			AppLogger.Error("Settings could not be loaded; defaults will be used.", ex);
		}

		return Normalize(new AppSettings());
	}

	private void Save() => Save(Current);

	private void Save(AppSettings settings)
	{
		try
		{
			var tempPath = _settingsPath + ".tmp";
			File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, _jsonOptions));
			File.Move(tempPath, _settingsPath, true);
		}
		catch (Exception ex)
		{
			AppLogger.Error("Settings could not be saved.", ex);
		}
	}

	private static AppSettings Normalize(AppSettings settings)
	{
		var sourceSchemaVersion = settings.SchemaVersion;
		settings.SchemaVersion = CurrentSchemaVersion;
		settings.CardScale = Math.Clamp(settings.CardScale, 0.55, 1.25);
		settings.IdleAutoHideSeconds = Math.Clamp(settings.IdleAutoHideSeconds, 15, 600);
		settings.PreviewRefreshMinutes = Math.Clamp(settings.PreviewRefreshMinutes, 1, 60);
		settings.SidebarOpacity = Math.Clamp(settings.SidebarOpacity, 0.65, 1.0);
		settings.IgnoredProcesses ??= new List<string>();
		settings.IgnoredProcesses = settings.IgnoredProcesses
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Select(value => value.Trim())
			.Where(value => sourceSchemaVersion >= 4 || !value.Equals("explorer", StringComparison.OrdinalIgnoreCase))
			.Where(value => sourceSchemaVersion >= 5 || !value.Equals("yuanbao", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
			.ToList();
		return settings;
	}
}
