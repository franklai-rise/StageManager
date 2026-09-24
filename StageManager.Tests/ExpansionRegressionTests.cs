using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using StageManager.Native.Window;
using StageManager.Card3DPrototype;
using Windows.UI.Composition;

internal static class ExpansionRegressionTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(20)), "Expansion check timed out.");
        if (error is not null) throw new InvalidOperationException("Expansion regression failed.", error);
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static CardHitTarget[] Hits(CompositionStageRenderer renderer) => Field<List<CardHitTarget>>(renderer, "_hitTargets").ToArray();
    private static CardHitTarget Primary(CompositionStageRenderer renderer, string key) =>
        Hits(renderer).Single(hit => hit.StageKey == key && hit.IsPrimaryCard);
    private static Point Center(CardHitTarget target, float scroll = 0)
    {
        var points = target.Projection?.CurrentPolygon() ?? target.Polygon;
        return new Point((int)points.Average(p => p.X), (int)(points.Average(p => p.Y) + scroll));
    }

    public static void AnchoredExpansion() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var root = compositor.CreateContainerVisual();
            using var owner = new Form();
            using var renderer = new CompositionStageRenderer(owner, compositor, root, 0.8, false, true);
            renderer.Resize(700 * dpi, 1400 * dpi, dpi);
            renderer.Synchronize([
                new PrototypeStageSnapshot("above", "Above", [new FakeWindow(101, "Above", "above.exe")], DateTime.UtcNow),
                new PrototypeStageSnapshot("group", "Group", Enumerable.Range(201, 5).Select(i => (IWindow)new FakeWindow(i, "Group", "group.exe")).ToArray(), DateTime.UtcNow),
                new PrototypeStageSnapshot("below", "Below", [new FakeWindow(301, "Below", "below.exe")], DateTime.UtcNow)
            ]);
            foreach (var useHover in new[] { false, true })
            foreach (var scrolled in new[] { false, true })
            {
                renderer.CollapseExpandedStage(true);
                if (scrolled) renderer.Scroll(120);
                renderer.UpdatePointer(Center(Primary(renderer, "group"), renderer.ScrollTranslationY));
                var before = Primary(renderer, "group").Polygon.ToArray();
                var above = Primary(renderer, "above").Polygon.ToArray();
                var header = Hits(renderer).Single(hit => hit.IsExplorerButton).Polygon.ToArray();
                var scroll = renderer.ScrollTranslationY;
                if (useHover)
                    Check(renderer.TryExpandHoveredPrimaryCard(Center(Primary(renderer, "group"), scroll), "group"), "Hover did not expand.");
                else renderer.Activate(Primary(renderer, "group"));
                Check(renderer.IsStageExpanded("group"), "Click did not expand.");
                Check(before.SequenceEqual(Primary(renderer, "group").Polygon), "Expansion moved the original group card.");
                Check(above.SequenceEqual(Primary(renderer, "above").Polygon), "Expansion pushed an upper card.");
                Check(header.SequenceEqual(Hits(renderer).Single(hit => hit.IsExplorerButton).Polygon), "Expansion moved the shortcut row.");
                Check(scroll == renderer.ScrollTranslationY, "Expansion clamped the existing scroll position.");
                var children = Hits(renderer).Where(hit => hit.StageKey == "group" && hit.Window is not null)
                    .OrderBy(hit => hit.Polygon.Min(p => p.Y)).ToArray();
                Check(children.Length == 5, "A child window disappeared.");
                Check(children[0].Polygon.Min(p => p.Y) > before.Max(p => p.Y), "A child was laid out above the primary.");
                for (var i = 1; i < children.Length; i++)
                    Check(children[i].Polygon.Min(p => p.Y) > children[i - 1].Polygon.Max(p => p.Y), "Expanded children overlap.");
            }
            renderer.CollapseExpandedStage(true);
            var fixedHeader = Hits(renderer).Single(hit => hit.IsExplorerButton).Polygon.ToArray();
            renderer.Activate(Hits(renderer).Single(hit => hit.IsExpandAllButton));
            Check(fixedHeader.SequenceEqual(Hits(renderer).Single(hit => hit.IsExplorerButton).Polygon), "Expand-all moved the header.");
        }
    });

    public static void MotionAndHits() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var stage = compositor.CreateContainerVisual();
        using var card = compositor.CreateContainerVisual();
        var parent = new CardVisualMotion(compositor, stage);
        var child = new CardVisualMotion(compositor, card);
        parent.Set(Vector3.Zero, Vector3.One, 0, false);
        child.Set(Vector3.Zero, Vector3.One, -7.5f, false);
        child.Set(new Vector3(18, 300, 4), Vector3.One, -7.5f, true);
        var projection = new CardHitProjection(parent, child, new Vector2(160, 100), new Vector2(140, 50), new Vector2(350, 700), 1200);
        Check(projection.IsMoving, "Animation did not begin.");
        Check(projection.CurrentPolygon().Average(p => p.Y) < 220, "Hit target jumped to the final position immediately.");
        var midpoint = CardVisualMotion.Interpolate(new(Vector3.Zero, Vector3.One, 0),
            new(new Vector3(0, 100, 0), Vector3.One, 10), 0.5);
        Check(Math.Abs(midpoint.Offset.Y - 87.5f) < 0.001f, "The hit curve differs from the compositor curve.");
        child.Set(new Vector3(18, 300, 4), Vector3.One, -7.5f, false);
        Check(!child.IsMoving && card.Offset.Y == 300 && child.Current == child.Target, "Disabling animation left a stale visual.");
        Check(projection.CurrentPolygon().Average(p => p.Y) > 300, "Settled hit geometry did not reach the card.");
    });

    public static void DpiChangesRebuildExistingCards() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var root = compositor.CreateContainerVisual();
        using var owner = new Form();
        using var renderer = new CompositionStageRenderer(owner, compositor, root, 0.8, false, true);
        renderer.Resize(900, 1800, 1f);
        renderer.Synchronize([new PrototypeStageSnapshot("dpi", "DPI", [
            new FakeWindow(61001, "One", "dpi.exe"), new FakeWindow(61002, "Two", "dpi.exe")], DateTime.UtcNow)]);
        renderer.Activate(Primary(renderer, "dpi"));
        var original = Field<Dictionary<string, StageCardVisual>>(renderer, "_stages")["dpi"].Windows[0];
        var size = original.Root.Size;
        foreach (var dpi in new[] { 1.25f, 2f, 1f })
        {
            renderer.Resize(900, 1800, dpi);
            var stage = Field<Dictionary<string, StageCardVisual>>(renderer, "_stages")["dpi"];
            Check(stage.Windows[0].Root.Size == size * dpi,
                "Changing display DPI updated the hits but kept an old-size visual.");
            Check(stage.Windows[0].SurfaceWidth == Math.Max(128, (int)Math.Ceiling(size.X * dpi * 1.25f)),
                "Changing display DPI retained the wrong backing texture size.");
            Check(renderer.IsStageExpanded("dpi") && stage.Windows.Select(c => c.Window.Handle).SequenceEqual(new IntPtr[] { 61001, 61002 }),
                "DPI reconstruction changed the expanded group or child order.");
            var child = Hits(renderer).First(hit => hit.Window?.Handle == (IntPtr)61001);
            Check(renderer.HitTest(Center(child, renderer.ScrollTranslationY))?.Window?.Handle == (IntPtr)61001,
                "A child hit no longer follows its visual after changing DPI.");
        }
    });

    public static void ScrollDuringExpansion()
    {
        var motion = new SidebarScrollMotion();
        motion.SetRange(new SidebarScrollRange(-100, 100), 1);
        motion.AddWheel(2400, 50, true);
        motion.Advance(0.07);
        var position = motion.Position;
        var target = motion.Target;
        motion.SetRange(new SidebarScrollRange(-100, 900), 1);
        Check(motion.Position == position && motion.Target == target, "Growing the column snapped a live wheel spring.");
    }

    public static void LateCaptureCannotUndoInvalidation() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var graphics = new D3DCompositionDevice(true);
        var window = new FakeWindow(62001, "Capture", "capture.exe");
        using var card = new WindowCardVisual(compositor, graphics, window, 128, 80, new Vector2(128, 80), false);
        var oldRevision = card.MarkCaptureStarted();
        var bytes = ArrayPool<byte>.Shared.Rent(128 * 80 * 4);
        Array.Clear(bytes);
        using var frame = new CapturedCardFrame(window.Handle, bytes, 128, 80, false);
        window.ShowMinimized();
        card.UpdateWindow(window);
        Check(!card.TryUpload(frame, oldRevision) && !card.HasSurface && card.NeedsCapture(DateTime.UtcNow, 5),
            "A late pre-minimize screenshot overwrote the current placeholder request.");
        var currentRevision = card.MarkCaptureStarted();
        card.MarkCaptureFailed(oldRevision);
        Check(card.TryUpload(frame, currentRevision), "An old failure invalidated the newer screenshot.");
        card.InvalidateCapture();
        var failedRevision = card.MarkCaptureStarted();
        card.MarkCaptureFailed(failedRevision);
        Check(!card.NeedsCapture(DateTime.UtcNow.AddSeconds(6), 5) && card.NeedsCapture(DateTime.UtcNow.AddMinutes(6), 5),
            "A failed capture did not respect the existing five-minute refresh policy.");
        card.Dispose();
        Check(!card.TryUpload(frame, failedRevision) && !card.NeedsCapture(DateTime.UtcNow.AddMinutes(10), 5),
            "A destroyed card still accepted background work.");
    });

    public static void QueuedCaptureIsReleasedOnShutdown() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var root = compositor.CreateContainerVisual();
        using var owner = new Form();
        using var renderer = new CompositionStageRenderer(owner, compositor, root, 0.8, false, true);
        renderer.Resize(900, 1400, 1);
        renderer.Synchronize([new PrototypeStageSnapshot("queued", "Queued", [new FakeWindow(63001, "Queued", "queued.exe")], DateTime.UtcNow)]);
        var card = Field<Dictionary<string, StageCardVisual>>(renderer, "_stages")["queued"].Windows[0];
        var bytes = ArrayPool<byte>.Shared.Rent(card.SurfaceWidth * card.SurfaceHeight * 4);
        using var frame = new CapturedCardFrame(card.Window.Handle, bytes, card.SurfaceWidth, card.SurfaceHeight, false);
        var completionType = typeof(CompositionStageRenderer).GetNestedType("CaptureCompletion", BindingFlags.NonPublic)!;
        var completion = Activator.CreateInstance(completionType, card, card.MarkCaptureStarted(), frame, null);
        typeof(CompositionStageRenderer).GetField("_pendingCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(renderer, completion);
        renderer.Dispose();
        var released = false;
        try { _ = frame.Pixels.Length; } catch (ObjectDisposedException) { released = true; }
        Check(released, "Closing before the UI completion callback leaked its pooled pixels.");
    });

    public static void RapidExpansion() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var root = compositor.CreateContainerVisual();
        using var owner = new Form();
        using var renderer = new CompositionStageRenderer(owner, compositor, root, 0.8, true, true);
        renderer.Resize(600, 1400, 1.25f);
        renderer.Synchronize(Enumerable.Range(0, 10).Select(group => new PrototypeStageSnapshot(
            "app" + group, "Application " + group,
            Enumerable.Range(1, 3).Select(window => (IWindow)new FakeWindow(10000 + group * 10 + window, "Application", "test.exe")).ToArray(),
            DateTime.UtcNow)).ToArray());
        var anchor = Hits(renderer).Single(hit => hit.IsExplorerButton).Polygon.ToArray();
        var durations = new List<double>();
        for (var repetition = 0; repetition < 100; repetition++)
        {
            var start = Stopwatch.GetTimestamp();
            var key = "app" + repetition % 10;
            renderer.Activate(Primary(renderer, key));
            var children = Hits(renderer).Where(hit => hit.StageKey == key && hit.Window is not null).ToArray();
            Check(children.Length == 3, "Rapid expansion lost a window.");
            // A resolved card always carries the clicked application's own window.
            foreach (var child in children)
            {
                var resolved = renderer.HitTest(Center(child, renderer.ScrollTranslationY));
                if (resolved?.Window is not null)
                    Check(resolved.StageKey == key, "A moving child hit activated a different application.");
            }
            renderer.CollapseExpandedStage(true);
            Check(anchor.SequenceEqual(Hits(renderer).Single(hit => hit.IsExplorerButton).Polygon), "Repeated expansion drifted the header.");
            durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / 2);
        }
        durations.Sort();
        Console.WriteLine($"Expansion timing (10 groups / 30 windows, 200 transitions): median {durations[50]:F2} ms, p95 {durations[95]:F2} ms per layout operation.");
    });

    public static void WarmPreviews() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var root = compositor.CreateContainerVisual();
        using var owner = new Form();
        using var renderer = new CompositionStageRenderer(owner, compositor, root, 1.25, false, true);
        renderer.Resize(900, 1800, 2);
        renderer.Synchronize([new PrototypeStageSnapshot("cache", "Cache",
            Enumerable.Range(1001, 12).Select(i => (IWindow)new FakeWindow(i, "Cache", "cache.exe")).ToArray(), DateTime.UtcNow)]);
        var cards = Field<Dictionary<string, StageCardVisual>>(renderer, "_stages")["cache"].Windows;
        foreach (var card in cards)
        {
            card.SetVisible(true);
            var bytes = ArrayPool<byte>.Shared.Rent(card.SurfaceWidth * card.SurfaceHeight * 4);
            Array.Clear(bytes);
            using var frame = new CapturedCardFrame(card.Window.Handle, bytes, card.SurfaceWidth, card.SurfaceHeight, true);
            card.Upload(frame);
            var captured = card.LastCaptureUtc;
            card.SetVisible(false);
            Check(card.HasSurface && card.LastCaptureUtc == captured, "Collapse discarded a reusable preview.");
            card.SetVisible(true);
            Check(!card.NeedsCapture(DateTime.UtcNow, 5), "Reopening scheduled a redundant screenshot.");
            card.SetVisible(false);
        }
        var trim = typeof(CompositionStageRenderer).GetMethod("TrimWarmPreviews", BindingFlags.Instance | BindingFlags.NonPublic)!;
        trim.Invoke(renderer, null);
        var retained = cards.Where(card => card.HasSurface).ToArray();
        Check(retained.Length is > 0 and <= 8 && retained.Sum(card => card.EstimatedSurfaceBytes) <= 4L * 1024 * 1024,
            "The hidden-preview cache exceeded its count or byte limit.");
        foreach (var card in retained)
            typeof(WindowCardVisual).GetProperty(nameof(WindowCardVisual.HiddenSinceUtc))!.SetValue(card, DateTime.UtcNow.AddSeconds(-9));
        trim.Invoke(renderer, null);
        Check(cards.All(card => !card.HasSurface), "Expired previews were not released.");
    });

    public static void IdleCaptureSchedulingAllocations() => OnSta(() =>
    {
        using var dispatcher = new DispatcherQueueHelper();
        dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var root = compositor.CreateContainerVisual();
        using var owner = new Form();
        using var renderer = new CompositionStageRenderer(owner, compositor, root, 0.8, false, true);
        renderer.Resize(900, 1800, 1);
        renderer.Synchronize(Enumerable.Range(0, 10).Select(i => new PrototypeStageSnapshot(
            "idle" + i, "Idle", [new FakeWindow(65000 + i, "Idle", "idle.exe")], DateTime.UtcNow)).ToArray());
        foreach (var stage in Field<Dictionary<string, StageCardVisual>>(renderer, "_stages").Values)
            foreach (var card in stage.Windows) card.MarkCaptureStarted();
        var schedule = (Action)typeof(CompositionStageRenderer).GetMethod("ScheduleCaptures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action), renderer);
        for (var i = 0; i < 100; i++) schedule();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) schedule();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Idle capture scheduler (10 cards, 1000 checks): {allocated} managed bytes.");
        Check(allocated < 128 * 1024, "Unchanged preview checks allocate unnecessary per-window pipelines.");
    });
}
