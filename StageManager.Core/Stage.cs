using StageManager.Model;
using StageManager.Native.Window;
using StageManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StageManager;

[System.Diagnostics.DebuggerDisplay("{Title}")]
public class Stage
{
	private readonly List<IWindow> _windows = new();
	private readonly Dictionary<IntPtr, WindowLayoutSnapshot> _layoutSnapshots = new();
	private readonly HashSet<IntPtr> _minimizedByManager = new();
	private bool _selected;
	private int _cycleIndex = -1;

	public Stage(string initialAppKey, params IWindow[] windows)
		: this(Guid.NewGuid(), initialAppKey, DateTime.UtcNow, windows)
	{
	}

	internal Stage(Guid id, string initialAppKey, DateTime lastActivatedUtc, params IWindow[] windows)
	{
		Id = id;
		InitialAppKey = initialAppKey;
		foreach (var window in windows)
			Add(window);
		LastActivatedUtc = lastActivatedUtc;
	}

	public event EventHandler? SelectedChanged;

	public Guid Id { get; }
	public string InitialAppKey { get; }
	public IReadOnlyList<IWindow> Windows => _windows;
	public DateTime LastActivatedUtc { get; private set; }
	public string Title => string.Join(" + ", _windows.Select(window => window.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase));
	public int WindowCount => _windows.Count;
	public bool HasFocus => _windows.Any(window => window.IsFocused);

	public bool IsSelected
	{
		get => _selected;
		set
		{
			if (_selected == value)
				return;
			_selected = value;
			SelectedChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	public bool ContainsWindow(IntPtr handle) => _windows.Any(window => window.Handle == handle);
	public bool ContainsApp(string appKey) => _windows.Any(window => string.Equals(GetAppKey(window), appKey, StringComparison.OrdinalIgnoreCase));

	public void Add(IWindow window)
	{
		if (_windows.Any(existing => existing.Handle == window.Handle))
			return;
		_windows.Add(window);
		Touch();
	}

	public void Remove(IWindow window)
	{
		Detach(window);
	}

	internal StageWindowTransferState Detach(IWindow window)
	{
		_layoutSnapshots.TryGetValue(window.Handle, out var layout);
		var wasMinimizedByManager = _minimizedByManager.Contains(window.Handle);
		_windows.RemoveAll(existing => existing.Handle == window.Handle);
		_layoutSnapshots.Remove(window.Handle);
		_minimizedByManager.Remove(window.Handle);
		_cycleIndex = Math.Min(_cycleIndex, _windows.Count - 1);
		Touch();
		return new StageWindowTransferState(layout, wasMinimizedByManager);
	}

	internal void Attach(IWindow window, StageWindowTransferState state)
	{
		Add(window);
		if (state.Layout is not null)
			_layoutSnapshots[window.Handle] = state.Layout;
		if (state.WasMinimizedByManager)
			_minimizedByManager.Add(window.Handle);
	}

	public void Touch() => LastActivatedUtc = DateTime.UtcNow;

	public void CaptureLayouts(DisplayTopologyService displays)
	{
		var zOrders = displays.CaptureZOrder(_windows);
		foreach (var window in _windows)
		{
			if (!window.IsMinimized)
				_layoutSnapshots[window.Handle] = displays.Capture(
					window,
					zOrders.TryGetValue(window.Handle, out var zOrder) ? zOrder : int.MaxValue);
		}
	}

	public bool TryGetLayout(IntPtr handle, out WindowLayoutSnapshot? snapshot)
	{
		var found = _layoutSnapshots.TryGetValue(handle, out var value);
		snapshot = value;
		return found;
	}

	public void MarkMinimizedByManager(IntPtr handle) => _minimizedByManager.Add(handle);
	public bool WasMinimizedByManager(IntPtr handle) => _minimizedByManager.Contains(handle);
	public void ClearManagerMinimized(IntPtr handle) => _minimizedByManager.Remove(handle);

	internal void RestoreLayoutSnapshot(WindowLayoutSnapshot snapshot) =>
		_layoutSnapshots[snapshot.Handle] = snapshot;

	internal int GetCapturedZOrder(IntPtr handle) =>
		_layoutSnapshots.TryGetValue(handle, out var snapshot) ? snapshot.ZOrder : int.MaxValue;

	public IWindow? GetNextWindow()
	{
		if (_windows.Count == 0)
			return null;
		_cycleIndex = (_cycleIndex + 1) % _windows.Count;
		return _windows[_cycleIndex];
	}

	public static string GetAppKey(IWindow window)
	{
		if (!string.IsNullOrWhiteSpace(window.AppUserModelId))
			return window.AppUserModelId!;
		if (!string.IsNullOrWhiteSpace(window.ProcessExecutable))
			return window.ProcessExecutable;
		return window.ProcessName;
	}
}

internal readonly record struct StageWindowTransferState(
	WindowLayoutSnapshot? Layout,
	bool WasMinimizedByManager);
