using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using StageManager.Settings;
using Windows.UI.Composition;

internal static class VerticalEdgeRendererTests
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Flags)!.GetValue(value)!;
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    public static void CheckRenderer(Compositor compositor)
    {
        foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (var edge in new[] { SidebarEdge.Left, SidebarEdge.Right })
        {
            using var root = compositor.CreateContainerVisual();
            using var owner = new Form(); // No native handle; never moves the user's pointer/windows.
            using var renderer = new CompositionStageRenderer(owner, compositor, root, .6, false, true);
            renderer.SetSidebarVerticalOffset(0);
            renderer.SetPinButtonEnabled(true);
            renderer.Resize(900 * dpi, 700 * dpi, dpi);
            renderer.SetDockEdge(edge);
            var groups = Enumerable.Range(0, 12).Select(i => new PrototypeStageSnapshot("group" + i, "Group " + i,
                Enumerable.Range(0, i < 2 ? 2 : 1).Select(j => (StageManager.Native.Window.IWindow)new FakeWindow(85000 + i * 10 + j, "test", "test.exe")).ToArray(), DateTime.UtcNow)).ToArray();
            renderer.Synchronize(groups);
            var hits = Field<List<CardHitTarget>>(renderer, "_hitTargets");
            var controller = Field<HorizontalEdgeScrollController>(renderer, "_horizontalEdgeScroll");
            var motion = Field<SidebarScrollMotion>(renderer, "_scrollMotion");
            foreach (var key in new[] { "group0", "group1" })
            {
                renderer.Activate(hits.Single(h => h.StageKey == key && h.IsPrimaryCard));
                renderer.Activate(hits.Single(h => h.StageKey == key && h.IsPinButton));
            }
            var order = hits.Where(h => h.Window is not null).Select(h => h.Window!.Handle).ToArray();
            var band = renderer.InteractionBounds;
            var x = edge == SidebarEdge.Left ? band.Left + 1 : band.Right - 1;
            var bottom = new Point(x, band.Bottom - 2);
            Check(renderer.HitTest(bottom) is null, "Blank edge stopped being click-through.");
            var range = Field<SidebarScrollRange>(renderer, "_verticalEdgeRange");
            Check(range.Maximum > 0, "Tall column has no edge overflow.");
            renderer.UpdateEdgeScroll(bottom, false);
            Check(controller.Velocity > 0 && renderer.IsScrolling, "Vertical trailing edge did not start.");
            var revision = renderer.LayoutRevision;
            typeof(CompositionStageRenderer).GetField("_lastScrollTick", Flags)!.SetValue(renderer, Stopwatch.GetTimestamp() - Stopwatch.Frequency / 60);
            typeof(CompositionStageRenderer).GetMethod("AdvanceScroll", Flags)!.Invoke(renderer, null);
            Check(renderer.ScrollTranslationY < 0 && renderer.ScrollTranslationX == 0, "Wrong auto-scroll axis.");
            Check(renderer.LayoutRevision == revision, "Auto-scroll rebuilt the card layout.");
            Check(renderer.IsStagePinned("group0") && renderer.IsStagePinned("group1"), "Auto-scroll lost independent FIX state.");
            Check(order.SequenceEqual(hits.Where(h => h.Window is not null).Select(h => h.Window!.Handle)), "Auto-scroll changed grouped child order.");
            renderer.UpdateEdgeScroll(new Point(band.Right + 1, bottom.Y), false);
            Check(controller.Velocity == 0 && !renderer.IsScrolling, "Leaving the band did not stop immediately.");
            renderer.UpdateEdgeScroll(new Point(x, band.Top + 2), false);
            Check(controller.Velocity < 0, "Leading edge did not reverse direction.");
            renderer.UpdateEdgeScroll(bottom, true);
            Check(controller.Velocity == 0, "Menu/button blocking did not stop.");
            renderer.UpdateEdgeScroll(bottom, false);
            renderer.SetSidebarVisible(false, false);
            Check(controller.Velocity == 0, "Hidden bar retained edge motion.");
            renderer.SetSidebarVisible(true, false);

            // Move an expanded group into the upper activation zone. Hovering either
            // its main card or FIX must take priority over edge travel, even when pinned.
            var main = hits.Single(h => h.StageKey == "group1" && h.IsPrimaryCard);
            var centerX = (int)main.Polygon.Average(p => p.X);
            var centerY = (float)main.Polygon.Average(p => p.Y);
            motion.MoveBounded(centerY - 12 * dpi - motion.Position, range);
            typeof(CompositionStageRenderer).GetMethod("ApplyScrollTranslation", Flags)!.Invoke(renderer, null);
            var mainPoint = new Point(centerX, (int)(centerY + renderer.ScrollTranslationY));
            var live = renderer.HitTest(mainPoint);
            Check(live is { IsPrimaryCard: true } or { IsPinButton: true }, "Group interaction fixture missed.");
            renderer.UpdateEdgeScroll(mainPoint, false);
            Check(controller.Velocity == 0 && !renderer.IsScrolling, "Group/FIX hover was starved by edge motion.");

            // A moving card passing underneath the pointer is not a fresh hover.
            motion.MoveBounded((range.Minimum+range.Maximum)/2-motion.Position, range);
            typeof(CompositionStageRenderer).GetMethod("ApplyScrollTranslation", Flags)!.Invoke(renderer, null);
            renderer.UpdateEdgeScroll(bottom,false);
            Check(controller.Velocity>0,"Could not begin continuity fixture.");
            var centerYInContent=bottom.Y-renderer.ScrollTranslationY;
            var passing=main with { Projection=null, Polygon=new System.Numerics.Vector2[] {
                new(bottom.X-40*dpi,centerYInContent-40*dpi), new(bottom.X+40*dpi,centerYInContent-40*dpi),
                new(bottom.X+40*dpi,centerYInContent+40*dpi), new(bottom.X-40*dpi,centerYInContent+40*dpi) } };
            hits.Insert(0,passing);
            renderer.UpdateEdgeScroll(bottom,false);
            Check(controller.Velocity>0 && renderer.IsScrolling,"Passing primary card stopped edge travel without pointer movement.");
            renderer.UpdateEdgeScroll(new Point(bottom.X,bottom.Y-(int)(8*dpi)),false);
            Check(controller.Velocity==0 && !renderer.IsScrolling,"Deliberately targeting the primary did not pause edge travel.");
            hits.Remove(passing);

            renderer.UpdateEdgeScroll(bottom, false);
            renderer.Scroll(-120);
            renderer.UpdateEdgeScroll(bottom, false);
            Check(controller.Velocity == 0 && controller.IsWheelSuppressed, "Wheel did not take priority.");
            renderer.SetDockEdge(SidebarEdge.Top);
            Check(controller.Velocity == 0, "Dock switch retained old axis velocity.");
        }
    }
}
