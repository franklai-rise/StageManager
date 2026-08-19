using StageManager.Native;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;

namespace StageManager.Desktop;

internal sealed record PrototypeStageSnapshot(
	string Key,
	string Title,
	IReadOnlyList<IWindow> Windows,
	DateTime LastActivatedUtc);

internal sealed record PrototypeApplicationChoice(
	string ProcessName,
	int WindowCount)
{
	public override string ToString()
	{
		if (WindowCount <= 0)
			return $"{ProcessName} (not currently running)";

		var suffix = WindowCount == 1 ? "window" : "windows";
		return $"{ProcessName} ({WindowCount} {suffix})";
	}
}

internal sealed class PrototypeStageCatalog : IDisposable
{
	private readonly SettingsService _settings;
	private readonly VirtualDesktopService _virtualDesktops;
	private readonly WindowsManager _windows;
	private readonly Dictionary<Guid, Dictionary<string, DateTime>> _lastActivatedByDesktop = new();
	private readonly Dictionary<Guid, StableStageOrder> _stableStageOrderByDesktop = new();
	private IntPtr _lastForeground;
	private bool _started;
	private bool _disposed;

	public PrototypeStageCatalog()
	{
		_settings = new SettingsService();
		_virtualDesktops = new VirtualDesktopService();
		_windows = new WindowsManager(new WindowClassifier(_settings, _virtualDesktops), _virtualDesktops);
		_windows.WindowCreated += Windows_Changed;
		_windows.WindowDestroyed += Windows_Changed;
		_windows.WindowUpdated += Windows_Updated;
		_windows.WindowFocused += Windows_Focused;
		_windows.DesktopChanged += Windows_DesktopChanged;
		_windows.ExternalWindowUpdate += Windows_Changed;
		_windows.ExternalWindowClosed += Windows_Changed;
	}

	public event EventHandler? Changed;

	public async Task StartAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_started)
			return;
		await _windows.Start().ConfigureAwait(false);
		_started = true;
	}

	public SettingsService Settings => _settings;
	public Guid CurrentDesktopId { get; private set; }

	public void ReevaluateWindows()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_windows.ReevaluateWindows();
	}

	public IReadOnlyList<PrototypeApplicationChoice> GetApplicationChoices()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var choices = _windows.Windows
			.Where(window => NativeMethods.IsWindow(window.Handle) &&
				ManagedWindowPresence.ShouldDisplay(
					NativeMethods.IsWindowVisible(window.Handle),
					NativeMethods.IsIconic(window.Handle)) &&
				_virtualDesktops.IsWindowOnCurrentDesktop(window.Handle) &&
				!string.IsNullOrWhiteSpace(window.ProcessName))
			.GroupBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => new PrototypeApplicationChoice(group.First().ProcessName, group.Count()),
				StringComparer.OrdinalIgnoreCase);

		foreach (var ignoredProcess in _settings.Current.IgnoredProcesses)
		{
			if (!choices.ContainsKey(ignoredProcess))
				choices[ignoredProcess] = new PrototypeApplicationChoice(ignoredProcess, 0);
		}

		return choices.Values
			.OrderBy(choice => choice.ProcessName, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}

	public IReadOnlyList<PrototypeStageSnapshot> GetStages()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var foreground = NativeMethods.GetForegroundWindow();
		var allWindows = _windows.Windows.ToArray();
		CurrentDesktopId = _virtualDesktops.GetCurrentDesktopId(allWindows, foreground);
		if (!_lastActivatedByDesktop.TryGetValue(CurrentDesktopId, out var lastActivatedByApp))
		{
			lastActivatedByApp = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
			_lastActivatedByDesktop.Add(CurrentDesktopId, lastActivatedByApp);
		}
		if (!_stableStageOrderByDesktop.TryGetValue(CurrentDesktopId, out var stableStageOrder))
		{
			stableStageOrder = new StableStageOrder();
			_stableStageOrderByDesktop.Add(CurrentDesktopId, stableStageOrder);
		}
		var ignoredProcesses = _settings.Current.IgnoredProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var candidates = allWindows
			.Where(window => NativeMethods.IsWindow(window.Handle) &&
				ManagedWindowPresence.ShouldDisplay(
					NativeMethods.IsWindowVisible(window.Handle),
					NativeMethods.IsIconic(window.Handle)) &&
				_virtualDesktops.IsWindowOnCurrentDesktop(window.Handle) &&
				!ignoredProcesses.Contains(window.ProcessName))
			.ToArray();
		var foregroundWindow = candidates.FirstOrDefault(window => window.Handle == foreground);
		var foregroundKey = foregroundWindow is null ? null : Stage.GetAppKey(foregroundWindow);

		if (foreground != IntPtr.Zero && foreground != _lastForeground && foregroundKey is not null)
		{
			_lastForeground = foreground;
			lastActivatedByApp[foregroundKey] = DateTime.UtcNow;
		}

		var now = DateTime.UtcNow;
		var snapshots = candidates
			.GroupBy(Stage.GetAppKey, StringComparer.OrdinalIgnoreCase)
			.Select(group =>
			{
				if (!lastActivatedByApp.TryGetValue(group.Key, out var lastActivated))
				{
					lastActivated = now.AddSeconds(-lastActivatedByApp.Count - 1);
					lastActivatedByApp[group.Key] = lastActivated;
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

		snapshots = stableStageOrder
			.Apply(snapshots, stage => stage.Key, stage => stage.LastActivatedUtc)
			.ToArray();

		var liveKeys = snapshots.Select(stage => stage.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var staleKey in lastActivatedByApp.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
			lastActivatedByApp.Remove(staleKey);
		return snapshots;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_windows.WindowCreated -= Windows_Changed;
		_windows.WindowDestroyed -= Windows_Changed;
		_windows.WindowUpdated -= Windows_Updated;
		_windows.WindowFocused -= Windows_Focused;
		_windows.DesktopChanged -= Windows_DesktopChanged;
		_windows.ExternalWindowUpdate -= Windows_Changed;
		_windows.ExternalWindowClosed -= Windows_Changed;
		_windows.Dispose();
	}

	private void Windows_Changed(IWindow window) => Changed?.Invoke(this, EventArgs.Empty);

	private void Windows_Changed(IWindow window, bool firstCreate) => Changed?.Invoke(this, EventArgs.Empty);

	private void Windows_Updated(IWindow window, WindowUpdateType updateType) => Changed?.Invoke(this, EventArgs.Empty);

	private void Windows_Focused(IWindow window) => Changed?.Invoke(this, EventArgs.Empty);

	private void Windows_DesktopChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
