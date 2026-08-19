using StageManager.Infrastructure;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using StageManager.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StageManager;

public sealed class SceneManager : IDisposable
{
	private readonly SettingsService _settings;
	private readonly IWindowCatalog _windowCatalog;
	private readonly DisplayTopologyService _displays;
	private readonly IUiDispatcher _dispatcher;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly Dictionary<Guid, List<Stage>> _stagesByDesktop = new();
	private readonly Dictionary<Guid, Stage?> _currentByDesktop = new();
	private readonly Dictionary<Guid, LinkedList<StageSessionSnapshot>> _undoByDesktop = new();
	private readonly Dictionary<Guid, HashSet<IntPtr>> _pinnedWindowsByDesktop = new();
	private readonly Dictionary<Guid, List<Guid>> _navigationOrderByDesktop = new();
	private readonly Dictionary<IntPtr, DateTime> _ignoreForegroundUntil = new();
	private Guid _currentDesktopId;
	private bool _started;
	private bool _stopping;

	public SceneManager(
		WindowsManager windowsManager,
		SettingsService settings,
		VirtualDesktopService virtualDesktops,
		DisplayTopologyService displays,
		IUiDispatcher dispatcher)
		: this((IWindowCatalog)windowsManager, settings, virtualDesktops, displays, dispatcher)
	{
	}

	internal SceneManager(
		IWindowCatalog windowCatalog,
		SettingsService settings,
		VirtualDesktopService virtualDesktops,
		DisplayTopologyService displays,
		IUiDispatcher dispatcher)
	{
		_windowCatalog = windowCatalog ?? throw new ArgumentNullException(nameof(windowCatalog));
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
		ArgumentNullException.ThrowIfNull(virtualDesktops);
		_displays = displays ?? throw new ArgumentNullException(nameof(displays));
		_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
	}

	public event EventHandler<StageChangedEventArgs>? StageChanged;
	public event EventHandler<CurrentStageSelectionChangedEventArgs>? CurrentStageSelectionChanged;
	public event EventHandler? StagesReset;

	public StageMode Mode => _settings.Current.StageMode;
	public AppWindowsMode WindowsMode => _settings.Current.AppWindowsMode;
	public bool CanUndo => _undoByDesktop.TryGetValue(_currentDesktopId, out var history) && history.Count > 0;
	public bool IsPinnedToAllStages(IntPtr handle) => CurrentPinnedWindows.Contains(handle);

	public async Task Start()
	{
		if (_started)
			return;
		if (!_dispatcher.CheckAccess())
			throw new NotSupportedException("SceneManager.Start must run on its UI dispatcher thread.");

		_windowCatalog.WindowCreated += OnWindowCreated;
		_windowCatalog.WindowUpdated += OnWindowUpdated;
		_windowCatalog.WindowDestroyed += OnWindowDestroyed;
		_windowCatalog.DesktopChanged += OnDesktopChanged;
		_settings.SettingsChanged += OnSettingsChanged;
		await _windowCatalog.Start().ConfigureAwait(true);
		BuildInitialStages();
		if (_settings.Current.StageMode == StageMode.Focus && CurrentStage is not null)
			await ApplyFocusModeAsync(CurrentStage).ConfigureAwait(true);
		_started = true;
	}

	public void Stop()
	{
		if (_stopping)
			return;
		_stopping = true;
		_lifetime.Cancel();
		_settings.SettingsChanged -= OnSettingsChanged;
		_windowCatalog.WindowCreated -= OnWindowCreated;
		_windowCatalog.WindowUpdated -= OnWindowUpdated;
		_windowCatalog.WindowDestroyed -= OnWindowDestroyed;
		_windowCatalog.DesktopChanged -= OnDesktopChanged;
		RestoreAllManagerMinimizedWindows();
		_ignoreForegroundUntil.Clear();
		_windowCatalog.Stop();
	}

	public void Dispose()
	{
		Stop();
		_windowCatalog.Dispose();
		_lifetime.Dispose();
		_gate.Dispose();
	}

	public IReadOnlyList<Stage> GetStages() => CurrentStages
		.OrderByDescending(stage => stage.LastActivatedUtc)
		.ToArray();

	public Stage? GetCurrentStage() => CurrentStage;

	public IEnumerable<IWindow> GetCurrentWindows() => _windowCatalog.Windows
		.Where(IsOnCurrentDesktop)
		.ToArray();

	public Stage? FindStageForWindow(IWindow window) => FindStageForWindow(window.Handle);

	public Stage? FindStageForWindow(IntPtr handle) => AllStages.FirstOrDefault(stage => stage.ContainsWindow(handle));

	public Task SwitchTo(Stage? stage) => RunSerializedAsync(() => SelectStageInternalAsync(stage, activateWindows: true, explicitSelection: true), "switch stage");

	public Task ActivateWindowInStage(Stage stage, IntPtr handle) => RunSerializedAsync(async () =>
	{
		if (!CurrentStages.Contains(stage) || !stage.ContainsWindow(handle) ||
			!_windowCatalog.TryGetWindow(handle, out var target) || target is null)
			return;

		var prior = CurrentStage;
		var repeated = prior?.Id == stage.Id;
		if (!repeated)
			prior?.CaptureLayouts(_displays);
		CurrentStage = stage;
		foreach (var candidate in CurrentStages)
			candidate.IsSelected = candidate.Id == stage.Id;
		stage.Touch();

		if (_settings.Current.StageMode == StageMode.Focus)
			await ApplyFocusModeAsync(stage);
		RestorePinnedWindows();

		if (_settings.Current.AppWindowsMode == AppWindowsMode.AllAtOnce)
		{
			foreach (var window in GetWindowsBottomToTop(stage))
			{
				RestoreManagedWindow(stage, window, restoreUserMinimized: true);
				if (!window.IsMinimized)
					window.BringToTop();
			}
		}
		else if (_settings.Current.StageMode == StageMode.Focus)
		{
			foreach (var other in stage.Windows.Where(window =>
				window.Handle != target.Handle &&
				!window.IsMinimized &&
				!CurrentPinnedWindows.Contains(window.Handle)))
			{
				stage.CaptureLayouts(_displays);
				stage.MarkMinimizedByManager(other.Handle);
				_ignoreForegroundUntil[other.Handle] = DateTime.UtcNow.AddMilliseconds(650);
				Win32.ShowWindowAsync(other.Handle, Win32.SW.SW_MINIMIZE);
			}
		}

		RestoreManagedWindow(stage, target, restoreUserMinimized: true);
		FocusWindow(target);
		if (!repeated)
			CurrentStageSelectionChanged?.Invoke(this, new CurrentStageSelectionChangedEventArgs(prior, stage));
		else
			StageChanged?.Invoke(this, new StageChangedEventArgs(stage, target, ChangeType.Updated));
	}, "activate exact stage window");

	public Task SwitchRelative(int direction) => RunSerializedAsync(async () =>
	{
		var stages = GetNavigationStages();
		if (stages.Count == 0)
			return;
		var currentIndex = CurrentStage is null ? -1 : stages.ToList().FindIndex(stage => stage.Id == CurrentStage.Id);
		var nextIndex = currentIndex < 0 ? 0 : (currentIndex + direction + stages.Count) % stages.Count;
		await SelectStageInternalAsync(stages[nextIndex], activateWindows: true, explicitSelection: true);
	}, "switch relative stage");

	private IReadOnlyList<Stage> GetNavigationStages()
	{
		if (!_navigationOrderByDesktop.TryGetValue(_currentDesktopId, out var order))
		{
			order = new List<Guid>();
			_navigationOrderByDesktop[_currentDesktopId] = order;
		}
		var liveIds = CurrentStages.Select(stage => stage.Id).ToHashSet();
		order.RemoveAll(id => !liveIds.Contains(id));
		foreach (var stage in CurrentStages)
		{
			if (!order.Contains(stage.Id))
				order.Add(stage.Id);
		}
		return order
			.Select(id => CurrentStages.FirstOrDefault(stage => stage.Id == id))
			.Where(stage => stage is not null)
			.Cast<Stage>()
			.ToArray();
	}

	public Task MoveWindow(IntPtr handle, Stage targetStage) => RunSerializedAsync(async () =>
	{
		if (!_windowCatalog.TryGetWindow(handle, out var window) || window is null)
			return;
		var source = FindStageForWindow(handle);
		if (source?.Id == targetStage.Id || !CurrentStages.Contains(targetStage))
			return;
		RememberStageAdjustment();
		await MoveWindowInternalAsync(source, window, targetStage);
	}, "move window to stage");

	public Task MergeStageIntoCurrent(Stage sourceStage) => RunSerializedAsync(async () =>
	{
		var target = CurrentStage;
		if (target is null || sourceStage.Id == target.Id)
			return;
		RememberStageAdjustment();
		await MergeStagesInternalAsync(sourceStage, target);
	}, "merge stage");

	public Task MergeStages(Stage sourceStage, Stage targetStage) => RunSerializedAsync(async () =>
	{
		if (sourceStage.Id == targetStage.Id ||
			!CurrentStages.Contains(sourceStage) ||
			!CurrentStages.Contains(targetStage))
			return;
		RememberStageAdjustment();
		await MergeStagesInternalAsync(sourceStage, targetStage);
	}, "merge stages");

	public Task ExtractLastWindow(Stage sourceStage) => RunSerializedAsync(async () =>
	{
		if (sourceStage.Windows.Count <= 1)
			return;
		var window = sourceStage.Windows.Last();
		RememberStageAdjustment();
		await ExtractWindowInternalAsync(sourceStage, window, activateExtracted: true);
	}, "extract window from stage");

	public Task ExtractWindow(IntPtr handle) => RunSerializedAsync(async () =>
	{
		if (!_windowCatalog.TryGetWindow(handle, out var window) || window is null)
			return;
		var source = FindStageForWindow(window);
		if (source is null || source.WindowCount <= 1)
			return;
		RememberStageAdjustment();
		await ExtractWindowInternalAsync(source, window, activateExtracted: true);
	}, "extract window from stage");

	public Task ToggleForegroundWindowInCurrentStage() => RunSerializedAsync(async () =>
	{
		var handle = Win32.GetForegroundWindow();
		if (!_windowCatalog.TryGetWindow(handle, out var window) || window is null)
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
			RememberStageAdjustment();
			await ExtractWindowInternalAsync(current, window, activateExtracted: true);
		}
		else if (source?.Id != current.Id)
		{
			RememberStageAdjustment();
			await MoveWindowInternalAsync(source, window, current);
		}
	}, "toggle foreground window in stage");

	public Task UndoLastStageAdjustment() => RunSerializedAsync(async () =>
	{
		if (!_undoByDesktop.TryGetValue(_currentDesktopId, out var history) || history.First is null)
			return;
		var snapshot = history.First.Value;
		history.RemoveFirst();
		await RestoreStageSessionAsync(snapshot);
	}, "undo stage adjustment");

	public Task TogglePinToAllStages(IntPtr handle) => RunSerializedAsync(() =>
	{
		if (!_windowCatalog.TryGetWindow(handle, out var window) || window is null || !IsOnCurrentDesktop(window))
			return Task.CompletedTask;
		var stage = FindStageForWindow(window);
		if (!CurrentPinnedWindows.Add(handle))
		{
			CurrentPinnedWindows.Remove(handle);
			if (_settings.Current.StageMode == StageMode.Focus && stage?.Id != CurrentStage?.Id && !window.IsMinimized)
			{
				stage?.CaptureLayouts(_displays);
				stage?.MarkMinimizedByManager(handle);
				_ignoreForegroundUntil[handle] = DateTime.UtcNow.AddMilliseconds(650);
				Win32.ShowWindowAsync(handle, Win32.SW.SW_MINIMIZE);
			}
		}
		else if (stage is not null)
		{
			RestoreManagedWindow(stage, window, restoreUserMinimized: true);
			if (!window.IsMinimized)
				window.BringToTop();
		}

		if (stage is not null)
			StageChanged?.Invoke(this, new StageChangedEventArgs(stage, window, ChangeType.Updated));
		return Task.CompletedTask;
	}, "toggle pin to all stages");

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

	private HashSet<IntPtr> CurrentPinnedWindows
	{
		get
		{
			if (!_pinnedWindowsByDesktop.TryGetValue(_currentDesktopId, out var handles))
			{
				handles = new HashSet<IntPtr>();
				_pinnedWindowsByDesktop[_currentDesktopId] = handles;
			}
			return handles;
		}
	}

	private IEnumerable<Stage> AllStages => _stagesByDesktop.Values.SelectMany(stages => stages);

	private void BuildInitialStages()
	{
		var windows = _windowCatalog.Windows.ToArray();
		foreach (var desktopGroup in windows.GroupBy(GetDesktopId))
		{
			_stagesByDesktop[desktopGroup.Key] = desktopGroup
				.GroupBy(Stage.GetAppKey, StringComparer.OrdinalIgnoreCase)
				.Select(group => new Stage(group.Key, group.ToArray()))
				.ToList();
		}

		_currentDesktopId = DetectCurrentDesktopId(windows);
		_ = CurrentStages;
		var foreground = Win32.GetForegroundWindow();
		CurrentStage = CurrentStages.FirstOrDefault(stage => stage.ContainsWindow(foreground))
			?? CurrentStages.OrderByDescending(stage => stage.LastActivatedUtc).FirstOrDefault();
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
				if (_ignoreForegroundUntil.TryGetValue(window.Handle, out var until))
				{
					if (until > DateTime.UtcNow)
						return;
					_ignoreForegroundUntil.Remove(window.Handle);
				}
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
		else if (type is WindowUpdateType.Show or WindowUpdateType.Hide or
			WindowUpdateType.MinimizeStart or WindowUpdateType.MinimizeEnd or
			WindowUpdateType.NameChanged or WindowUpdateType.StyleChanged)
		{
			Queue(() =>
			{
				var stage = FindStageForWindow(window);
				if (stage is not null && GetDesktopId(window) == _currentDesktopId)
					StageChanged?.Invoke(this, new StageChangedEventArgs(stage, window, ChangeType.Updated));
				return Task.CompletedTask;
			}, "window state changed");
		}
	}

	private void OnWindowDestroyed(IWindow window) => Queue(() =>
	{
		_ignoreForegroundUntil.Remove(window.Handle);
		foreach (var pinned in _pinnedWindowsByDesktop.Values)
			pinned.Remove(window.Handle);
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

	private void OnDesktopChanged(object? sender, EventArgs e) => Queue(async () =>
	{
		var detected = DetectCurrentDesktopId(_windowCatalog.Windows);
		ReconcileDesktopMembership();
		_currentDesktopId = detected;
		ReconcileCurrentDesktop();
		if (_settings.Current.StageMode == StageMode.Focus && CurrentStage is not null)
			await ApplyFocusModeAsync(CurrentStage);
		StagesReset?.Invoke(this, EventArgs.Empty);
	}, "virtual desktop changed");

	private void OnSettingsChanged(object? sender, EventArgs e) => Queue(async () =>
	{
		_windowCatalog.ReevaluateWindows();
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
			foreach (var window in stage.Windows.Where(window => !window.IsMinimized && !CurrentPinnedWindows.Contains(window.Handle)))
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
		RestorePinnedWindows();
		if (_settings.Current.AppWindowsMode == AppWindowsMode.OneAtATime)
		{
			var target = stage.GetNextWindow();
			if (target is null)
				return Task.CompletedTask;
			if (_settings.Current.StageMode == StageMode.Focus)
			{
				foreach (var other in stage.Windows.Where(window =>
					window.Handle != target.Handle &&
					!window.IsMinimized &&
					!CurrentPinnedWindows.Contains(window.Handle)))
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

		foreach (var window in GetWindowsBottomToTop(stage))
		{
			RestoreManagedWindow(stage, window, restoreUserMinimized: true);
			if (!window.IsMinimized)
				window.BringToTop();
		}
		if (activateWindows)
			FocusWindow(GetFrontmostWindow(stage));
		return Task.CompletedTask;
	}

	private static IReadOnlyList<IWindow> GetWindowsBottomToTop(Stage stage) => stage.Windows
		.Select((window, index) => new { Window = window, Index = index, ZOrder = stage.GetCapturedZOrder(window.Handle) })
		.OrderByDescending(item => item.ZOrder)
		.ThenByDescending(item => item.Index)
		.Select(item => item.Window)
		.ToArray();

	private static IWindow? GetFrontmostWindow(Stage stage)
	{
		var captured = stage.Windows
			.Select((window, index) => new { Window = window, Index = index, ZOrder = stage.GetCapturedZOrder(window.Handle) })
			.Where(item => item.ZOrder != int.MaxValue)
			.OrderBy(item => item.ZOrder)
			.ThenBy(item => item.Index)
			.Select(item => item.Window)
			.FirstOrDefault();
		return captured ?? stage.Windows.LastOrDefault();
	}

	private void RestorePinnedWindows()
	{
		foreach (var handle in CurrentPinnedWindows.ToArray())
		{
			if (!_windowCatalog.TryGetWindow(handle, out var window) || window is null)
			{
				CurrentPinnedWindows.Remove(handle);
				continue;
			}
			var ownerStage = FindStageForWindow(window);
			if (ownerStage is null)
				continue;
			RestoreManagedWindow(ownerStage, window, restoreUserMinimized: true);
			if (!window.IsMinimized)
				window.BringToTop();
		}
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

	private async Task MergeStagesInternalAsync(Stage sourceStage, Stage targetStage)
	{
		var sourceWasCurrent = CurrentStage?.Id == sourceStage.Id;
		foreach (var window in sourceStage.Windows.ToArray())
			await MoveWindowInternalAsync(sourceStage, window, targetStage, emitForEach: false);

		RemoveStageIfEmpty(sourceStage);
		targetStage.Touch();
		StageChanged?.Invoke(this, new StageChangedEventArgs(targetStage, null, ChangeType.Updated));
		if (!sourceWasCurrent)
			return;

		var prior = sourceStage;
		CurrentStage = targetStage;
		foreach (var stage in CurrentStages)
			stage.IsSelected = stage.Id == targetStage.Id;
		if (_settings.Current.StageMode == StageMode.Focus)
			await ApplyFocusModeAsync(targetStage);
		await RestoreAndActivateStageAsync(targetStage, activateWindows: true, repeated: false);
		CurrentStageSelectionChanged?.Invoke(this, new CurrentStageSelectionChangedEventArgs(prior, targetStage));
	}

	private async Task ExtractWindowInternalAsync(Stage sourceStage, IWindow window, bool activateExtracted)
	{
		if (!sourceStage.ContainsWindow(window.Handle) || sourceStage.WindowCount <= 1)
			return;

		var transferState = sourceStage.Detach(window);
		var extracted = new Stage(Stage.GetAppKey(window), window);
		extracted.Attach(window, transferState);
		CurrentStages.Add(extracted);
		StageChanged?.Invoke(this, new StageChangedEventArgs(sourceStage, window, ChangeType.Updated));
		StageChanged?.Invoke(this, new StageChangedEventArgs(extracted, window, ChangeType.Created));
		if (activateExtracted)
		{
			await SelectStageInternalAsync(extracted, activateWindows: true, explicitSelection: true);
			return;
		}

		if (_settings.Current.StageMode == StageMode.Focus && !window.IsMinimized && !CurrentPinnedWindows.Contains(window.Handle))
		{
			extracted.CaptureLayouts(_displays);
			extracted.MarkMinimizedByManager(window.Handle);
			_ignoreForegroundUntil[window.Handle] = DateTime.UtcNow.AddMilliseconds(650);
			Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_MINIMIZE);
		}
	}

	private void RememberStageAdjustment()
	{
		foreach (var stage in CurrentStages)
			stage.CaptureLayouts(_displays);

		var snapshot = new StageSessionSnapshot(
			_currentDesktopId,
			CurrentStage?.Id,
			CurrentStages.Select(stage => new StageStateSnapshot(
				stage.Id,
				stage.InitialAppKey,
				stage.LastActivatedUtc,
				stage.Windows.Select(window =>
				{
					stage.TryGetLayout(window.Handle, out var layout);
					var instanceId = _windowCatalog.TryGetWindowInstanceId(window.Handle, out var resolved)
						? resolved
						: new StageManager.Model.WindowInstanceId(window.Handle, window.ProcessId, null, 0);
					return new StageWindowStateSnapshot(instanceId, layout, stage.WasMinimizedByManager(window.Handle));
				}).ToArray())).ToArray());

		if (!_undoByDesktop.TryGetValue(_currentDesktopId, out var history))
		{
			history = new LinkedList<StageSessionSnapshot>();
			_undoByDesktop[_currentDesktopId] = history;
		}
		history.AddFirst(snapshot);
		while (history.Count > 10)
			history.RemoveLast();
	}

	private async Task RestoreStageSessionAsync(StageSessionSnapshot snapshot)
	{
		if (snapshot.DesktopId != _currentDesktopId)
			return;

		var prior = CurrentStage;
		foreach (var stage in CurrentStages)
		{
			foreach (var window in stage.Windows.ToArray())
				RestoreManagedWindow(stage, window);
		}

		var available = _windowCatalog.Windows
			.Where(IsOnCurrentDesktop)
			.ToDictionary(window => window.Handle);
		var used = new HashSet<IntPtr>();
		var restoredStages = new List<Stage>();
		foreach (var stageState in snapshot.Stages)
		{
			var matchedStates = stageState.Windows
				.Select(state => (State: state, Window: ResolveSnapshotWindow(state, available)))
				.Where(match => match.Window is not null)
				.ToArray();
			var members = matchedStates.Select(match => match.Window!).ToArray();
			if (members.Length == 0)
				continue;

			var stage = new Stage(stageState.Id, stageState.InitialAppKey, stageState.LastActivatedUtc, members);
			foreach (var match in matchedStates)
			{
				var state = match.State;
				used.Add(state.Handle);
				if (state.Layout is not null)
					stage.RestoreLayoutSnapshot(state.Layout);
				if (state.WasMinimizedByManager)
					stage.MarkMinimizedByManager(state.Handle);
			}
			restoredStages.Add(stage);
		}

		foreach (var group in available.Values
			.Where(window => !used.Contains(window.Handle))
			.GroupBy(Stage.GetAppKey, StringComparer.OrdinalIgnoreCase))
		{
			restoredStages.Add(new Stage(group.Key, group.ToArray()));
		}

		_stagesByDesktop[_currentDesktopId] = restoredStages;
		CurrentStage = snapshot.CurrentStageId is { } currentId
			? restoredStages.FirstOrDefault(stage => stage.Id == currentId)
			: null;
		CurrentStage ??= restoredStages.FirstOrDefault(stage => stage.ContainsWindow(Win32.GetForegroundWindow()));
		foreach (var stage in restoredStages)
			stage.IsSelected = stage.Id == CurrentStage?.Id;

		if (CurrentStage is not null)
		{
			if (_settings.Current.StageMode == StageMode.Focus)
				await ApplyFocusModeAsync(CurrentStage);
			await RestoreAndActivateStageAsync(CurrentStage, activateWindows: false, repeated: false);
		}
		StagesReset?.Invoke(this, EventArgs.Empty);
		CurrentStageSelectionChanged?.Invoke(this, new CurrentStageSelectionChangedEventArgs(prior, CurrentStage));
	}

	private IWindow? ResolveSnapshotWindow(
		StageWindowStateSnapshot state,
		IReadOnlyDictionary<IntPtr, IWindow> available)
	{
		if (!available.TryGetValue(state.Handle, out var window) ||
			!_windowCatalog.TryGetWindowInstanceId(state.Handle, out var current))
			return null;
		var expected = state.InstanceId;
		if (current.ProcessId != expected.ProcessId || current.Generation != expected.Generation)
			return null;
		if (expected.ProcessStartTimeUtc.HasValue && current.ProcessStartTimeUtc != expected.ProcessStartTimeUtc)
			return null;
		return window;
	}

	private async Task MoveWindowInternalAsync(Stage? sourceStage, IWindow window, Stage targetStage, bool emitForEach = true)
	{
		if (sourceStage?.Id == targetStage.Id)
			return;
		var transferState = sourceStage?.Detach(window) ?? default;
		targetStage.Attach(window, transferState);
		if (sourceStage is not null && emitForEach)
			StageChanged?.Invoke(this, new StageChangedEventArgs(sourceStage, window, ChangeType.Updated));
		if (emitForEach)
			StageChanged?.Invoke(this, new StageChangedEventArgs(targetStage, window, ChangeType.Updated));
		if (sourceStage is not null)
			RemoveStageIfEmpty(sourceStage);

		if (_settings.Current.StageMode == StageMode.Focus &&
			CurrentStage?.Id != targetStage.Id &&
			!window.IsMinimized &&
			!CurrentPinnedWindows.Contains(window.Handle))
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
		var currentWindows = _windowCatalog.Windows.Where(IsOnCurrentDesktop).ToArray();
		var handles = currentWindows.Select(window => window.Handle).ToHashSet();
		CurrentPinnedWindows.RemoveWhere(handle => !handles.Contains(handle));
		foreach (var stage in CurrentStages.ToArray())
		{
			foreach (var staleWindow in stage.Windows.Where(window => !handles.Contains(window.Handle)).ToArray())
				stage.Remove(staleWindow);
			if (stage.WindowCount == 0)
				CurrentStages.Remove(stage);
		}

		foreach (var window in currentWindows.Where(window => CurrentStages.All(stage => !stage.ContainsWindow(window.Handle))))
		{
			var appKey = Stage.GetAppKey(window);
			var stage = CurrentStages.FirstOrDefault(candidate => candidate.ContainsApp(appKey));
			if (stage is null)
				CurrentStages.Add(new Stage(appKey, window));
			else
				stage.Add(window);
		}

		var foreground = Win32.GetForegroundWindow();
		var priorCurrent = CurrentStage;
		CurrentStage = CurrentStages.FirstOrDefault(stage => stage.ContainsWindow(foreground))
			?? (priorCurrent is not null && CurrentStages.Contains(priorCurrent) ? priorCurrent : null)
			?? CurrentStages.OrderByDescending(stage => stage.LastActivatedUtc).FirstOrDefault();
		foreach (var stage in CurrentStages)
			stage.IsSelected = stage.Id == CurrentStage?.Id;
	}

	private void ReconcileDesktopMembership()
	{
		var moves = new List<(Guid SourceDesktop, Guid TargetDesktop, Stage SourceStage, IWindow Window)>();
		foreach (var pair in _stagesByDesktop.ToArray())
		{
			foreach (var stage in pair.Value.ToArray())
			{
				foreach (var window in stage.Windows.ToArray())
				{
					var actualDesktop = GetDesktopId(window);
					if (actualDesktop != pair.Key)
						moves.Add((pair.Key, actualDesktop, stage, window));
				}
			}
		}

		foreach (var move in moves)
		{
			var transfer = move.SourceStage.Detach(move.Window);
			var targetStages = GetOrCreateStages(move.TargetDesktop);
			var appKey = Stage.GetAppKey(move.Window);
			var target = targetStages
				.Where(stage => stage.ContainsApp(appKey))
				.OrderByDescending(stage => stage.LastActivatedUtc)
				.FirstOrDefault();
			if (target is null)
			{
				target = new Stage(appKey, move.Window);
				targetStages.Add(target);
			}
			target.Attach(move.Window, transfer);

			if (move.SourceStage.WindowCount == 0 && _stagesByDesktop.TryGetValue(move.SourceDesktop, out var sourceStages))
			{
				sourceStages.Remove(move.SourceStage);
				if (_currentByDesktop.TryGetValue(move.SourceDesktop, out var selected) && selected?.Id == move.SourceStage.Id)
					_currentByDesktop[move.SourceDesktop] = null;
			}

			if (_pinnedWindowsByDesktop.TryGetValue(move.SourceDesktop, out var sourcePinned) && sourcePinned.Remove(move.Window.Handle))
			{
				if (!_pinnedWindowsByDesktop.TryGetValue(move.TargetDesktop, out var targetPinned))
				{
					targetPinned = new HashSet<IntPtr>();
					_pinnedWindowsByDesktop[move.TargetDesktop] = targetPinned;
				}
				targetPinned.Add(move.Window.Handle);
			}
		}
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
		return _windowCatalog.GetDesktopId(window);
	}

	private Guid DetectCurrentDesktopId(IEnumerable<IWindow> windows)
	{
		return _windowCatalog.GetCurrentDesktopId(Win32.GetForegroundWindow());
	}

	private bool IsOnCurrentDesktop(IWindow window) =>
		_windowCatalog.IsWindowOnCurrentDesktop(window);

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
		_ = _dispatcher.InvokeAsync(() => RunSerializedAsync(action, operation)).ContinueWith(task =>
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

internal sealed record StageSessionSnapshot(
	Guid DesktopId,
	Guid? CurrentStageId,
	IReadOnlyList<StageStateSnapshot> Stages);

internal sealed record StageStateSnapshot(
	Guid Id,
	string InitialAppKey,
	DateTime LastActivatedUtc,
	IReadOnlyList<StageWindowStateSnapshot> Windows);

internal sealed record StageWindowStateSnapshot(
	StageManager.Model.WindowInstanceId InstanceId,
	StageManager.Model.WindowLayoutSnapshot? Layout,
	bool WasMinimizedByManager)
{
	public IntPtr Handle => InstanceId.Handle;
}
