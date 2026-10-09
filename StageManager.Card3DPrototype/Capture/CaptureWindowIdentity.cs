using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

internal static class CaptureWindowIdentity
{
    // StartTime is the raw UTC creation FILETIME, not wall-clock/local-time text.
    // The HWND owner may differ from IWindow.ProcessId for hosted applications.
    internal static (uint ProcessId, long? StartTime) Read(IntPtr handle)
    {
        if (handle == IntPtr.Zero || GetWindowThreadProcessId(handle, out var owner) == 0 || owner == 0)
            return default;
        using var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, owner);
        long? start = !process.IsInvalid && GetProcessTimes(process, out var created, out _, out _, out _)
            ? created : null;
        if (GetWindowThreadProcessId(handle, out var current) == 0 || current != owner) return default;
        return (owner, start);
    }

    internal static bool Matches(CaptureWindow expected) => Matches(expected, Read(expected.Handle));

    internal static bool Matches(CaptureWindow expected, (uint ProcessId, long? StartTime) actual) =>
        expected.OwnerProcessId != 0 && expected.OwnerProcessId == actual.ProcessId &&
        // Fail closed if creation time was inaccessible. A PID alone is recycled
        // too, so it is not sufficient authorization for native window capture.
        expected.ProcessStartTime.HasValue && expected.ProcessStartTime == actual.StartTime;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
}
