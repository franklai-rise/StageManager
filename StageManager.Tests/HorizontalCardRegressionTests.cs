using System.Drawing;
using System.Numerics;
using StageManager.Card3DPrototype;
using StageManager.Native.Window;
using StageManager.Settings;

internal static class HorizontalCardRegressionTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("Horizontal: " + message); }
    internal static void Check(CompositionStageRenderer renderer, List<CardHitTarget> hits)
    {
        static PrototypeStageSnapshot Group(int count) => new("group", "group",
            Enumerable.Range(51000, count).Select(i => (IWindow)new FakeWindow(i, "Application", "test.exe")).ToArray(), DateTime.UtcNow);
        static RectangleF Bounds(CardHitTarget hit) => RectangleF.FromLTRB(hit.Polygon.Min(p => p.X), hit.Polygon.Min(p => p.Y), hit.Polygon.Max(p => p.X), hit.Polygon.Max(p => p.Y));
        renderer.SetAiUsageCardEnabled(false); renderer.SetNotificationAreaCardEnabled(false);
        renderer.SetQuickLaunchApps(QuickLaunchDefinition.LegacyDefaults());
        foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (var screen in new[] { new Size(1080,1920), new Size(1920,1080) })
        foreach (var edge in new[] { SidebarEdge.Top, SidebarEdge.Bottom })
        foreach (var count in new[] { 1, 2, 3, 4, 6, 7, 10, 20 })
        {
            renderer.Resize(screen.Width * dpi, screen.Height * dpi, dpi); renderer.SetDockEdge(edge);
            var snapshot = Group(count); renderer.Synchronize([snapshot]); renderer.FinishScroll();
            var children = hits.Where(h => h.Window is not null).OrderBy(h => h.Window!.Handle.ToInt64()).ToArray();
            Assert(children.Length == count, "missing windows");
            Assert(!hits.Any(h => h.IsExpandAllButton || h.IsPinButton || h.IsTopPanel), "legacy expansion leaked");
            Assert(renderer.TopPanelBounds.IsEmpty, "shared panel remained visible");
            Assert(renderer.SidebarThickness >= 228 * dpi, "height floor changed");
            var reserved = renderer.SidebarThickness;
            for (var i = 0; i < count; i++)
            {
                var hit = children[i]; var b = Bounds(hit);
                Assert(!renderer.CanExpandOnHover(hit), "hover expansion enabled");
                Assert(renderer.Activate(hit)?.Handle == hit.Window!.Handle, "row activated wrong window");
                var at = new Point((int)(hit.Polygon.Average(p => p.X) + renderer.ScrollTranslationX), (int)hit.Polygon.Average(p => p.Y));
                Assert(renderer.HitTest(at)?.Window?.Handle == hit.Window!.Handle, "row projection mismatch");
                Assert(b.Top >= 0 && b.Bottom <= screen.Height * dpi, "cross-axis clipping");
                if (i % 3 != 0) Assert(!children[i-1].Polygon.Any(p => Card3DGeometry.Contains(hit.Polygon, p)), "rows overlap");
                else if (i > 0) Assert(Bounds(children[i-1]).Right < b.Left, "continuation overlaps");
            }
            Assert(renderer.SidebarThickness == reserved, "window activation changed reservation");
            var shortcutHits = hits.Where(h => h.IsExplorerButton || h.QuickLaunchApp is not null).OrderBy(h => Bounds(h).Top).ToArray();
            Assert(shortcutHits.Length == 3, "shortcut count");
            Assert(shortcutHits.Select(h => Math.Round(Bounds(h).Left)).Distinct().Count() <= 2, "shortcuts not combined");
            var actions = hits.Where(h => h.IsDesktopButton || h.IsDesktopIconsButton || h.IsSidebarCollapseButton).OrderBy(h=>Bounds(h).Top).ToArray();
            Assert(actions.Length == 3 && actions[0].Polygon.Average(p=>p.Y) < actions[1].Polygon.Average(p=>p.Y) && actions[1].Polygon.Average(p=>p.Y) < actions[2].Polygon.Average(p=>p.Y), "actions not three rows");
            renderer.Scroll(-120); renderer.FinishScroll();
            var moved = children[0]; var point = new Point((int)(moved.Polygon.Average(p=>p.X)+renderer.ScrollTranslationX), (int)moved.Polygon.Average(p=>p.Y));
            Assert(renderer.HitTest(point)?.Window?.Handle == moved.Window!.Handle, "scrolled row mismatch");
            Assert(hits.Where(h=>h.Window is not null).OrderBy(h=>h.Window!.Handle.ToInt64()).Select(h=>h.Window!.Handle).SequenceEqual(snapshot.Windows.Select(w=>w.Handle)), "activation reordered windows");
        }
        renderer.SetDockEdge(SidebarEdge.Top); renderer.Resize(600,1000,1); renderer.Synchronize([Group(20)]);
        Thread.Sleep(300); // Allow the real renderer's wheel-priority cooldown to expire.
        renderer.UpdateEdgeScroll(new Point(599,90), false); Assert(renderer.IsScrolling,"edge did not start");
        renderer.StopEdgeScroll(); Assert(!renderer.IsScrolling,"edge did not stop");
        renderer.UpdateEdgeScroll(new Point(599,900), false); Assert(!renderer.IsScrolling,"outside band scrolled");
        renderer.Synchronize([Group(2)]);
        renderer.SetDockEdge(SidebarEdge.Left);
        Assert(hits.Any(h=>h.IsExpandAllButton), "vertical expand-all missing");
        var primary = hits.Single(h=>h.StageKey=="group" && h.IsPrimaryCard); renderer.Activate(primary);
        Assert(hits.Count(h=>h.StageKey=="group" && h.Window is not null)==2,"vertical expansion broken");
        renderer.SetPinButtonEnabled(true);
        renderer.Activate(hits.Single(h=>h.StageKey=="group" && h.IsPinButton));
        renderer.SetDockEdge(SidebarEdge.Bottom); renderer.SetDockEdge(SidebarEdge.Left);
        Assert(renderer.IsStagePinned("group"),"edge switch lost fixed state");
        renderer.SetDockEdge(SidebarEdge.Top); renderer.SetQuickLaunchApps([]);
        Assert(hits.Count(h=>h.IsExplorerButton)==1 && !hits.Any(h=>h.QuickLaunchApp is not null),"shortcut change not reflected");
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var stages = (Dictionary<string, StageCardVisual>)typeof(CompositionStageRenderer).GetField("_stages", flags)!.GetValue(renderer)!;
        var rowCard = stages["group"].Windows[0];
        var caption = (TopPanelLabel)typeof(WindowCardVisual).GetField("_topCaption",flags)!.GetValue(rowCard)!;
        Assert(caption.LightSurface,"Row title reverted to a dark box");
        rowCard.UpdateWindow(rowCard.Window);
        Assert(caption.LightSurface,"Title refresh restored dark box");
        var parent = rowCard.Root.Parent;
        renderer.SetQuickLaunchApps([]);
        Assert(rowCard.Root.Parent == parent,"no-op shortcuts detached the row");
        renderer.SetDockEdge(SidebarEdge.Bottom);
        var content = (Windows.UI.Composition.SpriteVisual)typeof(WindowCardVisual).GetField("_content",flags)!.GetValue(rowCard)!;
        Assert(content.Size.Y < rowCard.Root.Size.Y && rowCard.FitMode==PreviewFitMode.Contain,"top/bottom lost caption separation or aspect fit");
        renderer.SetDesktopButtonEnabled(false); renderer.SetDesktopIconsButtonEnabled(false);
        Assert(hits.Count(h=>h.IsSidebarCollapseButton)==1 && !hits.Any(h=>h.IsDesktopButton || h.IsDesktopIconsButton),"disabled actions retained slots");

        var closingCard = stages["group"].Windows.Last();
        var closingHit = hits.Single(h=>h.Window?.Handle==closingCard.Window.Handle);
        typeof(CompositionStageRenderer).GetMethod("SetTopHoveredCard",flags)!.Invoke(renderer,[closingHit]);
        renderer.Synchronize([Group(1)]);
        Assert(typeof(CompositionStageRenderer).GetField("_topHoveredCard",flags)!.GetValue(renderer) is null,"closed window retained hover state");
        closingCard.SetTopHovered(true); // Late pointer cleanup must tolerate a disposed visual.

        renderer.SetDockEdge(SidebarEdge.Left);
        var capturedCard = stages["group"].Windows[0];
        using var deferredFrame = new CapturedCardFrame(capturedCard.Window.Handle,System.Buffers.ArrayPool<byte>.Shared.Rent(4),1,1,false);
        var completionType = typeof(CompositionStageRenderer).GetNestedType("CaptureCompletion",System.Reflection.BindingFlags.NonPublic)!;
        var completion = Activator.CreateInstance(completionType,capturedCard,capturedCard.MarkCaptureStarted(),deferredFrame,null);
        var deferredField = typeof(CompositionStageRenderer).GetField("_deferredCapture",flags)!;
        deferredField.SetValue(renderer,completion);
        renderer.SetDockEdge(SidebarEdge.Top);
        Assert(deferredField.GetValue(renderer) is null,"horizontal switch blocked the capture scheduler");
        var released = false;
        try { _ = deferredFrame.Pixels.Length; } catch(ObjectDisposedException) { released = true; }
        Assert(released,"horizontal switch retained deferred screenshot pixels");
        renderer.SetDockEdge(SidebarEdge.Left);
    }
}
