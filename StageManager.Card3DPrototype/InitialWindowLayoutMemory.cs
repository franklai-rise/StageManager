using StageManager.Native.Window;
using System.Drawing;
using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

internal readonly record struct InitialWindowLayout(Rectangle NormalBounds, bool WasMaximized);

/// <summary>
/// Remembers the first usable layout observed for each native window lifetime.
/// Later move/resize events deliberately do not replace that baseline.
/// </summary>
internal sealed class InitialWindowLayoutMemory
{
	private const int WindowPlacementRestoreToMaximized = 0x0002;
	private readonly Dictionary<IntPtr, Entry> _entries = new();
	private readonly Func<IWindow, InitialWindowLayout?> _capture;
	private readonly Func<IWindow, InitialWindowLayout, bool> _restore;
	private readonly Func<IntPtr, bool> _isWindowAlive;

	public InitialWindowLayoutMemory()
		: this(CaptureNativeLayout, RestoreNativeLayout)
	{
	}

	internal InitialWindowLayoutMemory(
		Func<IWindow, InitialWindowLayout?> capture,
		Func<IWindow, InitialWindowLayout, bool> restore,
		Func<IntPtr, bool>? isWindowAlive = null)
	{
		_capture = capture ?? throw new ArgumentNullException(nameof(capture));
		_restore = restore ?? throw new ArgumentNullException(nameof(restore));
		_isWindowAlive = isWindowAlive ?? NativeMethods.IsWindow;
	}

	internal int Count => _entries.Count;

	public void Observe(IEnumerable<IWindow> windows)
	{
		ArgumentNullException.ThrowIfNull(windows);
		var liveWindows = new Dictionary<IntPtr, IWindow>();
		foreach (var window in windows)
		{
			if (window.Handle == IntPtr.Zero)
				continue;

			liveWindows[window.Handle] = window;
			if (TryGetEntry(window, out _))
				continue;

			_entries.Remove(window.Handle);
			if (_capture(window) is { } layout &&
				layout.NormalBounds.Width > 0 && layout.NormalBounds.Height > 0)
			{
				_entries[window.Handle] = new Entry(window.ProcessId, window, layout);
			}
		}

		foreach (var pair in _entries.ToArray())
		{
			if (liveWindows.TryGetValue(pair.Key, out var liveWindow))
			{
				if (!Matches(pair.Value, liveWindow))
					_entries.Remove(pair.Key);
				continue;
			}

			// Windows on another virtual desktop or temporarily hidden in the tray
			// are absent from the current card list but still belong to this lifetime.
			if (!_isWindowAlive(pair.Key))
				_entries.Remove(pair.Key);
		}
	}

	public bool HasSnapshot(IWindow window) => TryGetEntry(window, out _);

	public bool TryRestore(IWindow window)
	{
		if (!TryGetEntry(window, out var entry))
			return false;
		return _restore(window, entry.Layout);
	}

	public void Clear() => _entries.Clear();

	private bool TryGetEntry(IWindow window, out Entry entry)
	{
		if (_entries.TryGetValue(window.Handle, out entry!) && Matches(entry, window))
			return true;
		entry = null!;
		return false;
	}

	private static bool Matches(Entry entry, IWindow window) =>
		entry.ProcessId == window.ProcessId && ReferenceEquals(entry.Window, window);

	private static InitialWindowLayout? CaptureNativeLayout(IWindow window)
	{
		if (!NativeMethods.IsWindow(window.Handle))
			return null;

		var wasMaximized = window.IsMaximized;
		var placement = new NativeWindowPlacement
		{
			Length = Marshal.SizeOf<NativeWindowPlacement>()
		};
		if (NativeMethods.GetWindowPlacement(window.Handle, ref placement))
		{
			var normalBounds = ToRectangle(placement.NormalPosition);
			if (normalBounds.Width > 0 && normalBounds.Height > 0)
				return new InitialWindowLayout(
					normalBounds,
					wasMaximized ||
					placement.ShowCommand == NativeMethods.SwShowMaximized ||
					(placement.Flags & WindowPlacementRestoreToMaximized) != 0);
		}

		if (!NativeMethods.GetWindowRect(window.Handle, out var currentRectangle))
			return null;
		var currentBounds = ToRectangle(currentRectangle);
		return currentBounds.Width > 0 && currentBounds.Height > 0
			? new InitialWindowLayout(currentBounds, wasMaximized)
			: null;
	}

	private static bool RestoreNativeLayout(IWindow window, InitialWindowLayout layout)
	{
		if (!NativeMethods.IsWindow(window.Handle) ||
			layout.NormalBounds.Width <= 0 || layout.NormalBounds.Height <= 0)
		{
			return false;
		}

		var placement = new NativeWindowPlacement
		{
			Length = Marshal.SizeOf<NativeWindowPlacement>()
		};
		if (NativeMethods.GetWindowPlacement(window.Handle, ref placement))
		{
			placement.NormalPosition = ToNativeRect(layout.NormalBounds);
			placement.Flags = layout.WasMaximized
				? placement.Flags | WindowPlacementRestoreToMaximized
				: placement.Flags & ~WindowPlacementRestoreToMaximized;
			placement.ShowCommand = layout.WasMaximized
				? NativeMethods.SwShowMaximized
				: NativeMethods.SwRestore;
			if (NativeMethods.SetWindowPlacement(window.Handle, ref placement))
			{
				NativeMethods.ShowWindowAsync(window.Handle, placement.ShowCommand);
				window.NotifyUpdated();
				return true;
			}
		}

		NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwRestore);
		var restored = NativeMethods.SetWindowPos(
			window.Handle,
			IntPtr.Zero,
			layout.NormalBounds.Left,
			layout.NormalBounds.Top,
			layout.NormalBounds.Width,
			layout.NormalBounds.Height,
			NativeMethods.SwpNoZOrder |
			NativeMethods.SwpNoActivate |
			NativeMethods.SwpAsyncWindowPos);
		if (!restored)
			return false;
		if (layout.WasMaximized)
			NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwShowMaximized);
		window.NotifyUpdated();
		return true;
	}

	private static Rectangle ToRectangle(NativeRect rectangle) =>
		Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

	private static NativeRect ToNativeRect(Rectangle rectangle) => new()
	{
		Left = rectangle.Left,
		Top = rectangle.Top,
		Right = rectangle.Right,
		Bottom = rectangle.Bottom
	};

	private sealed record Entry(int ProcessId, IWindow Window, InitialWindowLayout Layout);
}
