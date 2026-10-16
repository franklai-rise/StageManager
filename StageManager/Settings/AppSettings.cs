using System.Collections.Generic;

namespace StageManager.Settings;

public enum QuickLaunchTargetKind
{
	Executable,
	Shortcut,
	AppsFolder,
	LegacyBrowser
}

/// <summary>List position is display order; Id is identity, never the translated name.</summary>
public sealed record QuickLaunchDefinition
{
	public string Id { get; set; } = "";
	public string DisplayName { get; set; } = "";
	public QuickLaunchTargetKind Kind { get; set; }
	public string Target { get; set; } = "";
	public bool Enabled { get; set; } = true;

	public static List<QuickLaunchDefinition> LegacyDefaults(bool chrome = true, bool edge = true) => new()
	{
		new() { Id = "browser:chrome", DisplayName = "Google Chrome", Kind = QuickLaunchTargetKind.LegacyBrowser, Target = "chrome.exe", Enabled = chrome },
		new() { Id = "browser:edge", DisplayName = "Microsoft Edge", Kind = QuickLaunchTargetKind.LegacyBrowser, Target = "msedge.exe", Enabled = edge }
	};
}

public enum StageMode
{
	Coexist,
	Focus
}

public enum AppWindowsMode
{
	AllAtOnce,
	OneAtATime
}

public enum UiLanguage
{
	English,
	SimplifiedChinese
}

public sealed class AppSettings
{
	public int SchemaVersion { get; set; } = 1;
	public StageMode StageMode { get; set; } = StageMode.Coexist;
	public AppWindowsMode AppWindowsMode { get; set; } = AppWindowsMode.AllAtOnce;
	public UiLanguage UiLanguage { get; set; } = UiLanguage.English;
	public bool AutoHideSidebar { get; set; }
	public bool UsePerspectiveCards { get; set; } = true;
	public bool AnimationsEnabled { get; set; } = true;
	public bool HotkeysEnabled { get; set; } = true;
	public bool StartWithWindows { get; set; } = true;
	public bool IdleAutoHideEnabled { get; set; } = true;
	public int IdleAutoHideSeconds { get; set; } = 60;
	public int PreviewRefreshMinutes { get; set; } = 5;
	public bool PausePreviewRefreshWhenHidden { get; set; } = true;
	public bool LowMemoryRendering { get; set; } = true;
	public double CardScale { get; set; } = 0.60;
	public int SidebarVerticalOffset { get; set; } = -80;
	public bool ShowExplorerButton { get; set; } = true;
	public bool ShowChromeQuickLaunch { get; set; } = true;
	public bool ShowEdgeQuickLaunch { get; set; } = true;
	// Null means an unmigrated file; an empty list deliberately means no launchers.
	public List<QuickLaunchDefinition>? QuickLaunchApps { get; set; }
	public bool ShowAiUsageCard { get; set; }
	public bool AiUsageEnabled { get; set; }
	public bool AiUsageCodexEnabled { get; set; } = true;
	public bool AiUsageDeepSeekEnabled { get; set; } = true;
	public bool AiUsageGeminiEnabled { get; set; } = true;
	public bool AiUsageKimiEnabled { get; set; }
	public bool AiUsageKimiInternational { get; set; }
	public string CodexCliPath { get; set; } = "";
	public bool ShowExpandedPinButton { get; set; } = true;
	public bool ShowNotificationAreaCard { get; set; } = true;
	public bool ShowDesktopButton { get; set; } = true;
	public bool ShowDesktopIconsButton { get; set; } = true;
	public int NotificationAreaVerticalOffset { get; set; }
	public double SidebarOpacity { get; set; } = 0.94;
	public string ToggleSidebarHotkey { get; set; } = "Win+Alt+S";
	public string PreviousStageHotkey { get; set; } = "Win+Alt+[";
	public string NextStageHotkey { get; set; } = "Win+Alt+]";
	public string ToggleWindowInStageHotkey { get; set; } = "Win+Alt+G";
	public List<string> IgnoredProcesses { get; set; } = new();
}
