using StageManager.Infrastructure;
using StageManager.Model;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StageManager.Native;

public delegate void WindowDelegate(IWindow window);
public delegate void WindowCreateDelegate(IWindow window, bool firstCreate);
public delegate void WindowUpdateDelegate(IWindow window, WindowUpdateType type);

public sealed class WindowsManager : IWindowsManager, IWindowCatalog, IDisposable
{
	private const int EventPumpIntervalMilliseconds = 33;
	private const int DesktopSwitchDelayMilliseconds = 180;
	private const int RejectedWindowCalibrationDelayMilliseconds = 15 * 60 * 1000;
	private static readonly int[] RegistrationRetryDelaysMilliseconds = [140, 360, 900, 1800];

	private readonly ConcurrentDictionary<IntPtr, WindowsWindow> _windows = new();
	private readonly ConcurrentDictionary<IntPtr, WindowInstanceId> _windowInstances = new();
	private readonly ConcurrentDictionary<IntPtr, WindowGenerationState> _windowGenerations = new();
	private readonly ConcurrentDictionary<IntPtr, long> _rejectedWindowRetryDue = new();
	private readonly ConcurrentDictionary<WindowsWindow, bool> _floating = new();
	private readonly Dictionary<WindowInstanceKey, PendingRegistration> _pendingRegistrations = new();
	private readonly Dictionary<IntPtr, long> _processedGenerations = new();
	private readonly List<IntPtr> _winEventHooks = new();
	private readonly object _hooksLock = new();
	private readonly object _lifecycleLock = new();
	private readonly object _stateLock = new();
	private readonly object _mouseMoveLock = new();
	private readonly IWindowClassifier _classifier;
	private readonly VirtualDesktopService _virtualDesktops;
	private readonly WinEventDelegate _hookDelegate;

	private CancellationTokenSource? _eventPumpLifetime;
	private Task? _eventPumpTask;
	private WindowEventInbox? _eventInbox;
	private WindowsWindow? _mouseMoveWindow;
	private long _eventSequence;
	private long _nextWindowGeneration;
	private long _desktopSwitchDueTimestamp;
	private volatile bool _active;
	private bool _disposed;
	private int _currentProcessId;

	public WindowsManager(IWindowClassifier classifier, VirtualDesktopService virtualDesktops)
	{
		_classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
		_virtualDesktops = virtualDesktops ?? throw new ArgumentNullException(nameof(virtualDesktops));
		_hookDelegate = WindowHook;
	}

	public event WindowCreateDelegate? WindowCreated;
	public event WindowDelegate? WindowDestroyed;
	public event WindowUpdateDelegate? WindowUpdated;
	public event EventHandler<IntPtr>? UntrackedFocus;
	public event WindowFocusDelegate? WindowFocused;
	public event EventHandler? WindowMoved;
	public event EventHandler? DesktopChanged;
	public event WindowDelegate? ExternalWindowUpdate;
	public event WindowDelegate? ExternalWindowClosed;

	public IEnumerable<IWindow> Windows => _windows.Values.ToArray();

	public Task Start()
	{
		lock (_lifecycleLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_active)
				return Task.CompletedTask;

			_currentProcessId = Environment.ProcessId;
			_eventSequence = 0;
			_nextWindowGeneration = 0;
			var inbox = new WindowEventInbox();
			var eventPumpLifetime = new CancellationTokenSource();
			_eventInbox = inbox;
			_eventPumpLifetime = eventPumpLifetime;
			_active = true;
			_eventPumpTask = Task.Run(() => RunEventPumpAsync(inbox, eventPumpLifetime.Token));

			try
			{
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CREATE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED, Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_STATECHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_STATECHANGE);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE);
				RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_DESKTOPSWITCH, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_DESKTOPSWITCH);

				lock (_stateLock)
					ReconcileWindowsCore(emitEvent: false);
			}
			catch
			{
				StopCore();
				throw;
			}

			AppLogger.Info($"Window tracking started with {_windows.Count} candidates and a bounded {WindowEventInbox.DefaultCapacity}-event pump.");
			return Task.CompletedTask;
		}
	}

	public void Stop()
	{
		lock (_lifecycleLock)
			StopCore();
	}

	public void Dispose()
	{
		lock (_lifecycleLock)
		{
			if (_disposed)
				return;
			_disposed = true;
			StopCore();
		}
	}

	public void ReevaluateWindows()
	{
		if (!_active)
			return;

		lock (_stateLock)
		{
			if (_active)
			{
				_rejectedWindowRetryDue.Clear();
				ReconcileWindowsCore(emitEvent: true);
			}
		}
	}

	public void CalibrateWindows()
	{
		if (!_active)
			return;
		lock (_stateLock)
		{
			if (_active)
				ReconcileWindowsCore(emitEvent: true);
		}
	}

	public bool TryGetWindow(IntPtr handle, out IWindow? window)
	{
		if (_windows.TryGetValue(handle, out var concrete))
		{
			window = concrete;
			return true;
		}

		window = null;
		return false;
	}

	public bool TryGetWindowInstanceId(IntPtr handle, out WindowInstanceId instanceId) =>
		_windowInstances.TryGetValue(handle, out instanceId);

	public Guid GetDesktopId(IWindow window) => window is WindowsWindow concrete
		? concrete.Identity.VirtualDesktopId
		: _virtualDesktops.GetDesktopId(window.Handle);

	public bool IsWindowOnCurrentDesktop(IWindow window) =>
		_virtualDesktops.IsWindowOnCurrentDesktop(window.Handle);

	public Guid GetCurrentDesktopId(IntPtr foregroundHandle) =>
		_virtualDesktops.GetCurrentDesktopId(Windows, foregroundHandle);

	public IWindowsDeferPosHandle DeferWindowsPos(int count) => new WindowsDeferPosHandle(Win32.BeginDeferWindowPos(count));

	public void ToggleFocusedWindowTiling()
	{
		if (!_active)
			return;

		lock (_stateLock)
		{
			if (!_active)
				return;

			var window = _windows.Values.FirstOrDefault(candidate => candidate.IsFocused);
			if (window is null)
				return;

			if (_floating.TryRemove(window, out _))
				HandleWindowAdd(window, false);
			else
			{
				_floating[window] = true;
				HandleWindowRemove(window);
				window.BringToTop();
			}
			window.Focus();
		}
	}

	private void StopCore()
	{
		if (!_active && _eventPumpTask is null)
			return;

		_active = false;
		lock (_hooksLock)
		{
			foreach (var hook in _winEventHooks)
				Win32.UnhookWinEvent(hook);
			_winEventHooks.Clear();
		}

		_eventInbox?.Complete();
		_eventPumpLifetime?.Cancel();
		try
		{
			_eventPumpTask?.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_eventPumpLifetime?.Dispose();
			_eventPumpLifetime = null;
			_eventPumpTask = null;
			_eventInbox = null;
		}

		lock (_stateLock)
			ResetTrackedState();

		AppLogger.Info("Window tracking stopped after the event pump exited and all WinEvent hooks were released.");
	}

	private async Task RunEventPumpAsync(WindowEventInbox inbox, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var deferredDelay = GetNextDeferredOperationDelay();
				var eventArrived = await inbox.WaitForWorkAsync(deferredDelay, cancellationToken).ConfigureAwait(false);
				if (eventArrived)
				{
					await Task.Delay(EventPumpIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
					inbox.ClearPendingSignals();
				}

				var batch = inbox.DrainBatch();
				try
				{
					lock (_stateLock)
					{
						if (!_active)
							continue;
						ProcessEventBatch(batch);
						ProcessDeferredOperations();
					}
				}
				catch (Exception ex)
				{
					inbox.RequestReconcile();
					AppLogger.Error("The window event pump failed to process a batch; a full reconciliation was requested.", ex);
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private TimeSpan? GetNextDeferredOperationDelay()
	{
		lock (_stateLock)
		{
			if (!_active)
				return null;

			var nextTimestamp = _desktopSwitchDueTimestamp > 0
				? _desktopSwitchDueTimestamp
				: long.MaxValue;
			foreach (var registration in _pendingRegistrations.Values)
				nextTimestamp = Math.Min(nextTimestamp, registration.NextAttemptTimestamp);
			if (nextTimestamp == long.MaxValue)
				return null;

			var remainingTicks = nextTimestamp - Stopwatch.GetTimestamp();
			return remainingTicks <= 0
				? TimeSpan.Zero
				: TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
		}
	}

	private void ProcessEventBatch(WindowEventBatch batch)
	{
		var requiresReconcile = batch.RequiresReconcile;
		foreach (var item in batch.Events)
		{
			try
			{
				ProcessEvent(item);
			}
			catch (Exception ex)
			{
				requiresReconcile = true;
				AppLogger.Error($"Window event {item.Kind} failed for {item.InstanceId.Handle}; reconciliation will repair the catalog.", ex);
			}
		}

		if (requiresReconcile)
		{
			if (batch.RequiresReconcile)
				AppLogger.Warn("The bounded window event queue overflowed; reconciling the complete top-level window set.");
			ReconcileWindowsCore(emitEvent: true);
		}
	}

	private void ProcessEvent(WindowEventEnvelope captured)
	{
		if (captured.Kind == WindowEventKind.DesktopSwitch)
		{
			_desktopSwitchDueTimestamp = AddMilliseconds(Stopwatch.GetTimestamp(), DesktopSwitchDelayMilliseconds);
			return;
		}

		if (!AcceptGeneration(captured.InstanceId))
			return;

		var item = ResolveEventIdentity(captured);
		switch (item.Kind)
		{
			case WindowEventKind.Create:
				ScheduleRegistration(item.InstanceId);
				break;
			case WindowEventKind.Destroy:
				_rejectedWindowRetryDue.TryRemove(item.InstanceId.Handle, out _);
				CancelRegistrations(item.InstanceId.Handle, item.InstanceId.Generation);
				UnregisterWindow(item.InstanceId.Handle, item.InstanceId.Generation);
				ReleaseDestroyedGeneration(item.InstanceId);
				break;
			case WindowEventKind.Show:
				UpdateWindow(item.InstanceId, WindowUpdateType.Show);
				break;
			case WindowEventKind.Hide:
			case WindowEventKind.Cloaked:
				UpdateWindow(item.InstanceId, WindowUpdateType.Hide);
				break;
			case WindowEventKind.Uncloaked:
				UpdateWindow(item.InstanceId, WindowUpdateType.Show);
				break;
			case WindowEventKind.MinimizeStart:
				UpdateWindow(item.InstanceId, WindowUpdateType.MinimizeStart);
				break;
			case WindowEventKind.MinimizeEnd:
				UpdateWindow(item.InstanceId, WindowUpdateType.MinimizeEnd);
				break;
			case WindowEventKind.Foreground:
				UpdateWindow(item.InstanceId, WindowUpdateType.Foreground);
				break;
			case WindowEventKind.MoveStart:
				StartWindowMove(item.InstanceId);
				break;
			case WindowEventKind.MoveEnd:
				EndWindowMove(item.InstanceId);
				break;
			case WindowEventKind.LocationChanged:
				WindowMove(item.InstanceId);
				break;
			case WindowEventKind.NameChanged:
				UpdateWindow(item.InstanceId, WindowUpdateType.NameChanged);
				break;
			case WindowEventKind.StyleChanged:
				UpdateWindow(item.InstanceId, WindowUpdateType.StyleChanged);
				break;
		}

		ReleaseGenerationIfWindowGone(item.InstanceId);
	}

	private void ProcessDeferredOperations()
	{
		var now = Stopwatch.GetTimestamp();
		ProcessPendingRegistrations(now);

		if (_desktopSwitchDueTimestamp == 0 || now < _desktopSwitchDueTimestamp)
			return;

		_desktopSwitchDueTimestamp = 0;
		foreach (var window in _windows.Values)
			window.RefreshIdentity(_virtualDesktops);
		ReconcileWindowsCore(emitEvent: true);
		DesktopChanged?.Invoke(this, EventArgs.Empty);
	}

	private void RegisterWinEventHook(Win32.EVENT_CONSTANTS eventMin, Win32.EVENT_CONSTANTS eventMax)
	{
		var hook = Win32.SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _hookDelegate, 0, 0,
			(uint)(Win32.EVENT_CONSTANTS.WINEVENT_SKIPOWNPROCESS | Win32.EVENT_CONSTANTS.WINEVENT_OUTOFCONTEXT));
		if (hook != IntPtr.Zero)
		{
			lock (_hooksLock)
				_winEventHooks.Add(hook);
		}
		else
			AppLogger.Warn($"SetWinEventHook failed for {eventMin}..{eventMax}.");
	}

	private void WindowHook(
		IntPtr hookHandle,
		Win32.EVENT_CONSTANTS eventType,
		IntPtr hwnd,
		Win32.OBJID idObject,
		int idChild,
		uint eventThread,
		uint eventTime)
	{
		if (!_active || !TryMapEvent(eventType, out var kind))
			return;

		if (kind != WindowEventKind.DesktopSwitch && !EventWindowIsValid(idChild, idObject, hwnd))
			return;

		var inbox = _eventInbox;
		try
		{
			var instance = kind == WindowEventKind.DesktopSwitch
				? default
				: CaptureCallbackIdentity(hwnd, kind);
			var item = new WindowEventEnvelope(
				Interlocked.Increment(ref _eventSequence),
				kind,
				instance,
				(int)idObject,
				idChild,
				eventThread,
				eventTime);
			inbox?.TryWrite(item);
		}
		catch
		{
			// Never allow an exception to cross the unmanaged WinEvent callback.
			inbox?.RequestReconcile();
		}
	}

	private WindowInstanceId CaptureCallbackIdentity(IntPtr handle, WindowEventKind kind)
	{
		var state = kind switch
		{
			WindowEventKind.Destroy => MarkWindowDestroyed(handle),
			WindowEventKind.Create or WindowEventKind.Show => MarkWindowAlive(handle),
			_ => MarkWindowObserved(handle),
		};

		if (_windowInstances.TryGetValue(handle, out var known) && known.Generation == state.Generation)
			return known;

		Win32.GetWindowThreadProcessId(handle, out var processId);
		return new WindowInstanceId(handle, unchecked((int)processId), null, state.Generation);
	}

	private static bool TryMapEvent(Win32.EVENT_CONSTANTS eventType, out WindowEventKind kind)
	{
		kind = eventType switch
		{
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_CREATE => WindowEventKind.Create,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY => WindowEventKind.Destroy,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW => WindowEventKind.Show,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE => WindowEventKind.Hide,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED => WindowEventKind.Cloaked,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED => WindowEventKind.Uncloaked,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE => WindowEventKind.NameChanged,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_STATECHANGE => WindowEventKind.StyleChanged,
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART => WindowEventKind.MinimizeStart,
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND => WindowEventKind.MinimizeEnd,
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND => WindowEventKind.Foreground,
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART => WindowEventKind.MoveStart,
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND => WindowEventKind.MoveEnd,
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE => WindowEventKind.LocationChanged,
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_DESKTOPSWITCH => WindowEventKind.DesktopSwitch,
			_ => default,
		};

		return eventType is
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_CREATE or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_STATECHANGE or
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART or
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND or
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND or
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART or
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND or
			Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE or
			Win32.EVENT_CONSTANTS.EVENT_SYSTEM_DESKTOPSWITCH;
	}

	private static bool EventWindowIsValid(int idChild, Win32.OBJID idObject, IntPtr hwnd) =>
		idChild == Win32.CHILDID_SELF && idObject == Win32.OBJID.OBJID_WINDOW && hwnd != IntPtr.Zero;

	private bool AcceptGeneration(WindowInstanceId instance)
	{
		if (!_processedGenerations.TryGetValue(instance.Handle, out var current))
		{
			_processedGenerations[instance.Handle] = instance.Generation;
			return true;
		}

		if (instance.Generation < current)
			return false;
		if (instance.Generation == current)
			return true;

		CancelRegistrations(instance.Handle, instance.Generation - 1);
		UnregisterWindow(instance.Handle, current);
		_processedGenerations[instance.Handle] = instance.Generation;
		return true;
	}

	private WindowEventEnvelope ResolveEventIdentity(WindowEventEnvelope item)
	{
		if (_windowInstances.TryGetValue(item.InstanceId.Handle, out var known) &&
			known.Generation == item.InstanceId.Generation)
			return item with { InstanceId = known };

		var processId = item.InstanceId.ProcessId;
		if (processId <= 0 && Win32.IsWindow(item.InstanceId.Handle))
		{
			Win32.GetWindowThreadProcessId(item.InstanceId.Handle, out var nativeProcessId);
			processId = unchecked((int)nativeProcessId);
		}

		var startTime = item.InstanceId.ProcessStartTimeUtc ?? ResolveProcessStartTimeUtc(processId);
		return item with
		{
			InstanceId = item.InstanceId with
			{
				ProcessId = processId,
				ProcessStartTimeUtc = startTime,
			},
		};
	}

	private void ReconcileWindowsCore(bool emitEvent)
	{
		var observed = new HashSet<IntPtr>();
		var now = Stopwatch.GetTimestamp();
		Win32.EnumWindows((handle, state) =>
		{
			observed.Add(handle);
			var generation = MarkWindowAlive(handle).Generation;
			if (!_processedGenerations.TryGetValue(handle, out var processed) || generation > processed)
			{
				if (processed > 0)
					UnregisterWindow(handle, processed);
				_processedGenerations[handle] = generation;
			}

			if (!_windows.ContainsKey(handle) &&
				_rejectedWindowRetryDue.TryGetValue(handle, out var retryDue) &&
				retryDue > now)
				return true;

			RegisterWindow(CreateUnresolvedInstance(handle, generation), emitEvent);
			if (_windows.ContainsKey(handle))
				_rejectedWindowRetryDue.TryRemove(handle, out _);
			else
				_rejectedWindowRetryDue[handle] = AddMilliseconds(now, RejectedWindowCalibrationDelayMilliseconds);
			return true;
		}, IntPtr.Zero);
		foreach (var rejectedHandle in _rejectedWindowRetryDue.Keys.Where(handle => !observed.Contains(handle)).ToArray())
			_rejectedWindowRetryDue.TryRemove(rejectedHandle, out _);

		foreach (var pair in _windows.ToArray())
		{
			if (!observed.Contains(pair.Key))
			{
				var trackedGeneration = GetTrackedGeneration(pair.Key);
				var destroyedGeneration = MarkWindowDestroyed(pair.Key).Generation;
				UnregisterWindow(pair.Key, trackedGeneration);
				ReleaseDestroyedGeneration(new WindowInstanceId(pair.Key, pair.Value.ProcessId, null, destroyedGeneration));
			}
			else if (!_classifier.IsCandidate(pair.Value, out _))
				UnregisterWindow(pair.Key, GetTrackedGeneration(pair.Key));
		}
	}

	private WindowInstanceId CreateUnresolvedInstance(IntPtr handle, long generation)
	{
		Win32.GetWindowThreadProcessId(handle, out var processId);
		return new WindowInstanceId(handle, unchecked((int)processId), null, generation);
	}

	private bool RegisterWindow(WindowInstanceId requestedInstance, bool emitEvent)
	{
		var handle = requestedInstance.Handle;
		if (!_active || handle == IntPtr.Zero || !Win32.IsWindow(handle))
			return true;
		if (_processedGenerations.TryGetValue(handle, out var processed) && processed != requestedInstance.Generation)
			return true;

		if (_windows.TryGetValue(handle, out _) &&
			_windowInstances.TryGetValue(handle, out var existingInstance) &&
			existingInstance.Generation == requestedInstance.Generation)
			return true;

		if (_windows.ContainsKey(handle))
			UnregisterWindow(handle, GetTrackedGeneration(handle));

		var window = new WindowsWindow(handle, _virtualDesktops);
		if (window.ProcessId == _currentProcessId)
			return true;
		if (window.ProcessId < 0)
			return false;
		if (!_classifier.IsCandidate(window, out _))
			return false;

		var resolvedInstance = new WindowInstanceId(
			handle,
			window.ProcessId,
			ResolveProcessStartTimeUtc(window.ProcessId),
			requestedInstance.Generation);
		window.WindowFocused += HandleWindowFocused;
		window.WindowUpdated += HandleWindowUpdated;
		window.WindowClosed += HandleWindowClosed;
		if (!_windows.TryAdd(handle, window))
		{
			window.WindowFocused -= HandleWindowFocused;
			window.WindowUpdated -= HandleWindowUpdated;
			window.WindowClosed -= HandleWindowClosed;
			return true;
		}

		_windowInstances[handle] = resolvedInstance;
		_pendingRegistrations.Remove(new WindowInstanceKey(handle, requestedInstance.Generation));
		if (emitEvent)
			HandleWindowAdd(window, true);
		return true;
	}

	private void ScheduleRegistration(WindowInstanceId instance)
	{
		if (!_active || instance.Handle == IntPtr.Zero)
			return;
		if (_windowInstances.TryGetValue(instance.Handle, out var existing) && existing.Generation == instance.Generation)
			return;
		_rejectedWindowRetryDue.TryRemove(instance.Handle, out _);

		CancelRegistrations(instance.Handle, instance.Generation - 1);
		var key = new WindowInstanceKey(instance.Handle, instance.Generation);
		if (_pendingRegistrations.ContainsKey(key))
			return;

		_pendingRegistrations[key] = new PendingRegistration(
			instance,
			0,
			AddMilliseconds(Stopwatch.GetTimestamp(), RegistrationRetryDelaysMilliseconds[0]));
	}

	private void ProcessPendingRegistrations(long now)
	{
		foreach (var pair in _pendingRegistrations.ToArray())
		{
			if (pair.Value.NextAttemptTimestamp > now)
				continue;
			if (!_processedGenerations.TryGetValue(pair.Key.Handle, out var generation) || generation != pair.Key.Generation)
			{
				_pendingRegistrations.Remove(pair.Key);
				continue;
			}

			if (RegisterWindow(pair.Value.InstanceId, emitEvent: true) || !Win32.IsWindow(pair.Key.Handle))
			{
				_pendingRegistrations.Remove(pair.Key);
				continue;
			}

			var nextAttempt = pair.Value.AttemptIndex + 1;
			if (nextAttempt >= RegistrationRetryDelaysMilliseconds.Length)
			{
				_pendingRegistrations.Remove(pair.Key);
				continue;
			}

			_pendingRegistrations[pair.Key] = pair.Value with
			{
				AttemptIndex = nextAttempt,
				NextAttemptTimestamp = AddMilliseconds(now, RegistrationRetryDelaysMilliseconds[nextAttempt]),
			};
		}
	}

	private void CancelRegistrations(IntPtr handle, long throughGeneration)
	{
		foreach (var key in _pendingRegistrations.Keys
			.Where(key => key.Handle == handle && key.Generation <= throughGeneration)
			.ToArray())
			_pendingRegistrations.Remove(key);
	}

	private void UnregisterWindow(IntPtr handle, long expectedGeneration)
	{
		if (_windowInstances.TryGetValue(handle, out var instance) && instance.Generation != expectedGeneration)
			return;

		_windowInstances.TryRemove(handle, out _);
		if (!_windows.TryRemove(handle, out var window))
			return;
		window.WindowFocused -= HandleWindowFocused;
		window.WindowUpdated -= HandleWindowUpdated;
		window.WindowClosed -= HandleWindowClosed;
		_floating.TryRemove(window, out _);
		HandleWindowRemove(window);
	}

	private void UpdateWindow(WindowInstanceId instance, WindowUpdateType type)
	{
		if (!_active)
			return;

		if (_windows.TryGetValue(instance.Handle, out var window) &&
			_windowInstances.TryGetValue(instance.Handle, out var tracked) &&
			tracked.Generation == instance.Generation)
		{
			if (type == WindowUpdateType.Hide && !Win32.IsWindow(instance.Handle))
				UnregisterWindow(instance.Handle, instance.Generation);
			else if (type is WindowUpdateType.NameChanged or WindowUpdateType.StyleChanged &&
				!_classifier.IsCandidate(window, out _))
			{
				_rejectedWindowRetryDue[instance.Handle] = AddMilliseconds(
					Stopwatch.GetTimestamp(),
					RejectedWindowCalibrationDelayMilliseconds);
				UnregisterWindow(instance.Handle, instance.Generation);
			}
			else
				WindowUpdated?.Invoke(window, type);
		}
		else if (type is WindowUpdateType.Show or WindowUpdateType.NameChanged or WindowUpdateType.StyleChanged)
			ScheduleRegistration(instance);
		else if (type == WindowUpdateType.Foreground)
		{
			ScheduleRegistration(instance);
			UntrackedFocus?.Invoke(this, instance.Handle);
		}
	}

	private void StartWindowMove(WindowInstanceId instance)
	{
		if (!TryGetTrackedWindow(instance, out var window))
			return;
		window.StoreLastLocation();
		lock (_mouseMoveLock)
		{
			if (_mouseMoveWindow is not null)
				_mouseMoveWindow.IsMouseMoving = false;
			_mouseMoveWindow = window;
			window.IsMouseMoving = true;
		}
		WindowUpdated?.Invoke(window, WindowUpdateType.MoveStart);
	}

	private void EndWindowMove(WindowInstanceId instance)
	{
		if (!TryGetTrackedWindow(instance, out var window))
			return;
		lock (_mouseMoveLock)
		{
			if (_mouseMoveWindow is not null)
				_mouseMoveWindow.IsMouseMoving = false;
			_mouseMoveWindow = null;
		}
		WindowUpdated?.Invoke(window, WindowUpdateType.MoveEnd);
		WindowMoved?.Invoke(window, EventArgs.Empty);
	}

	private void WindowMove(WindowInstanceId instance)
	{
		lock (_mouseMoveLock)
		{
			if (_mouseMoveWindow is not null &&
				_mouseMoveWindow.Handle == instance.Handle &&
				_windowInstances.TryGetValue(instance.Handle, out var tracked) &&
				tracked.Generation == instance.Generation)
				WindowUpdated?.Invoke(_mouseMoveWindow, WindowUpdateType.Move);
		}
	}

	private bool TryGetTrackedWindow(WindowInstanceId instance, out WindowsWindow window)
	{
		if (_windows.TryGetValue(instance.Handle, out window!) &&
			_windowInstances.TryGetValue(instance.Handle, out var tracked) &&
			tracked.Generation == instance.Generation)
			return true;

		window = null!;
		return false;
	}

	private WindowGenerationState MarkWindowAlive(IntPtr handle) =>
		_windowGenerations.AddOrUpdate(
			handle,
			_ => new WindowGenerationState(Interlocked.Increment(ref _nextWindowGeneration), true),
			(_, current) => current.IsAlive
				? current
				: new WindowGenerationState(Interlocked.Increment(ref _nextWindowGeneration), true));

	private WindowGenerationState MarkWindowDestroyed(IntPtr handle) =>
		_windowGenerations.AddOrUpdate(
			handle,
			_ => new WindowGenerationState(Interlocked.Increment(ref _nextWindowGeneration), false),
			static (_, current) => current with { IsAlive = false });

	private WindowGenerationState MarkWindowObserved(IntPtr handle)
	{
		if (_windowGenerations.TryGetValue(handle, out var state))
			return state;
		return MarkWindowAlive(handle);
	}

	private void ReleaseGenerationIfWindowGone(WindowInstanceId instance)
	{
		if (Win32.IsWindow(instance.Handle))
			return;
		if (!_windowGenerations.TryGetValue(instance.Handle, out var state))
		{
			if (_processedGenerations.TryGetValue(instance.Handle, out var processed) &&
				processed == instance.Generation)
				_processedGenerations.Remove(instance.Handle);
			return;
		}
		if (state.Generation != instance.Generation)
			return;

		if (state.IsAlive)
		{
			var destroyed = state with { IsAlive = false };
			if (!_windowGenerations.TryUpdate(instance.Handle, destroyed, state))
				return;
		}

		ReleaseDestroyedGeneration(instance);
	}

	private void ReleaseDestroyedGeneration(WindowInstanceId instance)
	{
		if (!_windowGenerations.TryGetValue(instance.Handle, out var state) ||
			state.IsAlive || state.Generation != instance.Generation)
			return;

		var removed = ((ICollection<KeyValuePair<IntPtr, WindowGenerationState>>)_windowGenerations)
			.Remove(new KeyValuePair<IntPtr, WindowGenerationState>(instance.Handle, state));
		if (removed &&
			_processedGenerations.TryGetValue(instance.Handle, out var processed) &&
			processed == instance.Generation)
			_processedGenerations.Remove(instance.Handle);
	}

	private long GetTrackedGeneration(IntPtr handle)
	{
		if (_windowInstances.TryGetValue(handle, out var instance))
			return instance.Generation;
		return _processedGenerations.TryGetValue(handle, out var generation) ? generation : 0;
	}

	private static DateTimeOffset? ResolveProcessStartTimeUtc(int processId)
	{
		if (processId <= 0)
			return null;

		try
		{
			using var process = Process.GetProcessById(processId);
			return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
		}
		catch
		{
			return null;
		}
	}

	private void ResetTrackedState()
	{
		foreach (var window in _windows.Values)
		{
			window.WindowFocused -= HandleWindowFocused;
			window.WindowUpdated -= HandleWindowUpdated;
			window.WindowClosed -= HandleWindowClosed;
			window.IsMouseMoving = false;
		}

		_windows.Clear();
		_windowInstances.Clear();
		_windowGenerations.Clear();
		_rejectedWindowRetryDue.Clear();
		_processedGenerations.Clear();
		_pendingRegistrations.Clear();
		_floating.Clear();
		_nextWindowGeneration = 0;
		_desktopSwitchDueTimestamp = 0;
		lock (_mouseMoveLock)
			_mouseMoveWindow = null;
	}

	private static long AddMilliseconds(long timestamp, int milliseconds) =>
		timestamp + (long)(Stopwatch.Frequency * (milliseconds / 1000d));

	private void HandleWindowFocused(IWindow window) => WindowFocused?.Invoke(window);
	private void HandleWindowUpdated(IWindow window) => ExternalWindowUpdate?.Invoke(window);
	private void HandleWindowClosed(IWindow window) => ExternalWindowClosed?.Invoke(window);
	private void HandleWindowAdd(IWindow window, bool firstCreate) => WindowCreated?.Invoke(window, firstCreate);
	private void HandleWindowRemove(IWindow window) => WindowDestroyed?.Invoke(window);

	private readonly record struct WindowGenerationState(long Generation, bool IsAlive);
	private readonly record struct WindowInstanceKey(IntPtr Handle, long Generation);
	private sealed record PendingRegistration(WindowInstanceId InstanceId, int AttemptIndex, long NextAttemptTimestamp);
}
