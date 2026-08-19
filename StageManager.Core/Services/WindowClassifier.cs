using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Settings;
using System;
using System.Collections.Generic;

namespace StageManager.Services;

public interface IWindowClassifier
{
	bool IsCandidate(WindowsWindow window, out string reason);
}

public sealed class WindowClassifier : IWindowClassifier
{
	private static readonly HashSet<string> ExplorerFolderClasses = new(StringComparer.OrdinalIgnoreCase)
	{
		"CabinetWClass",
		"ExploreWClass"
	};

	private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
	{
		"TaskManagerWindow",
		"MSCTFIME UI",
		"SHELLDLL_DefView",
		"LockScreenBackstopFrame",
		"Progman",
		"Shell_TrayWnd",
		"Shell_SecondaryTrayWnd",
		"WorkerW",
		"NotifyIconOverflowWindow",
		"Windows.UI.Core.CoreWindow",
		"Xaml_WindowedPopupClass"
	};

	private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
	{
		"SearchUI",
		"ShellExperienceHost",
		"PeopleExperienceHost",
		"LockApp",
		"StartMenuExperienceHost",
		"SearchApp",
		"SearchHost",
		"search",
		"ScreenClippingHost",
		"TextInputHost",
		"SecurityHealthSystray",
		"explorer",
		"dwm"
	};

	private readonly SettingsService _settings;
	private readonly VirtualDesktopService _virtualDesktops;

	public WindowClassifier(SettingsService settings, VirtualDesktopService virtualDesktops)
	{
		_settings = settings;
		_virtualDesktops = virtualDesktops;
	}

	public bool IsCandidate(WindowsWindow window, out string reason)
	{
		if (window.Handle == IntPtr.Zero || !Win32.IsWindow(window.Handle))
			return Reject("invalid handle", out reason);
		if (window.ProcessId < 0 || string.IsNullOrWhiteSpace(window.ProcessName))
			return Reject("process unavailable", out reason);
		if (string.IsNullOrWhiteSpace(window.Title))
			return Reject("untitled/transient window", out reason);
		if (IgnoredClasses.Contains(window.Class))
			return Reject($"protected class {window.Class}", out reason);
		if (ProtectedProcesses.Contains(window.ProcessName) && !IsExplorerFolderWindow(window))
			return Reject($"protected process {window.ProcessName}", out reason);
		if (_settings.Current.IgnoredProcesses.Exists(name => string.Equals(name, window.ProcessName, StringComparison.OrdinalIgnoreCase)))
			return Reject($"user ignored process {window.ProcessName}", out reason);
		if (!Win32Helper.IsAppWindow(window.Handle) || !Win32Helper.IsAltTabWindow(window.Handle))
			return Reject("tool, owned, child, or non-activating window", out reason);
		if (Win32Helper.IsCloaked(window.Handle) && _virtualDesktops.IsWindowOnCurrentDesktop(window.Handle))
			return Reject("cloaked window on current desktop", out reason);

		reason = string.Empty;
		return true;
	}

	private static bool IsExplorerFolderWindow(WindowsWindow window)
	{
		return window.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
			ExplorerFolderClasses.Contains(window.Class);
	}

	private static bool Reject(string value, out string reason)
	{
		reason = value;
		return false;
	}
}
