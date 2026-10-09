using StageManager.Native.Window;

namespace StageManager.Card3DPrototype;

// Snapshot on the caller's thread: a late worker must never read mutable UI models
// or fetch the title of an unresponsive foreign HWND to draw a placeholder.
internal sealed record CaptureWindow(IntPtr Handle, string Title, string ProcessName,
    string ProcessExecutable, bool IsMinimized, uint OwnerProcessId = 0, long? ProcessStartTime = null)
{
    public static CaptureWindow FromWindow(IWindow window)
    {
        var handle = window.Handle;
        var identity = CaptureWindowIdentity.Read(handle);
        return new(handle, Limit(window.Title, 4096), Limit(window.ProcessName, 512),
            Limit(window.ProcessExecutable, 4096), window.IsMinimized, identity.ProcessId, identity.StartTime);
    }

    internal static string Limit(string? text, int length) =>
        string.IsNullOrEmpty(text) ? string.Empty : text[..Math.Min(text.Length, length)];
}
