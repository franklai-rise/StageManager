using StageManager.Infrastructure;
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

public sealed class WindowsManager : IWindowsManager, IDisposable
{
	private static readonly int[] RegistrationRetryDelaysMilliseconds = [140, 360, 900, 1800];
	private readonly ConcurrentDictionary<IntPtr, WindowsWindow> _windows = new();
	private readonly ConcurrentDictionary<IntPtr, CancellationTokenSource> _pendingRegistrations = new();
	private readonly ConcurrentDictionary<IntPtr, long> _lastForegroundEvents = new();
	private readonly ConcurrentDictionary<WindowsWindow, bool> _floating = new();
	private readonly List<IntPtr> _winEventHooks = new();
	private readonly object _hooksLock = new();
	private readonly object _mouseMoveLock = new();
	private readonly IWindowClassifier _classifier;
	private readonly VirtualDesktopService _virtualDesktops;
	private readonly WinEventDelegate _hookDelegate;
	private CancellationTokenSource _lifetime = new();
	private WindowsWindow? _mouseMoveWindow;
	private volatile bool _active;
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
		if (_active)
			return Task.CompletedTask;

		_active = true;
		_lifetime = new CancellationTokenSource();
		_currentProcessId = Environment.ProcessId;

		RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY, Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE);
		RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED, Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED);
		RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND);
		RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND);
		RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND);
		RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE);
		RegisterWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_DESKTOPSWITCH, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_DESKTOPSWITCH);

		EnumerateWindows(emitEvent: false);
		AppLogger.Info($"Window tracking started with {_windows.Count} candidates.");
		return Task.CompletedTask;
	}

	public void Stop()
	{
		if (!_active)
			return;

		_active = false;
		_lifetime.Cancel();
		foreach (var pending in _pendingRegistrations.Values)
			pending.Cancel();
		_pendingRegistrations.Clear();

		lock (_hooksLock)
		{
			foreach (var hook in _winEventHooks)
				Win32.UnhookWinEvent(hook);
			_winEventHooks.Clear();
		}

		AppLogger.Info("Window tracking stopped and all WinEvent hooks were released.");
	}

	public void Dispose()
	{
		Stop();
		_lifetime.Dispose();
	}

	public void ReevaluateWindows()
	{
		if (!_active)
			return;

		EnumerateWindows(emitEvent: true);
		foreach (var pair in _windows.ToArray())
		{
			if (!_classifier.IsCandidate(pair.Value, out _))
				UnregisterWindow(pair.Key);
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

	public IWindowsDeferPosHandle DeferWindowsPos(int count) => new WindowsDeferPosHandle(Win32.BeginDeferWindowPos(count));

	public void ToggleFocusedWindowTiling()
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

	private void EnumerateWindows(bool emitEvent)
	{
		Win32.EnumWindows((handle, _) =>
		{
			RegisterWindow(handle, emitEvent);
			return true;
		}, IntPtr.Zero);
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

	private void WindowHook(IntPtr hookHandle, Win32.EVENT_CONSTANTS eventType, IntPtr hwnd, Win32.OBJID idObject, int idChild, uint eventThread, uint eventTime)
	{
		if (!_active)
			return;

		try
		{
			if (eventType == Win32.EVENT_CONSTANTS.EVENT_SYSTEM_DESKTOPSWITCH)
			{
				_ = HandleDesktopSwitchAsync();
				return;
			}

			if (!EventWindowIsValid(idChild, idObject, hwnd))
				return;

			switch (eventType)
			{
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW:
					ScheduleRegistration(hwnd);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY:
					CancelRegistration(hwnd);
					UnregisterWindow(hwnd);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE:
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED:
					UpdateWindow(hwnd, WindowUpdateType.Hide);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED:
					UpdateWindow(hwnd, WindowUpdateType.Show);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART:
					UpdateWindow(hwnd, WindowUpdateType.MinimizeStart);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND:
					UpdateWindow(hwnd, WindowUpdateType.MinimizeEnd);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND:
					if (ShouldEmitForeground(hwnd))
						UpdateWindow(hwnd, WindowUpdateType.Foreground);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART:
					StartWindowMove(hwnd);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND:
					EndWindowMove(hwnd);
					break;
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE:
					WindowMove(hwnd);
					break;
			}
		}
		catch (Exception ex)
		{
			AppLogger.Error($"Window event {eventType} failed for {hwnd}.", ex);
		}
	}

	private async Task HandleDesktopSwitchAsync()
	{
		try
		{
			await Task.Delay(180, _lifetime.Token).ConfigureAwait(false);
			foreach (var window in _windows.Values)
				window.RefreshIdentity(_virtualDesktops);
			ReevaluateWindows();
			DesktopChanged?.Invoke(this, EventArgs.Empty);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void ScheduleRegistration(IntPtr handle)
	{
		if (!_active || handle == IntPtr.Zero || _windows.ContainsKey(handle) || _pendingRegistrations.ContainsKey(handle))
			return;

		var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		if (!_pendingRegistrations.TryAdd(handle, source))
		{
			source.Dispose();
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				foreach (var delay in RegistrationRetryDelaysMilliseconds)
				{
					await Task.Delay(delay, source.Token).ConfigureAwait(false);
					if (RegisterWindow(handle, emitEvent: true) || !Win32.IsWindow(handle))
						return;
				}
			}
			catch (OperationCanceledException)
			{
			}
			finally
			{
				((ICollection<KeyValuePair<IntPtr, CancellationTokenSource>>)_pendingRegistrations)
					.Remove(new KeyValuePair<IntPtr, CancellationTokenSource>(handle, source));
				source.Dispose();
			}
		});
	}

	private void CancelRegistration(IntPtr handle)
	{
		if (_pendingRegistrations.TryRemove(handle, out var source))
			source.Cancel();
	}

	private bool EventWindowIsValid(int idChild, Win32.OBJID idObject, IntPtr hwnd) =>
		idChild == Win32.CHILDID_SELF && idObject == Win32.OBJID.OBJID_WINDOW && hwnd != IntPtr.Zero;

	private bool RegisterWindow(IntPtr handle, bool emitEvent)
	{
		if (!_active || handle == IntPtr.Zero || !Win32.IsWindow(handle))
			return true;
		if (_windows.ContainsKey(handle))
			return true;

		var window = new WindowsWindow(handle, _virtualDesktops);
		if (window.ProcessId == _currentProcessId)
			return true;
		if (window.ProcessId < 0)
			return false;
		if (!_classifier.IsCandidate(window, out _))
			return false;

		window.WindowFocused += HandleWindowFocused;
		window.WindowUpdated += HandleWindowUpdated;
		window.WindowClosed += HandleWindowClosed;
		if (!_windows.TryAdd(handle, window))
			return true;

		if (emitEvent)
			HandleWindowAdd(window, true);
		return true;
	}

	private void UnregisterWindow(IntPtr handle)
	{
		if (!_windows.TryRemove(handle, out var window))
			return;
		window.WindowFocused -= HandleWindowFocused;
		window.WindowUpdated -= HandleWindowUpdated;
		window.WindowClosed -= HandleWindowClosed;
		HandleWindowRemove(window);
	}

	private void UpdateWindow(IntPtr handle, WindowUpdateType type)
	{
		if (!_active)
			return;

		if (_windows.TryGetValue(handle, out var window))
		{
			if (type == WindowUpdateType.Hide && !Win32.IsWindow(handle))
				UnregisterWindow(handle);
			else
				WindowUpdated?.Invoke(window, type);
		}
		else if (type == WindowUpdateType.Show)
			ScheduleRegistration(handle);
		else if (type == WindowUpdateType.Foreground)
		{
			ScheduleRegistration(handle);
			UntrackedFocus?.Invoke(this, handle);
		}
	}

	private bool ShouldEmitForeground(IntPtr handle)
	{
		var now = Stopwatch.GetTimestamp();
		var minimumTicks = Stopwatch.Frequency / 12;
		if (_lastForegroundEvents.TryGetValue(handle, out var previous) && now - previous < minimumTicks)
			return false;
		_lastForegroundEvents[handle] = now;
		return true;
	}

	private void StartWindowMove(IntPtr handle)
	{
		if (!_windows.TryGetValue(handle, out var window))
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

	private void EndWindowMove(IntPtr handle)
	{
		if (!_windows.TryGetValue(handle, out var window))
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

	private void WindowMove(IntPtr handle)
	{
		lock (_mouseMoveLock)
		{
			if (_mouseMoveWindow is not null && _mouseMoveWindow.Handle == handle)
				WindowUpdated?.Invoke(_mouseMoveWindow, WindowUpdateType.Move);
		}
	}

	private void HandleWindowFocused(IWindow window) => WindowFocused?.Invoke(window);
	private void HandleWindowUpdated(IWindow window) => ExternalWindowUpdate?.Invoke(window);
	private void HandleWindowClosed(IWindow window) => ExternalWindowClosed?.Invoke(window);
	private void HandleWindowAdd(IWindow window, bool firstCreate) => WindowCreated?.Invoke(window, firstCreate);
	private void HandleWindowRemove(IWindow window) => WindowDestroyed?.Invoke(window);
}
