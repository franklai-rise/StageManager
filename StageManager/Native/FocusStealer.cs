using StageManager.Native.PInvoke;
using System;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
    public static class FocusStealer
    {
        public static bool Steal(IntPtr windowToFocus) => Steal(windowToFocus, restoreIfMinimized: true);

        public static bool Steal(IntPtr windowToFocus, bool restoreIfMinimized)
        {
            if (windowToFocus == IntPtr.Zero || !Win32.IsWindow(windowToFocus))
                return false;
            if (Win32.GetForegroundWindow() == windowToFocus)
                return true;
            // Best effort only: recently hung windows may not yet be marked unresponsive.
            // No synchronous message probe or input-queue attachment on the UI thread.
            try { if (IsHungAppWindow(windowToFocus)) return false; }
            catch (EntryPointNotFoundException) { }

            // Honor Windows' foreground policy. Attaching input queues can block on a hung
            // application and disturb keyboard state; never synthesize Alt/input to bypass it.
            // SW_RESTORE is only issued once by a WindowActivation operation. In particular,
            // never normalize an already maximized window or repeat a pending async restore.
            if (restoreIfMinimized && Win32.IsIconic(windowToFocus))
                Win32.ShowWindowAsync(windowToFocus, Win32.SW.SW_RESTORE);
            Win32.SetForegroundWindow(windowToFocus);

            return Win32.GetForegroundWindow() == windowToFocus;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsHungAppWindow(IntPtr window);
    }
}
