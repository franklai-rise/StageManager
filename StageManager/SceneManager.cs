using StageManager.Infrastructure;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace StageManager;

public sealed class SceneManager : IDisposable
{
	private readonly SettingsService _settings;
	private readonly VirtualDesktopService _virtualDesktops;
	private readonly DisplayTopologyService _displays;
	private readonly Dispatcher _dispatcher;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly Dictionary<Guid, List<Stage>> _stagesByDesktop = new();
	private readonly Dictionary<Guid, Stage?> _currentByDesktop = new();
	private readonly Dictionary<IntPtr, DateTime> _ignoreForegroundUntil = new();
	private Guid _currentDesktopId;
	private bool _started;
	private bool _stopping;

	public SceneManager(
		WindowsManager windowsManager,
		SettingsService settings,
		VirtualDesktopService virtualDesktops,
		DisplayTopologyService displays,
		Dispatcher dispatcher)
	{
		WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		_virtualDesktops = virtualDesktops ?? throw new ArgumentNullException(nameof(virtualDesktops));
		_displays = displays ?? throw new ArgumentNullException(nameof(displays));
		_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
	}

	public event EventHandler<StageChangedEventArgs>? StageChanged;
	public event EventHandler<CurrentStageSelectionChangedEventArgs>? CurrentStageSelectionChanged;
	public event EventHandler? StagesReset;

	public WindowsManager WindowsManager { get; }
	public StageMode Mode => _settings.Current.StageMode;
	public AppWindowsMode WindowsMode => _settings.Current.AppWindowsMode;

	public async Task Start()
	{
		if (_started)
			return;
		if (!_dispatcher.CheckAccess())
			throw new NotSupportedException("SceneManager.Start must run on the WPF dispatcher thread.");

		WindowsManager.WindowCreated += OnWindowCreated;
		WindowsManager.WindowUpdated += OnWindowUpdated;
		WindowsManager.WindowDestroyed += OnWindowDestroyed;
		WindowsManager.DesktopChanged += OnDesktopChanged;
		_settings.SettingsChanged += OnSettingsChanged;
		await WindowsManager.Start().ConfigureAwait(true);
		BuildInitialStages();
		_started = true;
	}

	public void Stop()
	{
		if (_stopping)
			return;
		_stopping = true;
		_lifetime.Cancel();
		_settings.SettingsChanged -= OnSettingsChanged;
		WindowsManager.WindowCreated -= OnWindowCreated;
		WindowsManager.WindowUpdated -= OnWindowUpdated;
		WindowsManager.WindowDestroyed -= OnWindowDestroyed;
		WindowsManager.DesktopChanged -= OnDesktopChanged;
		WindowsManager.Stop();
		RestoreAllManagerMinimizedWindows();
	}

	public void Dispose()
	{
		Stop();
		WindowsManager.Dispose();
		_lifetime.Dispose();
		_gate.Dispose();
	}

	public IReadOnlyList<Stage> GetStages() => CurrentStages
		.OrderByDescending(stage => stage.LastActivatedUtc)
		.ToArray();

	public Stage? GetCurrentStage() => CurrentStage;

	public IEnumerable<IWindow> GetCurrentWindows() => WindowsManager.Windows
		.Where(IsOnCurrentDesktop)
		.ToArray();

	public Stage? FindStageForWindow(IWindow window) => FindStageForWindow(window.Handle);

	public Stage? FindStageForWindow(IntPtr handle) => AllStages.FirstOrDefault(stage => stage.ContainsWindow(handle));

	public Task SwitchTo(Stage? stage) => RunSerializedAsync(() => SelectStageInternalAsync(stage, activateWindows: true, explicitSelection: true), "switch stage");

	public Task SwitchRelative(int direction) => RunSerializedAsync(async () =>
	{
		var stages = GetStages();
		if (stages.Count == 0)
			return;
		var currentIndex = CurrentStage is null ? -1 : stages.ToList().FindIndex(stage => stage.Id == CurrentStage.Id);
		var nextIndex = currentIndex < 0 ? 0 : (currentIndex + direction + stages.Count) % stages.Count;
		await SelectStageInternalAsync(stages[nextIndex], activateWindows: true, explicitSelection: true);
	}, "switch relative stage");

	public Task MoveWindow(IntPtr handle, Stage targetStage) => RunSerializedAsync(async () =>
	{
		if (!WindowsManager.TryGetWindow(handle, out var window) || window is null)
			return;
		await MoveWindowInternalAsync(FindStageForWindow(handle), window, targetStage);
	}, "move window to stage");

	public Task MergeStageIntoCurrent(Stage sourceStage) => RunSerializedAsync(async () =>
	{
		var target = CurrentStage;
		if (target is null || sourceStage.Id == target.Id)
			return;
		foreach (var window in sourceStage.Windows.ToArray())
			await MoveWindowInternalAsync(sourceStage, window, target, emitForEach: false);
		RemoveStageIfEmpty(sourceStage);
		StageChanged?.Invoke(this, new StageChangedEventArgs(target, null, ChangeType.Updated));
	}, "merge stage");

	public Task ExtractLastWindow(Stage sourceStage) => RunSerializedAsync(async () =>
	{
		if (sourceStage.Windows.Count <= 1)
			return;
		var window = sourceStage.Windows.Last();
		var extracted = new Stage(Stage.GetAppKey(window), window);
		sourceStage.Remove(window);
		CurrentStages.Add(extracted);
		StageChanged?.Invoke(this, new StageChangedEventArgs(sourceStage, window, ChangeType.Updated));
		StageChanged?.Invoke(this, new StageChangedEventArgs(extracted, window, ChangeType.Created));
		await SelectStageInternalAsync(extracted, activateWindows: true, explicitSelection: true);
	}, "extract window from stage");

	public Task ToggleForegroundWindowInCurrentStage() => RunSerializedAsync(async () =>
	{
		var handle = Win32.GetForegroundWindow();
		if (!WindowsManager.TryGetWindow(handle, out var window) || window is null)
			return;
		var source = FindStageForWindow(handle);
		var current = CurrentStage;
		if (current is null)
		{
			if (source is not null)
				await SelectStageInternalAsync(source, activateWindows: true, explicitSelection: true);
			return;
		}

		if (source?.Id == current.Id && current.Windows.Count > 1)
		{
			var extracted = new Stage(Stage.GetAppKey(window), window);
			current.Remove(window);
			CurrentStages.Add(extracted);
			StageChanged?.Invoke(this, new StageChangedEventArgs(current, window, ChangeType.Updated));
			StageChanged?.Invoke(this, new StageChangedEventArgs(extracted, window, ChangeType.Created));
		}
		else
			await MoveWindowInternalAsync(source, window, current);
	}, "toggle foreground window in stage");

	public Task MoveStageToNextDisplay(Stage stage) => RunSerializedAsync(() =>
	{
		stage.CaptureLayouts(_displays);
		_displays.MoveToNextDisplay(stage.Windows);
		stage.CaptureLayouts(_displays);
		StageChanged?.Invoke(this, new StageChangedEventArgs(stage, null, ChangeType.Updated));
		return Task.CompletedTask;
	}, "move stage to next display");

	public Task ArrangeStage(Stage stage, StageLayout layout) => RunSerializedAsync(() =>
	{
		stage.CaptureLayouts(_displays);
		_displays.Arrange(stage.Windows, layout);
		stage.CaptureLayouts(_displays);
		StageChanged?.Invoke(this, new StageChangedEventArgs(stage, null, ChangeType.Updated));
		return Task.CompletedTask;
	}, "arrange stage");

	public Task CloseLastWindow(Stage stage) => RunSerializedAsync(() =>
	{
		stage.Windows.LastOrDefault()?.Close();
		return Task.CompletedTask;
	}, "close stage window");

	private List<Stage> CurrentStages
	{
		get
		{
			if (!_stagesByDesktop.TryGetValue(_currentDesktopId, out var stages))
			{
				stages = new List<Stage>();
				_stagesByDesktop[_currentDesktopId] = stages;
			}
			return stages;
		}
	}

	private Stage? CurrentStage
	{
		get => _currentByDesktop.TryGetValue(_currentDesktopId, out var stage) ? stage : null;
		set => _currentByDesktop[_currentDesktopId] = value;
	}

	private IEnumerable<Stage> AllStages => _stagesByDesktop.Values.SelectMany(stages => stages);

	private void BuildInitialStages()
	{
		var windows = WindowsManager.Windows.ToArray();
		foreach (var desktopGroup in windows.GroupBy(GetDesktopId))
		{
			_stagesByDesktop[desktopGroup.Key] = desktopGroup
				.GroupBy(Stage.GetAppKey, StringComparer.OrdinalIgnoreCase)
				.Select(group => new Stage(group.Key, group.ToArray()))
				.ToList();
		}

		_currentDesktopId = _virtualDesktops.GetCurrentDesktopId(windows, Win32.GetForegroundWindow());
		_ = CurrentStages;
		var foreground = Win32.GetForegroundWindow();
		CurrentStage = CurrentStages.FirstOrDefault(stage => stage.ContainsWindow(foreground));
		if (CurrentStage is not null)
		{
			CurrentStage.IsSelected = true;
			CurrentStage.Touch();
		}
	}

	private void OnWindowCreated(IWindow window, bool firstCreate) => Queue(async () =>
	{
		var desktopId = GetDesktopId(window);
		var stages = GetOrCreateStages(desktopId);
		var appKey = Stage.GetAppKey(window);
		var stage = stages
			.Where(candidate => candidate.ContainsApp(appKey))
			.OrderByDescending(candidate => candidate.LastActivatedUtc)
			.FirstOrDefault();
		if (stage is null)
		{
			stage = new Stage(appKey, window);
			stages.Add(stage);
			if (desktopId == _currentDesktopId)
				StageChanged?.Invoke(this, new StageChangedEventArgs(stage, window, ChangeType.Created));
		}
		else
		{
			stage.Add(window);
			if (desktopId == _currentDesktopId)
				StageChanged?.Invoke(this, new StageChangedEventArgs(stage, window, ChangeType.Updated));
		}

		if (firstCreate && desktopId == _currentDesktopId)
			await SelectStageInternalAsync(stage, activateWindows: false, explicitSelection: false);
	}, "window created");

	private void OnWindowUpdated(IWindow window, WindowUpdateType type)
	{
		if (type == WindowUpdateType.Foreground)
		{
			Queue(async () =>
			{
				if (_ignoreForegroundUntil.TryGetValue(window.Handle, out var until) && until > DateTime.UtcNow)
					return;
				var stage = FindStageForWindow(window);
				if (stage is not null && GetDesktopId(window) == _currentDesktopId)
					await SelectStageInternalAsync(stage, activateWindows: false, explicitSelection: false);
			}, "foreground changed");
		}
		else if (type == WindowUpdateType.MoveEnd)
		{
			Queue(() =>
			{
				var stage = FindStageForWindow(window);
				if (stage is not null)
				{
					stage.CaptureLayouts(_displays);
					StageChanged?.Invoke(this, new StageChangedEventArgs(stage, window, ChangeType.Updated));
				}
				return Task.CompletedTask;
			}, "window move completed");
		}
	}

	private void OnWindowDestroyed(IWindow window) => Queue(() =>
	{
		var stage = FindStageForWindow(window);
		if (stage is null)
			return Task.CompletedTask;
		var desktopId = _stagesByDesktop.First(pair => pair.Value.Contains(stage)).Key;
		stage.Remove(window);
		if (stage.WindowCount == 0)
		{
			_stagesByDesktop[desktopId].Remove(stage);
			if (_currentByDesktop.TryGetValue(desktopId, out var current) && current?.Id == stage.Id)
				_currentByDesktop[desktopId] = null;
			if (desktopId == _currentDesktopId)
				StageChanged?.Invoke(this, new StageChangedEventArgs(stage, window, ChangeType.Removed));
		}
		else if (desktopId == _currentDesktopId)
			StageChanged?.Invoke(this, new StageChangedEventArgs(stage, window, ChangeType.Updated));
		return Task.CompletedTask;
	}, "window destroyed");

	private void OnDesktopChanged(object? sender, EventArgs e) => Queue(() =>
	{
		var detected = _virtualDesktops.GetCurrentDesktopId(WindowsManager.Windows, Win32.GetForegroundWindow());
		_currentDesktopId = detected;
		ReconcileCurrentDesktop();
		StagesReset?.Invoke(this, EventArgs.Empty);
		return Task.CompletedTask;
	}, "virtual desktop changed");

	private void OnSettingsChanged(object? sender, EventArgs e) => Queue(async () =>
	{
		WindowsManager.ReevaluateWindows();
		if (_settings.Current.StageMode == StageMode.Coexist)
			RestoreAllManagerMinimizedWindows();
		else if (CurrentStage is not null)
			await ApplyFocusModeAsync(CurrentStage);
		StagesReset?.Invoke(this, EventArgs.Empty);
	}, "settings changed");

	private async Task SelectStageInternalAsync(Stage? stage, bool activateWindows, bool explicitSelection)
	{
		if (stage is not null && !CurrentStages.Contains(stage))
			return;

		var prior = CurrentStage;
		var repeated = stage is not null && prior?.Id == stage.Id;
		if (repeated && !explicitSelection)
			return;

		prior?.CaptureLayouts(_displays);
		CurrentStage = stage;
		foreach (var candidate in CurrentStages)
			candidate.IsSelected = candidate.Id == stage?.Id;

		if (stage is not null)
		{
			stage.Touch();
			if (_settings.Current.StageMode == StageMode.Focus)
				await ApplyFocusModeAsync(stage);
			await RestoreAndActivateStageAsync(stage, activateWindows, repeated);
		}

		if (!repeated)
			CurrentStageSelectionChanged?.Invoke(this, new CurrentStageSelectionChangedEventArgs(prior, stage));
		else
			StageChanged?.Invoke(this, new StageChangedEventArgs(stage!, null, ChangeType.Updated));
	}

	private Task ApplyFocusModeAsync(Stage target)
	{
		foreach (var stage in CurrentStages.Where(candidate => candidate.Id != target.Id))
		{
			stage.CaptureLayouts(_displays);
			foreach (var window in stage.Windows.Where(window => !window.IsMinimized))
			{
				stage.MarkMinimizedByManager(window.Handle);
				_ignoreForegroundUntil[window.Handle] = DateTime.UtcNow.AddMilliseconds(650);
				Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_MINIMIZE);
			}
		}
		return Task.CompletedTask;
	}

	private Task RestoreAndActivateStageAsync(Stage stage, bool activateWindows, bool repeated)
	{
		if (_settings.Current.AppWindowsMode == AppWindowsMode.OneAtATime)
		{
			var target = stage.GetNextWindow();
			if (target is null)
				return Task.CompletedTask;
			if (_settings.Current.StageMode == StageMode.Focus)
			{
				foreach (var other in stage.Windows.Where(window => window.Handle != target.Handle && !window.IsMinimized))
				{
					stage.MarkMinimizedByManager(other.Handle);
					Win32.ShowWindowAsync(other.Handle, Win32.SW.SW_MINIMIZE);
				}
			}
			RestoreManagedWindow(stage, target, restoreUserMinimized: true);
			if (activateWindows)
				FocusWindow(target);
			return Task.CompletedTask;
		}

		foreach (var window in stage.Windows)
		{
			RestoreManagedWindow(stage, window, restoreUserMinimized: true);
			if (!window.IsMinimized)
				window.BringToTop();
		}
		if (activateWindows)
			FocusWindow(stage.Windows.LastOrDefault());
		return Task.CompletedTask;
	}

	private void RestoreManagedWindow(Stage stage, IWindow window, bool restoreUserMinimized = false)
	{
		if (!stage.WasMinimizedByManager(window.Handle))
		{
			if (restoreUserMinimized && window.IsMinimized)
				Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_RESTORE);
			return;
		}
		_ignoreForegroundUntil[window.Handle] = DateTime.UtcNow.AddMilliseconds(650);
		if (stage.TryGetLayout(window.Handle, out var snapshot) && snapshot is not null)
			_displays.Restore(window, snapshot);
		else
			Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_RESTORE);
		stage.ClearManagerMinimized(window.Handle);
	}

	private void FocusWindow(IWindow? window)
	{
		if (window is null)
			return;
		_ignoreForegroundUntil[window.Handle] = DateTime.UtcNow.AddMilliseconds(650);
		window.BringToTop();
		window.Focus();
	}

	private async Task MoveWindowInternalAsync(Stage? sourceStage, IWindow window, Stage targetStage, bool emitForEach = true)
	{
		if (sourceStage?.Id == targetStage.Id)
			return;
		sourceStage?.Remove(window);
		targetStage.Add(window);
		if (sourceStage is not null && emitForEach)
			StageChanged?.Invoke(this, new StageChangedEventArgs(sourceStage, window, ChangeType.Updated));
		if (emitForEach)
			StageChanged?.Invoke(this, new StageChangedEventArgs(targetStage, window, ChangeType.Updated));
		if (sourceStage is not null)
			RemoveStageIfEmpty(sourceStage);

		if (_settings.Current.StageMode == StageMode.Focus && CurrentStage?.Id != targetStage.Id && !window.IsMinimized)
		{
			targetStage.CaptureLayouts(_displays);
			targetStage.MarkMinimizedByManager(window.Handle);
			Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_MINIMIZE);
		}
		else if (CurrentStage?.Id == targetStage.Id)
		{
			RestoreManagedWindow(targetStage, window, restoreUserMinimized: true);
			FocusWindow(window);
		}
		await Task.CompletedTask;
	}

	private void RemoveStageIfEmpty(Stage stage)
	{
		if (stage.WindowCount != 0)
			return;
		CurrentStages.Remove(stage);
		if (CurrentStage?.Id == stage.Id)
			CurrentStage = null;
		StageChanged?.Invoke(this, new StageChangedEventArgs(stage, null, ChangeType.Removed));
	}

	private void ReconcileCurrentDesktop()
	{
		var currentWindows = WindowsManager.Windows.Where(IsOnCurrentDesktop).ToArray();
		var handles = currentWindows.Select(window => window.Handle).ToHashSet();
		foreach (var stage in CurrentStages.ToArray())
		{
			foreach (var staleWindow in stage.Windows.Where(window => !handles.Contains(window.Handle)).ToArray())
				stage.Remove(staleWindow);
			if (stage.WindowCount == 0)
				CurrentStages.Remove(stage);
		}

		foreach (var window in currentWindows.Where(window => FindStageForWindow(window) is null))
		{
			var appKey = Stage.GetAppKey(window);
			var stage = CurrentStages.FirstOrDefault(candidate => candidate.ContainsApp(appKey));
			if (stage is null)
				CurrentStages.Add(new Stage(appKey, window));
			else
				stage.Add(window);
		}

		var foreground = Win32.GetForegroundWindow();
		CurrentStage = CurrentStages.FirstOrDefault(stage => stage.ContainsWindow(foreground));
		foreach (var stage in CurrentStages)
			stage.IsSelected = stage.Id == CurrentStage?.Id;
	}

	private List<Stage> GetOrCreateStages(Guid desktopId)
	{
		if (!_stagesByDesktop.TryGetValue(desktopId, out var stages))
		{
			stages = new List<Stage>();
			_stagesByDesktop[desktopId] = stages;
		}
		return stages;
	}

	private Guid GetDesktopId(IWindow window)
	{
		var id = window is WindowsWindow concrete ? concrete.Identity.VirtualDesktopId : _virtualDesktops.GetDesktopId(window.Handle);
		return _virtualDesktops.IsAvailable ? id : Guid.Empty;
	}

	private bool IsOnCurrentDesktop(IWindow window) => !_virtualDesktops.IsAvailable || _virtualDesktops.IsWindowOnCurrentDesktop(window.Handle);

	private void RestoreAllManagerMinimizedWindows()
	{
		foreach (var stage in AllStages)
		{
			foreach (var window in stage.Windows.ToArray())
				RestoreManagedWindow(stage, window);
		}
	}

	private void Queue(Func<Task> action, string operation)
	{
		if (_stopping)
			return;
		var dispatcherOperation = _dispatcher.InvokeAsync(() => RunSerializedAsync(action, operation), DispatcherPriority.Background);
		_ = dispatcherOperation.Task.Unwrap().ContinueWith(task =>
		{
			if (task.Exception is not null)
				AppLogger.Error($"Queued operation '{operation}' failed.", task.Exception.Flatten());
		}, TaskScheduler.Default);
	}

	private async Task RunSerializedAsync(Func<Task> action, string operation)
	{
		if (_stopping)
			return;
		await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
		try
		{
			await action().ConfigureAwait(true);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			AppLogger.Error($"Stage operation '{operation}' failed.", ex);
		}
		finally
		{
			_gate.Release();
		}
	}
}
