using Microsoft.Win32.SafeHandles;
using StageManager.Native.PInvoke;
using System;
using System.Runtime.InteropServices;

namespace StageManager.Native;

public enum WindowIntegrityRelation { Unknown, Lower, Equal, Higher }

/// <summary>Integrity is advisory, not proof that a particular operation is permitted.</summary>
public readonly record struct WindowAccessInfo(uint? TargetIntegrity, uint? OurIntegrity)
{
    public WindowIntegrityRelation Relation => WindowAccess.Compare(TargetIntegrity, OurIntegrity);
}

public readonly record struct WindowAccessText(string Badge, string Description);

public readonly record struct WindowAccessBadges(string? MinimizedBadge, string PermissionBadge, string PermissionDescription);

/// <summary>Read-only token inspection. Never elevates, enables privileges, or changes message filters.</summary>
public static class WindowAccess
{
    /// <summary>Pass the renderer's existing minimized label verbatim; render these as separate badges.</summary>
    public static WindowAccessBadges GetBadges(WindowAccessInfo access, string? existingMinimizedBadge, bool chinese)
    {
        var text = Describe(access, chinese);
        return new(existingMinimizedBadge, text.Badge, text.Description);
    }

    public static WindowAccessInfo Query(IntPtr window)
    {
        var ours = ReadIntegrity((uint)Environment.ProcessId);
        if (window == IntPtr.Zero || !Win32.IsWindow(window) ||
            Win32.GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
            return new(null, ours);

        var target = ReadIntegrity(processId);
        // Do not publish a result for a window that vanished or changed owner during the query.
        if (!Win32.IsWindow(window) || Win32.GetWindowThreadProcessId(window, out var currentId) == 0 ||
            currentId != processId) target = null;
        return new(target, ours);
    }

    public static WindowIntegrityRelation Compare(uint? target, uint? ours) =>
        !target.HasValue || !ours.HasValue ? WindowIntegrityRelation.Unknown :
        target.Value > ours.Value ? WindowIntegrityRelation.Higher :
        target.Value < ours.Value ? WindowIntegrityRelation.Lower : WindowIntegrityRelation.Equal;

    // Query off the render/paint path and retain this immutable text with the card snapshot.
    public static WindowAccessText Describe(WindowAccessInfo access, bool chinese) => access.Relation switch
    {
        WindowIntegrityRelation.Higher => chinese
            ? new("ADMIN", "目标窗口完整性级别高于本程序，部分操作可能被 Windows 拒绝。可直接操作目标窗口；本程序不会自动提权。")
            : new("ADMIN", "This window has higher integrity than Stage Manager. Windows may reject some operations. Use the window directly; Stage Manager will not elevate automatically."),
        WindowIntegrityRelation.Unknown => chinese
            ? new("ACCESS ?", "无法确认目标窗口与本程序的完整性级别。未知不代表普通权限，也不代表拒绝访问。")
            : new("ACCESS ?", "The target/Stage Manager integrity comparison is unavailable. Unknown means neither normal access nor access denied."),
        _ => chinese
            ? new("", "目标窗口完整性级别不高于本程序；Windows 仍可能限制前台切换或其他操作。")
            : new("", "The target integrity is no higher than Stage Manager's; Windows may still restrict foreground activation or other operations.")
    };

    internal static uint? ReadIntegrity(uint processId)
    {
        using var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token))
            return null;
        using (token)
        {
            if (token.IsInvalid) return null;
            GetTokenInformation(token, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out var size);
            if (Marshal.GetLastWin32Error() != 122 || size < IntPtr.Size + sizeof(uint) || size > 65536)
                return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, size, out _)) return null;
                var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                if (sid == IntPtr.Zero || !IsValidSid(sid)) return null;
                var countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == IntPtr.Zero) return null;
                var count = Marshal.ReadByte(countPointer);
                if (count == 0) return null;
                var rid = GetSidSubAuthority(sid, (uint)(count - 1));
                return rid == IntPtr.Zero ? null : unchecked((uint)Marshal.ReadInt32(rid));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int length, out int requiredLength);
    [DllImport("advapi32.dll")]
    private static extern bool IsValidSid(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
