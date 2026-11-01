using System.Runtime.InteropServices;
using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal static class SidebarDisplayCatalog
{
    public static SidebarDisplayInfo[] Enumerate(Screen[]? screens = null) => (screens ?? Screen.AllScreens)
        .Select(screen =>
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            // EDD_GET_DEVICE_INTERFACE_NAME links each GDI screen to its monitor.
            var found = EnumDisplayDevices(screen.DeviceName, 0, ref device, 1);
            return new SidebarDisplayInfo(screen.DeviceName, found ? device.Id ?? "" : "",
                found && !string.IsNullOrWhiteSpace(device.Description) ? device.Description : screen.DeviceName,
                screen.Bounds, screen.Primary);
        }).OrderBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top).ToArray();

    public static Screen Select(Screen[] screens, AppSettings? settings)
    {
        if (settings is null || settings.SidebarDisplayMode == SidebarDisplayMode.Leftmost)
            return SidebarDisplayPolicy.SelectLeftmost(screens, screen => screen.Bounds);
        if (settings.SidebarDisplayMode == SidebarDisplayMode.Primary)
            return screens.FirstOrDefault(s => s.Primary) ?? SidebarDisplayPolicy.SelectLeftmost(screens, s => s.Bounds);
        var selected = SidebarDisplayPolicy.Select(Enumerate(screens), settings.SidebarDisplayMode,
            settings.SidebarDisplayId, settings.SidebarDisplayDeviceName);
        return screens.First(s => string.Equals(s.DeviceName, selected.DeviceName, StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
}
