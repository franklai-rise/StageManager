using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

internal sealed class DesktopIconVisibilityService
{
	private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
	private const string HideIconsValue = "HideIcons";
	private const uint WmCommand = 0x0111;
	private const int ToggleDesktopIconsCommand = 0x7402;
	private const uint SmtoAbortIfHung = 0x0002;
	private readonly Func<bool?> _queryVisibility;
	private readonly Func<bool> _toggleVisibility;

	public DesktopIconVisibilityService()
		: this(QueryRegistryVisibility, ToggleShellDesktopIcons)
	{
	}

	internal DesktopIconVisibilityService(Func<bool?> queryVisibility, Func<bool> toggleVisibility)
	{
		_queryVisibility = queryVisibility;
		_toggleVisibility = toggleVisibility;
		IconsVisible = QueryVisibilitySafely() ?? true;
	}

	public bool IconsVisible { get; private set; }

	public bool Refresh()
	{
		var current = QueryVisibilitySafely();
		if (current is null || current.Value == IconsVisible)
			return false;
		IconsVisible = current.Value;
		return true;
	}

	private bool? QueryVisibilitySafely()
	{
		try { return _queryVisibility(); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			// A transient registry failure must not close the sidebar or change
			// the desktop. Retain the last observed state until a later refresh.
			return null;
		}
	}

	public bool TryToggle(out string? error)
	{
		error = null;
		try
		{
			var previous = IconsVisible;
			if (!_toggleVisibility())
			{
				error = "The Windows desktop view did not accept the toggle request.";
				return false;
			}

			var observed = QueryVisibilitySafely();
			// Explorer may persist HideIcons a fraction later than it updates the
			// desktop view. Give immediate visual feedback and let Refresh reconcile.
			IconsVisible = observed is not null && observed.Value != previous
				? observed.Value
				: !previous;
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private static bool? QueryRegistryVisibility()
	{
		using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: false);
		var hidden = key?.GetValue(HideIconsValue, 0) is int value && value != 0;
		return !hidden;
	}

	private static bool ToggleShellDesktopIcons()
	{
		var desktopView = FindDesktopView();
		if (desktopView == IntPtr.Zero)
			return false;
		return SendMessageTimeout(
			desktopView,
			WmCommand,
			(IntPtr)ToggleDesktopIconsCommand,
			IntPtr.Zero,
			SmtoAbortIfHung,
			1000,
			out _) != IntPtr.Zero;
	}

	private static IntPtr FindDesktopView()
	{
		var progman = FindWindow("Progman", null);
		var view = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
		if (view != IntPtr.Zero)
			return view;

		EnumWindows((window, _) =>
		{
			view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
			return view == IntPtr.Zero;
		}, IntPtr.Zero);
		return view;
	}

	private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string? className, string? windowName);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SendMessageTimeout(
		IntPtr window,
		uint message,
		IntPtr wParam,
		IntPtr lParam,
		uint flags,
		uint timeout,
		out UIntPtr result);
}
