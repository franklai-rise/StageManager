using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using StageManager.Settings;

internal static class SidebarDisplaySelectionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var left = new SidebarDisplayInfo(@"\\.\DISPLAY2", "monitor:left", "Portrait", new(-1200, -100, 1200, 1920), false);
        var main = new SidebarDisplayInfo(@"\\.\DISPLAY1", "monitor:main", "Main", new(0, 0, 1920, 1080), true);
        var right = new SidebarDisplayInfo(@"\\.\DISPLAY3", "monitor:right", "External", new(1920, 0, 2560, 1440), false);
        var displays = new[] { right, main, left };
        foreach (var edge in Enum.GetValues<SidebarEdge>())
        {
            Check(SidebarDisplayPolicy.Select(displays, SidebarDisplayMode.Leftmost, "", "") == left, $"Leftmost default changed at {edge}.");
            Check(SidebarDisplayPolicy.Select(displays, SidebarDisplayMode.Primary, "", "") == main, "Primary selection ignored.");
            Check(SidebarDisplayPolicy.Select(displays, SidebarDisplayMode.Specific, right.MonitorId.ToUpperInvariant(), "old-number") == right, "Monitor identity did not survive renumbering.");
            Check(SidebarDisplayPolicy.Select(new[] { left, main }, SidebarDisplayMode.Specific, right.MonitorId, left.DeviceName) == main, "Disconnected target reused another screen's display number.");
            Check(SidebarDisplayPolicy.Select(displays, SidebarDisplayMode.Specific, "", right.DeviceName) == right, "GDI fallback selection failed.");
            var selected = SidebarDisplayPolicy.Select(displays, SidebarDisplayMode.Specific, right.MonitorId, right.DeviceName);
            Check(SidebarDockLayout.IsNearEdge(new Point(selected.Bounds.Left, selected.Bounds.Top), selected.Bounds, edge, 30) == (edge is SidebarEdge.Left or SidebarEdge.Top), "Chosen monitor edge geometry failed.");
        }
        Check(SidebarDisplayPolicy.Select(new[] { main }, SidebarDisplayMode.Specific, left.MonitorId, left.DeviceName) == main, "Single-monitor fallback failed.");
        Check(SidebarDisplayPolicy.Select(displays, SidebarDisplayMode.Specific, right.MonitorId, right.DeviceName) == right, "Reconnected target not restored.");
        var native = SidebarDisplayCatalog.Enumerate();
        Check(native.Length == Screen.AllScreens.Length && native.All(d => d.DeviceName.Length > 0 && d.Name.Length > 0), "Connected display enumeration invalid.");
        var directory = Path.Combine(Path.GetTempPath(), "StageManager-DisplaySettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var original = """{"SchemaVersion":22,"ShowCalendarCard":true,"AiUsageEnabled":true,"ShowAiUsageCard":true,"CardScale":0.57}""";
            File.WriteAllText(path, original);
            var service = new SettingsService(path);
            Check(service.Current.SchemaVersion == 23 && service.Current.SidebarDisplayMode == SidebarDisplayMode.Leftmost, "Legacy monitor default changed.");
            Check(File.ReadAllText(path + ".pre-schema23.bak") == original, "Display migration backup changed bytes.");
            Check(service.Current.ShowCalendarCard && service.Current.AiUsageEnabled && service.Current.CardScale == .57, "Display migration changed existing features.");
            var draft = service.CloneCurrent();
            draft.SidebarDisplayMode = SidebarDisplayMode.Specific; draft.SidebarDisplayId = right.MonitorId;
            draft.SidebarDisplayDeviceName = right.DeviceName;
            service.Apply(draft);
            var reloaded = new SettingsService(path).Current;
            Check(reloaded.SidebarDisplayMode == SidebarDisplayMode.Specific && reloaded.SidebarDisplayId == right.MonitorId && reloaded.SidebarDisplayDeviceName == right.DeviceName, "Monitor selection did not round trip.");
            SidebarDisplayPolicy.Select(new[] { main }, reloaded.SidebarDisplayMode, reloaded.SidebarDisplayId, reloaded.SidebarDisplayDeviceName);
            Check(reloaded.SidebarDisplayId == right.MonitorId, "Fallback overwrote the user's saved target.");
            draft.SidebarDisplayMode = (SidebarDisplayMode)999; draft.SidebarDisplayId = null!; draft.SidebarDisplayDeviceName = null!;
            service.Apply(draft);
            Check(service.Current.SidebarDisplayMode == SidebarDisplayMode.Leftmost && service.Current.SidebarDisplayId == "" && service.Current.SidebarDisplayDeviceName == "", "Malformed monitor settings not normalized.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
