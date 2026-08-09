using System;
using System.IO;
using System.Text.Json;

namespace StageManager.Infrastructure;

internal sealed class StartupGuard
{
	private readonly string _statePath;
	private GuardState _state = new();

	public StartupGuard()
	{
		var directory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Stage_Manager_Lai");
		Directory.CreateDirectory(directory);
		_statePath = Path.Combine(directory, "startup-state.json");
	}

	public bool BeginRun()
	{
		try
		{
			if (File.Exists(_statePath))
				_state = JsonSerializer.Deserialize<GuardState>(File.ReadAllText(_statePath)) ?? new GuardState();

			if (!_state.CleanExit)
				_state.ConsecutiveUncleanExits++;
			else
				_state.ConsecutiveUncleanExits = 0;

			_state.CleanExit = false;
			_state.LastStartedUtc = DateTime.UtcNow;
			Save();
		}
		catch (Exception ex)
		{
			AppLogger.Error("Unable to update startup guard state.", ex);
		}

		return _state.ConsecutiveUncleanExits >= 3;
	}

	public void MarkCleanExit()
	{
		_state.CleanExit = true;
		_state.ConsecutiveUncleanExits = 0;
		Save();
	}

	public void Reset()
	{
		_state = new GuardState { CleanExit = true };
		Save();
	}

	private void Save()
	{
		try
		{
			var tempPath = _statePath + ".tmp";
			File.WriteAllText(tempPath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
			File.Move(tempPath, _statePath, true);
		}
		catch (Exception ex)
		{
			AppLogger.Error("Unable to save startup guard state.", ex);
		}
	}

	private sealed class GuardState
	{
		public bool CleanExit { get; set; } = true;
		public int ConsecutiveUncleanExits { get; set; }
		public DateTime LastStartedUtc { get; set; }
	}
}
