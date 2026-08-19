using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using StageManager.Threading;

namespace StageManager.Desktop;

internal sealed record PrototypeStageSnapshot(
	string Key,
	string Title,
	IReadOnlyList<IWindow> Windows,
	DateTime LastActivatedUtc,
	bool IsApplicationGroup,
	bool IsCurrent,
	bool IsPinned = false,
	IReadOnlySet<IntPtr>? PinnedWindowHandles = null);

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

internal interface IWindowCatalog
{
	event EventHandler? Changed;
	SettingsService Settings { get; }
	Guid CurrentDesktopId { get; }
	Task StartAsync();
	void ReevaluateWindows();
	void CalibrateWindows();
	IReadOnlyList<PrototypeApplicationChoice> GetApplicationChoices();
	IReadOnlyList<PrototypeStageSnapshot> GetStages();
}

internal interface IStageSession
{
	bool CanUndo { get; }
	bool IsPinned(IntPtr handle);
	Task SwitchToAsync(string stageKey);
	Task ActivateWindowAsync(string stageKey, IntPtr handle);
	Task SwitchRelativeAsync(int direction);
	Task ToggleForegroundWindowAsync();
	Task MergeStagesAsync(string sourceStageKey, string targetStageKey);
	Task MoveWindowAsync(IntPtr handle, string targetStageKey);
	Task ExtractWindowAsync(IntPtr handle);
	Task UndoAsync();
	Task TogglePinAsync(IntPtr handle);
}

/// <summary>
/// Adapts the event-driven core Stage session to the Composition sidebar. The
/// historical class name is retained for binary/source compatibility with the
/// v2.x renderer while its implementation now represents real cross-app stages.
/// </summary>
internal sealed class PrototypeStageCatalog : IWindowCatalog, IStageSession, IDisposable
{
	private readonly SettingsService _settings;
	private readonly VirtualDesktopService _virtualDesktops;
	private readonly WindowsManager _windows;
	private readonly SceneManager _sceneManager;
	private bool _started;
	private bool _disposed;

	public PrototypeStageCatalog()
	{
		var synchronizationContext = SynchronizationContext.Current
			?? throw new InvalidOperationException("The stage catalog must be created on the Windows Forms UI thread.");
		_settings = new SettingsService();
		_virtualDesktops = new VirtualDesktopService();
		_windows = new WindowsManager(new WindowClassifier(_settings, _virtualDesktops), _virtualDesktops);
		_sceneManager = new SceneManager(
			_windows,
			_settings,
			_virtualDesktops,
			new DisplayTopologyService(),
			new SynchronizationContextUiDispatcher(synchronizationContext));
		_sceneManager.StageChanged += SceneManager_Changed;
		_sceneManager.CurrentStageSelectionChanged += SceneManager_Changed;
		_sceneManager.StagesReset += SceneManager_Changed;
	}

	public event EventHandler? Changed;

	public SettingsService Settings => _settings;
	public Guid CurrentDesktopId { get; private set; }
	public bool CanUndo => _sceneManager.CanUndo;

	public async Task StartAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_started)
			return;
		await _sceneManager.Start().ConfigureAwait(true);
		_started = true;
		UpdateCurrentDesktopId();
	}

	public void ReevaluateWindows()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_windows.ReevaluateWindows();
	}

	public void CalibrateWindows()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_windows.CalibrateWindows();
	}

	public IReadOnlyList<PrototypeApplicationChoice> GetApplicationChoices()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var choices = _windows.Windows
			.Where(window => Win32.IsWindow(window.Handle) &&
				ManagedWindowPresence.ShouldDisplay(
					Win32.IsWindowVisible(window.Handle),
					Win32.IsIconic(window.Handle)) &&
				_virtualDesktops.IsWindowOnCurrentDesktop(window.Handle) &&
				!string.IsNullOrWhiteSpace(window.ProcessName))
			.GroupBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => new PrototypeApplicationChoice(group.First().ProcessName, group.Count()),
				StringComparer.OrdinalIgnoreCase);

		foreach (var rule in _settings.Current.ApplicationRules)
		{
			if (!choices.ContainsKey(rule.ApplicationId))
				choices[rule.ApplicationId] = new PrototypeApplicationChoice(rule.ApplicationId, 0);
		}

		return choices.Values
			.OrderBy(choice => choice.ProcessName, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}

	public IReadOnlyList<PrototypeStageSnapshot> GetStages()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		UpdateCurrentDesktopId();
		var currentStageId = _sceneManager.GetCurrentStage()?.Id;
		return _sceneManager.GetStages()
			.Select(stage =>
			{
				var windows = stage.Windows
					.OrderByDescending(window => window.IsFocused)
					.ThenBy(window => window.IsMinimized)
					.ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
					.ToArray();
				var distinctApps = windows.Select(Stage.GetAppKey)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Count();
				var pinnedHandles = windows
					.Where(window => _sceneManager.IsPinnedToAllStages(window.Handle))
					.Select(window => window.Handle)
					.ToHashSet();
				return new PrototypeStageSnapshot(
					stage.Id.ToString("N"),
					stage.Title,
					windows,
					stage.LastActivatedUtc,
					windows.Length > 1 && distinctApps == 1,
					stage.Id == currentStageId,
					pinnedHandles.Count > 0,
					pinnedHandles);
			})
			.ToArray();
	}

	public Task SwitchToAsync(string stageKey)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return FindStage(stageKey) is { } stage ? _sceneManager.SwitchTo(stage) : Task.CompletedTask;
	}

	public Task ActivateWindowAsync(string stageKey, IntPtr handle)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return FindStage(stageKey) is { } stage
			? _sceneManager.ActivateWindowInStage(stage, handle)
			: Task.CompletedTask;
	}

	public Task SwitchRelativeAsync(int direction)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _sceneManager.SwitchRelative(direction);
	}

	public Task ToggleForegroundWindowAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _sceneManager.ToggleForegroundWindowInCurrentStage();
	}

	public Task MergeStagesAsync(string sourceStageKey, string targetStageKey)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var source = FindStage(sourceStageKey);
		var target = FindStage(targetStageKey);
		return source is not null && target is not null
			? _sceneManager.MergeStages(source, target)
			: Task.CompletedTask;
	}

	public Task MoveWindowAsync(IntPtr handle, string targetStageKey)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return FindStage(targetStageKey) is { } target
			? _sceneManager.MoveWindow(handle, target)
			: Task.CompletedTask;
	}

	public Task ExtractWindowAsync(IntPtr handle)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _sceneManager.ExtractWindow(handle);
	}

	public Task UndoAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _sceneManager.UndoLastStageAdjustment();
	}

	public bool IsPinned(IntPtr handle)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _sceneManager.IsPinnedToAllStages(handle);
	}

	public Task TogglePinAsync(IntPtr handle)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _sceneManager.TogglePinToAllStages(handle);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_sceneManager.StageChanged -= SceneManager_Changed;
		_sceneManager.CurrentStageSelectionChanged -= SceneManager_Changed;
		_sceneManager.StagesReset -= SceneManager_Changed;
		_sceneManager.Dispose();
		_virtualDesktops.Dispose();
	}

	private Stage? FindStage(string stageKey)
	{
		if (!Guid.TryParseExact(stageKey, "N", out var id))
			return null;
		return _sceneManager.GetStages().FirstOrDefault(stage => stage.Id == id);
	}

	private void UpdateCurrentDesktopId()
	{
		var allWindows = _windows.Windows.ToArray();
		CurrentDesktopId = _virtualDesktops.GetCurrentDesktopId(allWindows, Win32.GetForegroundWindow());
	}

	private void SceneManager_Changed(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
