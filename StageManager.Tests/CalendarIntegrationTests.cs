using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Numerics;
using StageManager.Card3DPrototype;
using StageManager.Card3DPrototype.Calendar;
using StageManager.Card3DPrototype.QuickLaunch;
using StageManager.Settings;
using Windows.UI.Composition;

internal static class CalendarIntegrationTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { CheckRenderer(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new Exception("Calendar rendering timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void CheckRenderer()
    {
        using var dispatcher = new DispatcherQueueHelper(); dispatcher.EnsureDispatcherQueue();
        using var compositor = new Compositor();
        using var root = compositor.CreateContainerVisual();
        using var owner = new Form();
        using var renderer = new CompositionStageRenderer(owner, compositor, root, .6, false, true);
        var hits = (List<CardHitTarget>)typeof(CompositionStageRenderer).GetField("_hitTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        Check(!hits.Any(t => t.IsCalendarCard), "Calendar is not default-off.");
        renderer.SetSidebarVerticalOffset(0);
        renderer.SetCalendarCardEnabled(true);
        object Field(string name) => typeof(CompositionStageRenderer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        Vector2 SizeOf(object visual) => (Vector2)visual.GetType().GetProperty("Size")!.GetValue(visual)!;
        var calendar = (CalendarCardVisual)Field("_calendarCard");
        var apps = Enumerable.Range(0, 7).Select(i => QuickLaunchCatalog.FromFile(@"C:\Missing-StageManager-Test\CalendarLauncher" + i + ".exe")).ToArray();
        foreach (var count in new[] { 0, 1, 2, 3, 4, 7 })
        foreach (var cardScale in new[] { .55, 1.1 })
        foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (var viewport in new[] { new Size(1440, 2560), new Size(2560, 1440) })
        foreach (var edge in Enum.GetValues<SidebarEdge>())
        {
            renderer.SetQuickLaunchApps(apps.Take(count));
            renderer.SetExplorerButtonEnabled(count > 0);
            renderer.SetCardScale(cardScale);
            renderer.Resize(viewport.Width * dpi, viewport.Height * dpi, dpi); renderer.SetDockEdge(edge);
            renderer.SetAiUsageCardEnabled(true);
            var hit = hits.Single(t => t.IsCalendarCard);
            bool horizontal = SidebarDockLayout.IsHorizontal(edge);
            var launchers = ((System.Collections.IEnumerable)Field("_quickLaunchButtons")).Cast<object>().ToArray();
            var expected = launchers.LastOrDefault() is { } last ? SizeOf(last) : SizeOf(Field("_explorerButton"));
            if (count == 0 && horizontal) expected = new(Math.Max(64 * dpi, (float)(196 * cardScale * dpi) - 8 * dpi), 36 * dpi);
            Check(Vector2.Distance(calendar.Size, expected) < .02f, $"Calendar/shortcut dimensions differ at {edge}, {dpi}, {count}: {calendar.Size}/{expected}");
            Check(calendar.Angle == (horizontal ? -4 : -7.5f), "Calendar tilt differs from shortcut");
            var actualTexture = (CardSwapChain)typeof(CalendarCardVisual).GetField("_surface", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(calendar)!;
            var expectedTexture = CalendarPresentation.DateTextureSize(new Size((int)Math.Ceiling(calendar.Size.X), (int)Math.Ceiling(calendar.Size.Y)));
            Check(actualTexture.Width == expectedTexture.Width && actualTexture.Height == expectedTexture.Height, "Composition did not use the refined date texture");
            var brush = (CompositionSurfaceBrush)typeof(CalendarCardVisual).GetField("_brush", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(calendar)!;
            Check(brush.Stretch == CompositionStretch.Fill, "Supersampled date must fit the unchanged card dimensions");
            Check(!CardInteraction.IsWindowCard(hit) && !renderer.CanExpandOnHover(hit), "Calendar entered window operations.");
            var center = new Point((int)hit.Polygon.Average(p => p.X) + (int)Math.Round(renderer.ScrollTranslationX),
                (int)hit.Polygon.Average(p => p.Y) + (int)Math.Round(renderer.ScrollTranslationY));
            Check(renderer.HitTest(center)?.IsCalendarCard == true, "Calendar pixel hit mismatch: " + edge);
            var ai = hits.Single(h => h.IsAiUsageCard);
            Check(horizontal ? hit.Polygon.Max(p => p.X) < ai.Polygon.Min(p => p.X) : hit.Polygon.Max(p => p.Y) < ai.Polygon.Min(p => p.Y), "Calendar must precede AI without overlap.");
            renderer.Activate(hit);
            Check(renderer.ConsumeCalendarClick() && !renderer.ConsumeCalendarClick() && !renderer.ConsumeAiUsageClickRequest(), "Calendar click leaked.");
            var revision = renderer.LayoutRevision;
            renderer.RefreshCalendarDate(); renderer.RefreshCalendarDate();
            Check(renderer.LayoutRevision == revision && hits.Count(t => t.IsCalendarCard) == 1, "Date refresh changed layout or left invisible completion hits.");
            calendar.SetHovered(true); calendar.SetHovered(false);
            Check(ReferenceEquals(actualTexture, typeof(CalendarCardVisual).GetField("_surface", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(calendar)), "Clock/hover must reuse the date texture");
            renderer.SetAiUsageCardEnabled(false);
            Check(hits.Count(t => t.IsCalendarCard) == 1 && !hits.Any(t => t.IsAiUsageCard), "Calendar depends on AI.");
            renderer.Scroll(-60); renderer.FinishScroll();
            hit = hits.Single(t => t.IsCalendarCard);
            center = new Point((int)hit.Polygon.Average(p => p.X) + (int)Math.Round(renderer.ScrollTranslationX),
                (int)hit.Polygon.Average(p => p.Y) + (int)Math.Round(renderer.ScrollTranslationY));
            if (renderer.InteractionBounds.Contains(center)) Check(renderer.HitTest(center)?.IsCalendarCard == true, "Scrolled date card click mismatch: " + edge);
        }
        renderer.SetCalendarCardEnabled(false);
        Check(!hits.Any(t => t.IsCalendarCard), "Disabled calendar retains clickable area.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
