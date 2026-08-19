using StageManager.Infrastructure;
using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;

namespace StageManager.Services;

public sealed class HotkeyManager : IDisposable
{
	private const int WmHotkey = 0x0312;
	private const uint ModAlt = 0x0001;
	private const uint ModControl = 0x0002;
	private const uint ModShift = 0x0004;
	private const uint ModWin = 0x0008;
	private const uint ModNoRepeat = 0x4000;
	private readonly IntPtr _windowHandle;
	private readonly HwndSource _source;
	private readonly Dictionary<int, Action> _actions = new();
	private int _nextId = 100;

	public HotkeyManager(IntPtr windowHandle)
	{
		_windowHandle = windowHandle;
		_source = HwndSource.FromHwnd(windowHandle) ?? throw new InvalidOperationException("The StageManager window source is unavailable.");
		_source.AddHook(WindowProcedure);
	}

	public bool Register(string gesture, Action action)
	{
		if (!TryParse(gesture, out var modifiers, out var virtualKey))
		{
			AppLogger.Warn($"Invalid hotkey gesture: {gesture}.");
			return false;
		}

		var id = _nextId++;
		if (!Win32.RegisterHotKey(_windowHandle, id, modifiers | ModNoRepeat, virtualKey))
		{
			AppLogger.Warn($"The hotkey {gesture} could not be registered, likely because another app already uses it.");
			return false;
		}
		_actions[id] = action;
		AppLogger.Info($"Registered hotkey {gesture} as id {id}.");
		return true;
	}

	public void Clear()
	{
		foreach (var id in _actions.Keys.ToArray())
			Win32.UnregisterHotKey(_windowHandle, id);
		_actions.Clear();
	}

	public void Dispose()
	{
		Clear();
		_source.RemoveHook(WindowProcedure);
	}

	public static bool TryParse(string gesture, out uint modifiers, out uint virtualKey) =>
		HotkeyGestureParser.TryParse(gesture, out modifiers, out virtualKey);

	private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
		{
			AppLogger.Info($"Hotkey id {wParam.ToInt32()} invoked.");
			action();
			handled = true;
		}
		return IntPtr.Zero;
	}
}
