using Microsoft.Win32.SafeHandles;
using StageManager.Native.PInvoke;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StageManager.Native;

public enum WindowOperationOutcome
{
    Confirmed, Hidden, NotConfirmed, RequestRejected, TargetGone, Abandoned, Cancelled, TimedOut
}

public readonly record struct WindowOperationResult(WindowOperationOutcome Outcome, int NativeError = 0)
{
    public bool Succeeded => Outcome is WindowOperationOutcome.Confirmed or WindowOperationOutcome.Hidden;
}

/// <summary>
/// One user-requested operation. At most two native foreground requests, one restore, and one
/// optional cooperative tray request. Call on the UI thread; callbacks retain its context.
/// Cancellation is cooperative: already queued Windows/app requests cannot be recalled.
/// </summary>
public static class WindowActivation
{
    public static Task<WindowOperationResult> ActivateAsync(IntPtr window,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? trayRestore = null, bool preferTrayRestore = false) =>
        ActivateAsync(new NativeWindowOperation(window), cancellationToken, trayRestore, preferTrayRestore);

    public static Task<WindowOperationResult> MinimizeAsync(IntPtr window,
        CancellationToken cancellationToken = default) =>
        MinimizeAsync(new NativeWindowOperation(window), cancellationToken);

    internal static async Task<WindowOperationResult> ActivateAsync(IWindowOperation native,
        CancellationToken token, Func<CancellationToken, Task<bool>>? trayRestore = null,
        bool preferTrayRestore = false)
    {
        var baseline = native.Read();
        try
        {
            token.ThrowIfCancellationRequested();
            var terminal = CheckActivation(baseline, baseline);
            if (terminal.HasValue) return new(terminal.Value);
            var cooperativeTray = preferTrayRestore && trayRestore is not null;
            if (preferTrayRestore && trayRestore is not null)
            {
                var trayResult = await InvokeTrayAsync(native, baseline, trayRestore, token);
                if (trayResult.Outcome != WindowOperationOutcome.NotConfirmed) return trayResult;
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                terminal = CheckActivation(baseline, native.Read(), requireInputKnowledge: attempt > 0 || preferTrayRestore,
                    allowCooperativeTray: cooperativeTray);
                if (terminal.HasValue) return new(terminal.Value);
                // A tray-first application owns its restore semantics. Never follow its signal
                // with SW_RESTORE, which can race with the application's maximized restore.
                native.RequestActivation(restoreIfMinimized: attempt == 0 && !preferTrayRestore);
                var result = await ObserveActivationAsync(native, baseline, token, cooperativeTray);
                if (result.Outcome != WindowOperationOutcome.NotConfirmed) return result;
            }

            if (!preferTrayRestore && trayRestore is not null)
                return await InvokeTrayAsync(native, baseline, trayRestore, token);
            return new(WindowOperationOutcome.NotConfirmed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(WindowOperationOutcome.Cancelled); }
    }

    internal static async Task<WindowOperationResult> MinimizeAsync(IWindowOperation native, CancellationToken token)
    {
        var baseline = native.Read();
        try
        {
            token.ThrowIfCancellationRequested();
            if (!baseline.Exists || baseline.ProcessId == 0) return new(WindowOperationOutcome.TargetGone);
            if (baseline.Iconic) return new(WindowOperationOutcome.Confirmed);
            if (!baseline.Visible) return new(WindowOperationOutcome.NotConfirmed);
            // WM_SYSCOMMAND preserves app-specific minimize-to-tray behavior. Queueing is not success.
            var current = native.Read();
            if (!SameTarget(baseline, current)) return new(WindowOperationOutcome.TargetGone);
            if (UserMovedOn(baseline, current)) return new(WindowOperationOutcome.Abandoned);
            var request = native.RequestMinimize();
            if (!request.Accepted) return new(WindowOperationOutcome.RequestRejected, request.Error);
            for (var poll = 0; poll < 10; poll++)
            {
                await native.Delay(40, token);
                token.ThrowIfCancellationRequested();
                current = native.Read();
                if (!SameTarget(baseline, current)) return new(WindowOperationOutcome.TargetGone);
                if (current.Iconic) return new(WindowOperationOutcome.Confirmed);
                if (!current.Visible) return new(WindowOperationOutcome.Hidden);
                // Minimizing naturally changes foreground; only new input abandons this observer.
                if (InputChanged(baseline, current)) return new(WindowOperationOutcome.Abandoned);
            }
            return new(WindowOperationOutcome.NotConfirmed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(WindowOperationOutcome.Cancelled); }
    }

    private static async Task<WindowOperationResult> ObserveActivationAsync(IWindowOperation native,
        WindowOperationSnapshot baseline, CancellationToken token, bool allowCooperativeTray = false)
    {
        for (var poll = 0; poll < 5; poll++)
        {
            await native.Delay(40, token);
            token.ThrowIfCancellationRequested();
            var terminal = CheckActivation(baseline, native.Read(), requireInputKnowledge: allowCooperativeTray,
                allowCooperativeTray: allowCooperativeTray);
            if (terminal.HasValue) return new(terminal.Value);
        }
        return new(WindowOperationOutcome.NotConfirmed);
    }

    private static async Task<WindowOperationResult> InvokeTrayAsync(IWindowOperation native,
        WindowOperationSnapshot baseline, Func<CancellationToken, Task<bool>> restore, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var terminal = CheckActivation(baseline, native.Read(), requireInputKnowledge: true);
        if (terminal.HasValue) return new(terminal.Value);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Preserve the client's eight-second cold UIA/auto-hidden taskbar allowance.
        // This outer deadline includes the final foreground observation, not just worker execution.
        pending.CancelAfter(TimeSpan.FromSeconds(9));
        var task = restore(pending.Token);
        try
        {
            // Monitor even while the notification-area worker is running. Bound a non-cooperative
            // callback too; cancellation cannot undo an activation it has already dispatched.
            for (var poll = 0; !task.IsCompleted && poll < 225; poll++)
            {
                await native.Delay(40, pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                terminal = CheckActivation(baseline, native.Read(), requireInputKnowledge: true, allowCooperativeTray: true);
                if (terminal.HasValue) return new(terminal.Value);
            }
            if (!task.IsCompleted) return new(WindowOperationOutcome.TimedOut);
            _ = await task; // "invoked" is not foreground confirmation, regardless of its value.
            return await ObserveActivationAsync(native, baseline, pending.Token, allowCooperativeTray: true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && pending.IsCancellationRequested)
        { return new(WindowOperationOutcome.TimedOut); }
        finally
        {
            pending.Cancel();
            // Observe a late worker failure even if it ignored cancellation and outlived us.
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static WindowOperationOutcome? CheckActivation(WindowOperationSnapshot baseline,
        WindowOperationSnapshot current, bool requireInputKnowledge = false, bool allowCooperativeTray = false)
    {
        if (!SameTarget(baseline, current)) return WindowOperationOutcome.TargetGone;
        // Even a verified shell panel never excuses real input received after the user's request.
        if (InputChanged(baseline, current)) return WindowOperationOutcome.Abandoned;
        // Restored/visible alone is never confirmation. Include an owned modal foreground window.
        if (current.ForegroundForTarget && current.Visible && !current.Iconic) return WindowOperationOutcome.Confirmed;
        if (UserMovedOn(baseline, current, allowCooperativeTray)) return WindowOperationOutcome.Abandoned;
        if (requireInputKnowledge && (!baseline.LastInput.HasValue || !current.LastInput.HasValue))
            return WindowOperationOutcome.Abandoned;
        return null;
    }

    private static bool SameTarget(WindowOperationSnapshot baseline, WindowOperationSnapshot current) =>
        baseline.Exists && current.Exists && baseline.ProcessId != 0 &&
        current.ProcessId == baseline.ProcessId && current.ThreadId == baseline.ThreadId;

    private static bool InputChanged(WindowOperationSnapshot baseline, WindowOperationSnapshot current) =>
        baseline.LastInput.HasValue && current.LastInput.HasValue && baseline.LastInput != current.LastInput;

    private static bool UserMovedOn(WindowOperationSnapshot baseline, WindowOperationSnapshot current,
        bool allowCooperativeTray = false) =>
        InputChanged(baseline, current) ||
        (current.Foreground != IntPtr.Zero && current.Foreground != baseline.Foreground && !current.ForegroundForTarget &&
         !(allowCooperativeTray && current.CooperativeTrayForeground));
}

internal readonly record struct WindowOperationSnapshot(bool Exists, uint ProcessId, uint ThreadId,
    bool Visible, bool Iconic, IntPtr Foreground, bool ForegroundForTarget, uint? LastInput,
    bool CooperativeTrayForeground = false);

internal interface IWindowOperation
{
    WindowOperationSnapshot Read();
    void RequestActivation(bool restoreIfMinimized);
    (bool Accepted, int Error) RequestMinimize();
    Task Delay(int milliseconds, CancellationToken token);
}

internal sealed class NativeWindowOperation(IntPtr window) : IWindowOperation
{
    public WindowOperationSnapshot Read()
    {
        var thread = Win32.GetWindowThreadProcessId(window, out var process);
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        uint? lastInput = GetLastInputInfo(ref input) ? input.Time : null;
        var foreground = Win32.GetForegroundWindow();
        return new(Win32.IsWindow(window), process, thread, Win32.IsWindowVisible(window),
            Win32.IsIconic(window), foreground, Win32Helper.IsForegroundForWindow(window), lastInput,
            IsCooperativeTrayForeground(foreground));
    }

    // Class names alone are spoofable. Require a visible top-level window, an allowed owner
    // topology, the OS shell's PID, and that process's exact Windows explorer.exe image path.
    // Ordinary Explorer folder windows (CabinetWClass), desktops and unrelated popups never qualify.
    private static bool IsCooperativeTrayForeground(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || !Win32.IsWindowVisible(foreground) ||
            Win32.GetAncestor(foreground, Win32.GA.GA_ROOT) != foreground) return false;
        var name = new StringBuilder(256);
        if (Win32.GetClassName(foreground, name, name.Capacity) == 0) return false;
        var className = name.ToString();
        if (!IsKnownTrayClass(className)) return false;

        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || Win32.GetWindowThreadProcessId(shell, out var shellPid) == 0 || shellPid == 0 ||
            Win32.GetWindowThreadProcessId(foreground, out var foregroundPid) == 0 || foregroundPid != shellPid)
            return false;

        var primaryTray = FindWindow("Shell_TrayWnd", null);
        if (primaryTray == IntPtr.Zero || Win32.GetWindowThreadProcessId(primaryTray, out var trayPid) == 0 || trayPid != shellPid)
            return false;
        if (className == "Shell_TrayWnd" && foreground != primaryTray) return false;
        var owner = Win32.GetWindow(foreground, Win32.GW.GW_OWNER);
        if (owner != IntPtr.Zero && owner != shell && owner != primaryTray)
        {
            name.Clear();
            if (Win32.GetWindowThreadProcessId(owner, out var ownerPid) == 0 || ownerPid != shellPid ||
                Win32.GetAncestor(owner, Win32.GA.GA_ROOT) != owner ||
                Win32.GetClassName(owner, name, name.Capacity) == 0 || name.ToString() != "Shell_SecondaryTrayWnd")
                return false;
        }

        using var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, shellPid);
        if (process.IsInvalid) return false;
        var image = new StringBuilder(32768);
        var length = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref length)) return false;
        return string.Equals(image.ToString(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            StringComparison.OrdinalIgnoreCase) &&
            Win32.GetWindowThreadProcessId(foreground, out var currentPid) != 0 && currentPid == shellPid &&
            GetShellWindow() == shell;
    }

    internal static bool IsKnownTrayClass(string className) => className is
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";

    public void RequestActivation(bool restoreIfMinimized) =>
        FocusStealer.Steal(Win32Helper.ResolveActivationTarget(window), restoreIfMinimized);

    public (bool Accepted, int Error) RequestMinimize()
    {
        var accepted = PostMessage(window, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF020 /* SC_MINIMIZE */, IntPtr.Zero);
        return (accepted, accepted ? 0 : Marshal.GetLastWin32Error());
    }

    public Task Delay(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
