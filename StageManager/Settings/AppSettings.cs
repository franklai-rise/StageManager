using System.Collections.Generic;

namespace StageManager.Settings;

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

public sealed class AppSettings
{
	public int SchemaVersion { get; set; } = 1;
	public StageMode StageMode { get; set; } = StageMode.Coexist;
	public AppWindowsMode AppWindowsMode { get; set; } = AppWindowsMode.AllAtOnce;
	public bool AutoHideSidebar { get; set; }
	public bool UsePerspectiveCards { get; set; } = true;
	public bool AnimationsEnabled { get; set; } = true;
	public bool HotkeysEnabled { get; set; } = true;
	public bool StartWithWindows { get; set; } = true;
	public bool IdleAutoHideEnabled { get; set; } = true;
	public int IdleAutoHideSeconds { get; set; } = 60;
	public double CardScale { get; set; } = 0.60;
	public double SidebarOpacity { get; set; } = 0.94;
	public string ToggleSidebarHotkey { get; set; } = "Win+Alt+S";
	public string PreviousStageHotkey { get; set; } = "Win+Alt+[";
	public string NextStageHotkey { get; set; } = "Win+Alt+]";
	public string ToggleWindowInStageHotkey { get; set; } = "Win+Alt+G";
	public List<string> IgnoredProcesses { get; set; } = new()
	{
		"explorer",
		"yuanbao"
	};
}
