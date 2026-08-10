using StageManager.Native;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal sealed record PrototypeStageSnapshot(
	string Key,
	string Title,
	IReadOnlyList<IWindow> Windows,
	DateTime LastActivatedUtc);

internal sealed class PrototypeStageCatalog : IDisposable
{
	private readonly SettingsService _settings;
	private readonly VirtualDesktopService _virtualDesktops;
	private readonly WindowsManager _windows;
	private readonly Dictionary<string, DateTime> _lastActivated = new(StringComparer.OrdinalIgnoreCase);
	private readonly StableStageOrder _stableStageOrder = new();
	private IntPtr _lastForeground;
	private bool _started;
	private bool _disposed;

	public PrototypeStageCatalog()
	{
		_settings = new SettingsService();
		_virtualDesktops = new VirtualDesktopService();
		_windows = new WindowsManager(new WindowClassifier(_settings, _virtualDesktops), _virtualDesktops);
	}

	public async Task StartAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_started)
			return;
		await _windows.Start().ConfigureAwait(false);
		_started = true;
	}

	public SettingsService Settings => _settings;

	public IReadOnlyList<PrototypeStageSnapshot> GetStages()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var foreground = NativeMethods.GetForegroundWindow();
		var candidates = _windows.Windows
			.Where(window => NativeMethods.IsWindow(window.Handle) && _virtualDesktops.IsWindowOnCurrentDesktop(window.Handle))
			.ToArray();
		var foregroundWindow = candidates.FirstOrDefault(window => window.Handle == foreground);
		var foregroundKey = foregroundWindow is null ? null : Stage.GetAppKey(foregroundWindow);

		if (foreground != IntPtr.Zero && foreground != _lastForeground && foregroundKey is not null)
		{
			_lastForeground = foreground;
			_lastActivated[foregroundKey] = DateTime.UtcNow;
		}

		var now = DateTime.UtcNow;
		var snapshots = candidates
			.GroupBy(Stage.GetAppKey, StringComparer.OrdinalIgnoreCase)
			.Select(group =>
			{
				if (!_lastActivated.TryGetValue(group.Key, out var lastActivated))
				{
					lastActivated = now.AddSeconds(-_lastActivated.Count - 1);
					_lastActivated[group.Key] = lastActivated;
				}
				var windows = group
					.OrderByDescending(window => window.IsFocused)
					.ThenBy(window => window.IsMinimized)
					.ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
					.ToArray();
				return new PrototypeStageSnapshot(
					group.Key,
					string.Join(" + ", windows.Select(window => window.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase)),
					windows,
					lastActivated);
			})
			.ToArray();

		snapshots = _stableStageOrder
			.Apply(snapshots, stage => stage.Key, stage => stage.LastActivatedUtc)
			.ToArray();

		var liveKeys = snapshots.Select(stage => stage.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var staleKey in _lastActivated.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
			_lastActivated.Remove(staleKey);
		return snapshots;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_windows.Dispose();
	}
}
