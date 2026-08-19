using StageManager.Services;
using StageManager.Settings;

namespace StageManager.Infrastructure;

internal static class AppServices
{
	public static SettingsService Settings { get; private set; } = null!;
	public static StartupGuard StartupGuard { get; private set; } = null!;
	public static VirtualDesktopService VirtualDesktops { get; private set; } = null!;
	public static DisplayTopologyService Displays { get; private set; } = null!;
	public static WindowClassifier WindowClassifier { get; private set; } = null!;
	public static bool SafeMode { get; private set; }

	public static void Initialize()
	{
		AppLogger.Initialize();
		Settings = new SettingsService();
		StartupGuard = new StartupGuard();
		SafeMode = StartupGuard.BeginRun();
		VirtualDesktops = new VirtualDesktopService();
		Displays = new DisplayTopologyService();
		WindowClassifier = new WindowClassifier(Settings, VirtualDesktops);
		AppLogger.Info($"Stage_Manager_Lai started. SafeMode={SafeMode}.");
	}

	public static void DisableSafeMode()
	{
		StartupGuard.Reset();
		SafeMode = false;
	}
}
