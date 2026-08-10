using StageManager.Native.PInvoke;
using System;

namespace StageManager.Native
{
    public static class FocusStealer
    {
        public static bool Steal(IntPtr windowToFocus)
        {
            if (windowToFocus == IntPtr.Zero || !Win32.IsWindow(windowToFocus))
                return false;
            if (Win32.GetForegroundWindow() == windowToFocus)
                return true;

            var currentThread = Win32.GetCurrentThreadId();
            var foreground = Win32.GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(foreground, out _);
            var targetThread = Win32.GetWindowThreadProcessId(windowToFocus, out _);
            var attachedToForeground = false;
            var attachedToTarget = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != currentThread)
                    attachedToForeground = Win32.AttachThreadInput(currentThread, foregroundThread, true);
                if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                    attachedToTarget = Win32.AttachThreadInput(currentThread, targetThread, true);

                if (Win32.IsIconic(windowToFocus))
                    Win32.ShowWindowAsync(windowToFocus, Win32.SW.SW_RESTORE);
                Win32.BringWindowToTop(windowToFocus);
                Win32.SetForegroundWindow(windowToFocus);
                Win32.SetActiveWindow(windowToFocus);
                Win32.SetFocus(windowToFocus);
            }
            finally
            {
                if (attachedToTarget)
                    Win32.AttachThreadInput(currentThread, targetThread, false);
                if (attachedToForeground)
                    Win32.AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (Win32.GetForegroundWindow() != windowToFocus)
            {
                const byte virtualKeyMenu = 0x12;
                const uint keyEventKeyUp = 0x0002;
                Win32.keybd_event(virtualKeyMenu, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(virtualKeyMenu, 0, keyEventKeyUp, UIntPtr.Zero);
                Win32.SetForegroundWindow(windowToFocus);
            }

            return Win32.GetForegroundWindow() == windowToFocus;
        }
    }
}
