using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace StageManager;

public static class AutoStart
{
	public const string DefaultAppName = "StageManager";
	public const string StartupShortcutName = "Stage_Manager_Lai.lnk";
	private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

	public static void SetStartup(string appName, bool startup)
	{
		using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true)
			?? Registry.CurrentUser.CreateSubKey(RegistryKeyPath, writable: true);
		// Remove the old Codex/terminal-child launch mechanism in either state.
		key.DeleteValue(appName, throwOnMissingValue: false);

		var shortcutPath = GetStartupShortcutPath();
		if (!startup)
		{
			File.Delete(shortcutPath);
			return;
		}

		var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable.");
		CreateShortcut(shortcutPath, executable, Path.GetDirectoryName(executable) ?? string.Empty);
	}

	public static bool IsStartup(string appName)
	{
		if (File.Exists(GetStartupShortcutPath()))
			return true;
		using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
		return key is not null && IsStartup(key, appName);
	}

	public static bool IsStartup(RegistryKey key, string appName) =>
		GetValueAsString(key, appName).Equals(Quote(Environment.ProcessPath ?? string.Empty), StringComparison.OrdinalIgnoreCase);

	internal static string GetStartupShortcutPath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.Startup),
		StartupShortcutName);

	private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
	{
		var shellType = Type.GetTypeFromProgID("WScript.Shell")
			?? throw new InvalidOperationException("Windows Script Host is unavailable.");
		object? shell = null;
		object? shortcut = null;
		try
		{
			shell = Activator.CreateInstance(shellType);
			shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
			var shortcutType = shortcut?.GetType() ?? throw new InvalidOperationException("The startup shortcut could not be created.");
			shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
			shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
			shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Stage_Manager_Lai independent startup" });
			shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
		}
		finally
		{
			if (shortcut is not null && Marshal.IsComObject(shortcut))
				Marshal.FinalReleaseComObject(shortcut);
			if (shell is not null && Marshal.IsComObject(shell))
				Marshal.FinalReleaseComObject(shell);
		}
	}

	private static string GetValueAsString(RegistryKey key, string appName) => key.GetValue(appName)?.ToString() ?? string.Empty;
	private static string Quote(string value) => $"\"{value}\"";
}
