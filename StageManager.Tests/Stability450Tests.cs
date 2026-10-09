using System.Drawing;
using StageManager.Card3DPrototype;

internal static class Stability450Tests
{
    public static void WheelHitTestingIsIndependentAndExpires()
    {
        var bounds = new Rectangle(-1920, 0, 200, 1080);
        Check(SidebarMouseWheelHook.CanHandle(bounds, new Point(-1800, 100), TimeSpan.FromMilliseconds(50)),
            "Mixed-display negative screen coordinates should be accepted.");
        Check(!SidebarMouseWheelHook.CanHandle(bounds, new Point(0, 100), TimeSpan.Zero),
            "Outside application input must pass through.");
        Check(!SidebarMouseWheelHook.CanHandle(bounds, new Point(-1800, 100), TimeSpan.FromMilliseconds(301)),
            "Stalled UI must not swallow input.");
        Check(!SidebarMouseWheelHook.CanHandle(Rectangle.Empty, Point.Empty, TimeSpan.Zero),
            "Hidden sidebar must not swallow input.");
    }

    public static void HookLifecycleDoesNotNeedCallerMessagePump()
    {
        // Constructor and shutdown run on the test thread without a UI loop.
        // The hook's own loop must remain independent and release promptly.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (var hook = new SidebarMouseWheelHook())
        {
            hook.PublishRegion(IntPtr.Zero, Rectangle.Empty);
            Check(hook.TakePendingDelta() == 0, "An empty hook must not invent wheel input.");
        }
        Check(clock.Elapsed < TimeSpan.FromSeconds(2), "Hook shutdown blocked the caller.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
