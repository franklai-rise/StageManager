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

public enum SidebarDisplayMode
{
	Leftmost,
	Primary,
	Specific
}

public enum FullScreenSidebarMode
{
	EdgeReveal,
	Disabled
}

public enum RenderProfile
{
	LowMemory,
	Balanced,
	Performance
}

public enum PreviewMode
{
	Auto,
	Snapshot,
	IconOnly
}

public sealed class ApplicationRule
{
	public string ApplicationId { get; set; } = string.Empty;
	public bool Ignore { get; set; }
	public PreviewMode PreviewMode { get; set; } = PreviewMode.Auto;
}

public sealed class AppSettings
{
	public int SchemaVersion { get; set; } = 9;
	public StageMode StageMode { get; set; } = StageMode.Coexist;
	public AppWindowsMode AppWindowsMode { get; set; } = AppWindowsMode.AllAtOnce;
	public SidebarDisplayMode SidebarDisplayMode { get; set; } = SidebarDisplayMode.Leftmost;
	public string? SidebarDisplayId { get; set; }
	public FullScreenSidebarMode FullScreenSidebarMode { get; set; } = FullScreenSidebarMode.EdgeReveal;
	public RenderProfile RenderProfile { get; set; } = RenderProfile.LowMemory;
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
	public double SidebarOpacity { get; set; } = 0.94;
	public string ToggleSidebarHotkey { get; set; } = "Win+Alt+S";
	public string PreviousStageHotkey { get; set; } = "Win+Alt+[";
	public string NextStageHotkey { get; set; } = "Win+Alt+]";
	public string ToggleWindowInStageHotkey { get; set; } = "Win+Alt+G";
	public List<string> IgnoredProcesses { get; set; } = new();
	public List<ApplicationRule> ApplicationRules { get; set; } = new();

	public ApplicationRule? FindApplicationRule(string? applicationId)
	{
		if (string.IsNullOrWhiteSpace(applicationId))
			return null;
		return ApplicationRules.FirstOrDefault(rule =>
			string.Equals(rule.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase));
	}
}
