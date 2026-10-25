using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Card3DPrototype;

internal sealed partial class PrototypeForm
{
    private CardHitTarget? _openGroupPress;

    private async Task OpenApplicationGroupAsync(string key)
    {
        var windows = _renderer?.GetStageSnapshot(key)?.Windows.ToArray();
        if (windows is null || windows.Length == 0 || _closing) return;
        CancelActivationVerification();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_notificationAreaCancellation.Token);
        source.CancelAfter(TimeSpan.FromSeconds(20));
        _activationVerification = source;
        _renderer?.SetOpenGroupBusy(key, true);
        _desktopToggle.MarkDesktopDismissed(); _renderer?.SetDesktopShown(false);
        try
        {
            var result = await GroupWindowOpener.OpenAsync(windows, window =>
            {
                Win32.GetWindowThreadProcessId(window.Handle, out var pid);
                return pid == window.ProcessId && pid != 0 && NativeMethods.IsWindow(window.Handle);
            }, async (window, token) =>
            {
                var trayFirst = window.ProcessName.Equals("VpnManager", StringComparison.OrdinalIgnoreCase) ||
                    window.ProcessName.Equals("Clash for Windows", StringComparison.OrdinalIgnoreCase);
                var opened = await WindowActivation.ActivateAsync(window.Handle, token,
                    t => RestoreFromTrayAsync(window, t), trayFirst);
                if (opened.Succeeded && !_closing) QueueFocusWindowPlacement(window);
                if (!opened.Succeeded && opened.Outcome is WindowOperationOutcome.NotConfirmed or WindowOperationOutcome.RequestRejected or WindowOperationOutcome.TimedOut)
                    Win32Helper.FlashTaskbar(window.Handle);
                return opened;
            }, source.Token);
            if (!_closing && !source.IsCancellationRequested && result.Failed > 0)
                _trayIcon?.ShowBalloonTip(3500, "Stage_Manager_Lai",
                    L($"Windows could not bring forward {result.Failed} window(s). Use the application's taskbar button.",
                        $"有 {result.Failed} 个窗口未能置于前台，请使用该应用的任务栏按钮。"), ToolTipIcon.Info);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { TraceCardClick($"GROUP OPEN failed={error.GetType().Name}"); }
        finally
        {
            if (ReferenceEquals(_activationVerification, source)) _activationVerification = null;
            source.Dispose();
            if (!_closing) { _renderer?.SetOpenGroupBusy(key, false); RefreshStages(); }
        }
    }
}
