using StageManager.Native;
using StageManager.Native.Window;

namespace StageManager.Card3DPrototype;

internal readonly record struct GroupOpenResult(int Opened, int Failed, bool Interrupted);

internal static class GroupWindowOpener
{
    // One cancellation scope for the whole request. Do not use single-card toggle
    // dispatch here: it would minimize the foreground child and cancel its siblings.
    internal static async Task<GroupOpenResult> OpenAsync(IReadOnlyList<IWindow> windows,
        Func<IWindow, bool> stillValid,
        Func<IWindow, CancellationToken, Task<WindowOperationResult>> activate,
        CancellationToken token)
    {
        var opened = 0; var failed = 0;
        var targets = windows.Where(w => w.Handle != IntPtr.Zero).DistinctBy(w => w.Handle).Reverse().ToArray();
        try
        {
            foreach (var window in targets)
            {
                token.ThrowIfCancellationRequested();
                WindowOperationResult result;
                try
                {
                    if (!stillValid(window)) continue; // closed/recycled handles are never activated
                    result = await activate(window, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { failed++; continue; } // one app-specific failure must not skip its siblings
                if (result.Outcome is WindowOperationOutcome.Abandoned or WindowOperationOutcome.Cancelled)
                    return new(opened, failed, true);
                if (result.Succeeded) opened++;
                else if (result.Outcome != WindowOperationOutcome.TargetGone) failed++;
            }
            return new(opened, failed, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(opened, failed, true); }
    }
}
