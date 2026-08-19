using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Model;
using StageManager.Settings;
using System;
using System.Collections.Generic;

namespace StageManager.Services;

public interface IWindowClassifier
{
	bool IsCandidate(WindowsWindow window, out string reason);
	WindowClassification Classify(WindowsWindow window);
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
		var classification = Classify(window);
		reason = classification.RejectionReason;
		return classification.CreatesCard;
	}

	public WindowClassification Classify(WindowsWindow window)
	{
		ArgumentNullException.ThrowIfNull(window);
		var applicationId = GetCanonicalApplicationId(window);
		var owner = window.Handle == IntPtr.Zero ? IntPtr.Zero : Win32.GetWindow(window.Handle, Win32.GW.GW_OWNER);
		var activationTarget = owner == IntPtr.Zero ? window.Handle : owner;
		if (window.Handle == IntPtr.Zero || !Win32.IsWindow(window.Handle))
			return Reject(applicationId, WindowRole.Unknown, activationTarget, "invalid handle");
		if (window.ProcessId < 0 || string.IsNullOrWhiteSpace(window.ProcessName))
			return Reject(applicationId, WindowRole.Unknown, activationTarget, "process unavailable");
		if (string.IsNullOrWhiteSpace(window.Title))
			return Reject(applicationId, WindowRole.TransientPopup, activationTarget, "untitled/transient window");
		if (IgnoredClasses.Contains(window.Class))
			return Reject(applicationId, WindowRole.Shell, activationTarget, $"protected class {window.Class}");
		if (ProtectedProcesses.Contains(window.ProcessName) && !IsExplorerFolderWindow(window))
			return Reject(applicationId, WindowRole.Shell, activationTarget, $"protected process {window.ProcessName}");
		if (_settings.Current.IgnoredProcesses.Exists(name => string.Equals(name, window.ProcessName, StringComparison.OrdinalIgnoreCase)))
			return Reject(applicationId, WindowRole.Primary, activationTarget, $"user ignored process {window.ProcessName}");
		var extendedStyle = Win32.GetWindowExStyleLongPtr(window.Handle);
		if (extendedStyle.HasFlag(Win32.WS_EX.WS_EX_NOACTIVATE))
			return Reject(applicationId, WindowRole.Overlay, activationTarget, "non-activating overlay");
		if (owner != IntPtr.Zero)
			return Reject(applicationId, WindowRole.ModalDialog, activationTarget, "owned dialog uses its application card");
		if (!Win32Helper.IsAppWindow(window.Handle) || !Win32Helper.IsAltTabWindow(window.Handle))
			return Reject(applicationId, WindowRole.TransientPopup, activationTarget, "tool, child, or transient window");
		if (Win32Helper.IsCloaked(window.Handle) && _virtualDesktops.IsWindowOnCurrentDesktop(window.Handle))
			return Reject(applicationId, WindowRole.Unknown, activationTarget, "cloaked window on current desktop");

		return new WindowClassification(true, applicationId, WindowRole.Primary, window.Handle, string.Empty);
	}

	public static string GetCanonicalApplicationId(WindowsWindow window)
	{
		if (!string.IsNullOrWhiteSpace(window.AppUserModelId))
			return "aumid:" + window.AppUserModelId.Trim().ToUpperInvariant();
		if (!string.IsNullOrWhiteSpace(window.ProcessExecutable))
			return "exe:" + Path.GetFullPath(window.ProcessExecutable).Trim().ToUpperInvariant();
		return "process:" + window.ProcessName.Trim().ToUpperInvariant();
	}

	private static bool IsExplorerFolderWindow(WindowsWindow window)
	{
		return window.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
			ExplorerFolderClasses.Contains(window.Class);
	}

	private static WindowClassification Reject(
		string applicationId,
		WindowRole role,
		IntPtr activationTarget,
		string reason) => WindowClassification.Rejected(applicationId, role, activationTarget, reason);
}
