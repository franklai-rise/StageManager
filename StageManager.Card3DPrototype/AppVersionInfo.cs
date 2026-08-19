using System.Reflection;

namespace StageManager.Card3DPrototype;

internal static class AppVersionInfo
{
	private static readonly Assembly Assembly = typeof(AppVersionInfo).Assembly;

	public static string InformationalVersion =>
		Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? Assembly.GetName().Version?.ToString(3)
		?? "development";

	public static string DisplayName => InformationalVersion.StartsWith("Stage_Manager_Lai", StringComparison.OrdinalIgnoreCase)
		? InformationalVersion.Replace('_', ' ')
		: $"Stage Manager Lai {InformationalVersion}";
}
