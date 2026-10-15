namespace StageManager.Card3DPrototype.Updates;

internal static class AppVersion
{
    public static Version Current => typeof(AppVersion).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    public static string Label => $"Stage_Manager_Lai v{Current}";
    public const string Repository = "franklai-rise/StageManager";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
}
