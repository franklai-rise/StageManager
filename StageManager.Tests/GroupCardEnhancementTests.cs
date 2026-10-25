using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using StageManager.Native;
using StageManager.Native.Window;
using StageManager.Settings;
using Windows.UI.Composition;

internal static class GroupCardEnhancementTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    public static async Task OpenAll()
    {
        var windows = Enumerable.Range(1, 7).Select(i => new FakeWindow(i, "App", "app.exe")).ToArray();
        windows[1].ShowMinimized(); windows[5].ShowMinimized();
        var calls = new List<IntPtr>();
        var result = await GroupWindowOpener.OpenAsync(windows, _ => true, (w, _) => {
            calls.Add(w.Handle); w.ShowInCurrentState(); w.Focus(); return Task.FromResult(new WindowOperationResult(WindowOperationOutcome.Confirmed));
        }, CancellationToken.None);
        Check(result == new GroupOpenResult(7, 0, false), "Open did not restore every child, including off-page children.");
        Check(calls.SequenceEqual(windows.Reverse().Select(w => w.Handle)), "Open changed the stable activation order.");
        Check(windows.All(w => !w.IsMinimized), "Open toggled a visible window into minimized state.");
        calls.Clear();
        result = await GroupWindowOpener.OpenAsync([windows[0], windows[1], windows[1], windows[2], new FakeWindow(0, "", "")],
            w => w.Handle != windows[2].Handle, (w, _) => {
                calls.Add(w.Handle);
                if (w.Handle == windows[1].Handle) throw new InvalidOperationException("Synthetic target failure");
                return Task.FromResult(new WindowOperationResult(WindowOperationOutcome.Confirmed));
            }, CancellationToken.None);
        Check(result == new GroupOpenResult(1, 1, false) && calls.Count == 2, "Closed/duplicate handles or partial failures block later siblings.");
        using var cancel = new CancellationTokenSource(); calls.Clear();
        result = await GroupWindowOpener.OpenAsync(windows, _ => true, (w, _) => {
            calls.Add(w.Handle); cancel.Cancel(); return Task.FromResult(new WindowOperationResult(WindowOperationOutcome.Confirmed));
        }, cancel.Token);
        Check(result.Interrupted && calls.Count == 1, "Cancellation continued raising more windows.");
        calls.Clear();
        result = await GroupWindowOpener.OpenAsync(windows, _ => true, (w, _) => {
            calls.Add(w.Handle); return Task.FromResult(new WindowOperationResult(WindowOperationOutcome.Abandoned));
        }, CancellationToken.None);
        Check(result.Interrupted && calls.Count == 1, "New user activity did not stop batch focus attempts.");
    }

    public static void GeometryAndActions()
    {
        Exception? error = null;
        var thread = new Thread(() => { try {
            using var queue = new DispatcherQueueHelper(); queue.EnsureDispatcherQueue();
            using var compositor = new Compositor();
            foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
            foreach (var scale in new[] { .55, .58, 1.25 })
            foreach (var edge in new[] { SidebarEdge.Left, SidebarEdge.Right })
            {
                using var root = compositor.CreateContainerVisual(); using var owner = new Form();
                using var renderer = new CompositionStageRenderer(owner, compositor, root, scale, false, true);
                renderer.Resize(900*dpi, 1400*dpi, dpi); renderer.SetDockEdge(edge); renderer.SetPinButtonEnabled(true);
                renderer.Synchronize([new PrototypeStageSnapshot("empty", "Closing application", [], DateTime.UtcNow)]);
                var counts = new[] { 1, 2, 7, 1, 3 };
                renderer.Synchronize(counts.Select((count, i) => new PrototypeStageSnapshot("g"+i, "Group "+i,
                    Enumerable.Range(1, count).Select(j => (IWindow)new FakeWindow(91000+i*100+j, "test", "test.exe")).ToArray(), DateTime.UtcNow)).ToArray());
                var stages = Field<Dictionary<string, StageCardVisual>>(renderer, "_stages");
                var hits = Field<List<CardHitTarget>>(renderer, "_hitTargets");
                var camera = new Vector2(450*dpi, 700*dpi);
                (float Top, float Bottom) Bounds(StageCardVisual stage) {
                    var cards = stage.Windows.Where(c => c.IsVisible).ToList();
                    if (stage.GroupCard is { IsVisible: true } group) cards.Add(group);
                    var corners = cards.SelectMany(c => Card3DGeometry.ProjectCard(stage.Motion.Target.Offset, 1,
                        c.Motion.Target.Offset, c.Motion.Target.Scale, c.Motion.Target.Angle, c.Root.Size, c.Pivot, camera, 1200*dpi)).ToArray();
                    return (corners.Min(p=>p.Y), corners.Max(p=>p.Y));
                }
                for (var i=1;i<counts.Length;i++)
                    Check(Math.Abs(Bounds(stages["g"+i]).Top-Bounds(stages["g"+(i-1)]).Bottom-14*dpi)<.1f,
                        $"Collapsed stack whitespace differs at {edge}/{dpi}/{scale}, index={i}.");
                var first = stages["g0"].Motion.Target.Offset;
                var anchor = stages["g2"].Motion.Target.Offset;
                renderer.Activate(hits.Single(h=>h.StageKey=="g2" && h.IsPrimaryCard));
                var pin = hits.Single(h=>h.StageKey=="g2" && h.IsPinButton);
                var open = hits.Single(h=>h.StageKey=="g2" && h.IsOpenGroupButton);
                Point Center(CardHitTarget h) => new((int)h.Polygon.Average(p=>p.X), (int)(h.Polygon.Average(p=>p.Y)+renderer.ScrollTranslationY));
                Check(renderer.HitTest(Center(pin))?.IsPinButton == true && renderer.HitTest(Center(open))?.IsOpenGroupButton == true,
                    "FIX and OPEN clicks overlap or miss their visual.");
                Check(!CardInteraction.SameTarget(pin,open) && !CardInteraction.IsWindowCard(open), "OPEN enters the minimize toggle path.");
                Check(stages["g0"].Motion.Target.Offset==first && stages["g2"].Motion.Target.Offset==anchor, "Adding OPEN or expanding moved an upper/primary card.");
                renderer.Activate(pin); renderer.Activate(open);
                Check(renderer.IsStagePinned("g2") && renderer.IsStageExpanded("g2"), "OPEN collapsed or unpinned the group.");
                Check(renderer.ConsumeOpenGroupRequest()=="g2" && renderer.ConsumeOpenGroupRequest() is null, "OPEN dispatch is not single-consumption.");
                renderer.SetOpenGroupBusy("g2",true); renderer.Activate(open);
                Check(renderer.ConsumeOpenGroupRequest() is null, "Busy OPEN requeued a batch.");
                renderer.SetOpenGroupBusy("g2",false);
                renderer.Scroll(-120); open=hits.Single(h=>h.StageKey=="g2" && h.IsOpenGroupButton);
                Check(renderer.HitTest(Center(open))?.IsOpenGroupButton==true, "Scrolled OPEN hit does not follow its button.");
                renderer.SetPinButtonEnabled(false);
                Check(hits.Any(h=>h.IsOpenGroupButton) && !hits.Any(h=>h.IsPinButton), "OPEN incorrectly depends on FIX being enabled.");
                renderer.SetPinButtonEnabled(true);
                renderer.Activate(hits.Single(h=>h.StageKey=="g2" && h.IsPinButton));
                renderer.SetDockEdge(SidebarEdge.Top);
                Check(!hits.Any(h=>h.IsOpenGroupButton || h.IsPinButton), "Horizontal three-row mode unexpectedly gained group overlays.");
                renderer.SetDockEdge(edge);
                Check(renderer.IsStagePinned("g2"), "Dock switch lost a pinned group.");
            }
        } catch(Exception e) { error=e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(45)), "Group geometry tests timed out.");
        if(error is not null) throw new Exception("Group geometry regression",error);
    }
}
