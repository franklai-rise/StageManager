using System.Drawing;
using System.Reflection;
using System.IO;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using StageManager.Settings;
using Windows.UI.Composition;

internal static class SidebarDockTests
{
	public static void RunAll()
	{
		Exception? failure = null;
		var thread = new Thread(() => { try { CheckRenderer(); } catch (Exception e) { failure = e; } });
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		if (!thread.Join(TimeSpan.FromSeconds(120))) throw new Exception("Dock checks timed out.");
		if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
		var screen = new Rectangle(-1080, -200, 1080, 1920);
		foreach (var edge in Enum.GetValues<SidebarEdge>())
		{
			var band = SidebarDockLayout.Band(screen, edge, 180);
			var probe = edge switch
			{
				SidebarEdge.Left => new Point(screen.Left, screen.Top + 500),
				SidebarEdge.Right => new Point(screen.Right - 1, screen.Top + 500),
				SidebarEdge.Top => new Point(screen.Left + 500, screen.Top),
				_ => new Point(screen.Left + 500, screen.Bottom - 1)
			};
			Check(SidebarDockLayout.IsNearEdge(probe, screen, edge, 8), "Edge wake failed: " + edge);
			Check(!SidebarDockLayout.IsNearEdge(new(screen.Left + 500, screen.Top + 500), screen, edge, 8), "Interior woke sidebar.");
			var available = SidebarDockLayout.AvailableArea(screen, band, edge);
			var fitted = SidebarDockLayout.KeepOutside(screen, available, edge);
			Check(fitted == available && !fitted.IntersectsWith(band), "Focus overlapped dock: " + edge);
			var safe = new Rectangle(available.Left + 20, available.Top + 20, 200, 300);
			Check(SidebarDockLayout.KeepOutside(safe, available, edge) == safe, "Safe window was moved.");
		}
		Check(new AppSettings().SidebarEdge == SidebarEdge.Left, "Old settings default changed.");
		var folder = Path.Combine(Path.GetTempPath(), "StageManager-DockTests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			var path = Path.Combine(folder, "settings.json");
			File.WriteAllText(path, "{\"SchemaVersion\":19,\"SidebarVerticalOffset\":-50,\"AiUsageEnabled\":true}");
			var settings = new SettingsService(path);
			Check(settings.Current.SidebarEdge == SidebarEdge.Left && settings.Current.SidebarVerticalOffset == -50 && settings.Current.AiUsageEnabled, "Migration lost existing choices.");
			Check(File.Exists(path + ".pre-schema20.bak"), "No migration backup.");
			foreach (var edge in Enum.GetValues<SidebarEdge>())
			{
				var draft = settings.CloneCurrent(); draft.SidebarEdge = edge; settings.Apply(draft);
				Check(new SettingsService(path).Current.SidebarEdge == edge, "Dock edge did not persist.");
			}
			var invalid = settings.CloneCurrent(); invalid.SidebarEdge = (SidebarEdge)99; settings.Apply(invalid);
			Check(settings.Current.SidebarEdge == SidebarEdge.Left, "Invalid edge not normalized.");
		}
		finally { Directory.Delete(folder, true); }
	}

	private static void CheckRenderer()
	{
		using var dispatcher = new DispatcherQueueHelper();
		dispatcher.EnsureDispatcherQueue();
		using var compositor = new Compositor();
		CardPolishRegressionTests.Check(compositor);
		using var root = compositor.CreateContainerVisual();
		using var owner = new Form();
		using var renderer = new CompositionStageRenderer(owner, compositor, root, .6, false, true);
		renderer.SetSidebarVerticalOffset(0);
		renderer.SetNotificationAreaCardEnabled(true);
		renderer.SetAiUsageCardEnabled(true);
		renderer.Synchronize([new("test-group", "Test group", [new FakeWindow(40001, "test", "test.exe"), new FakeWindow(40002, "test", "test.exe")], DateTime.UtcNow)]);
		var hits = (List<CardHitTarget>)typeof(CompositionStageRenderer).GetField("_hitTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
		foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
		foreach (var size in new[] { new Size(1080, 1920), new Size(1920, 1080) })
		foreach (var edge in new[] { SidebarEdge.Left, SidebarEdge.Right })
		{
			renderer.CollapseExpandedStage(true);
			renderer.Resize(size.Width * dpi, size.Height * dpi, dpi);
			renderer.SetDockEdge(edge);
			if (edge != SidebarEdge.Top) Check(!renderer.CanScrollAt(new Point((int)(size.Width*dpi/2),(int)(size.Height*dpi/2))), "Non-top wheel area leaked into the screen center.");
			if (edge == SidebarEdge.Top)
			{
				var expectedHeight = 120f * dpi;
				foreach (var name in new[] { "_expandAllButton", "_explorerButton", "_collapseButton", "_sidebarPinButton", "_desktopButton", "_desktopIconsButton", "_aiUsageCard", "_notificationAreaCard" })
				{
					var visual = typeof(CompositionStageRenderer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
					var frameSize = (System.Numerics.Vector2)visual.GetType().GetProperty("Size")!.GetValue(visual)!;
					Check(Math.Abs(frameSize.Y - expectedHeight) < .1f && Math.Abs(frameSize.X - 196 * .6f * dpi) < .1f, "Top card size differs: " + name);
				}
				var ordered = hits.Where(h => h.IsPrimaryCard || h.IsExplorerButton || h.IsExpandAllButton || h.IsAiUsageCard || h.IsNotificationAreaCard || h.IsDesktopButton || h.IsDesktopIconsButton || h.IsSidebarCollapseButton || h.IsSidebarPinButton)
					.Select(Bounds).OrderBy(b => b.Left).ToArray();
				for (var i = 1; i < ordered.Length; i++)
					Check(ordered[i].Left - ordered[i-1].Right >= 12f * dpi, "Top cards lack safe perspective spacing.");
			}
			var primary = hits.Single(h => h.StageKey == "test-group" && h.IsPrimaryCard);
			renderer.Activate(primary);
			Check(hits.Count(h => h.StageKey == "test-group" && h.Window is not null) == 2, "Children missing: " + edge);
			var children = hits.Where(h => h.StageKey == "test-group" && h.Window is not null).ToArray();
			var horizontal = SidebarDockLayout.IsHorizontal(edge);
			var a = Bounds(children[0]); var b = Bounds(children[1]);
			Check(horizontal ? a.Right < b.Left || b.Right < a.Left : a.Bottom < b.Top || b.Bottom < a.Top, "Expanded children overlap: " + edge);
			foreach (var target in hits.Where(h => !h.IsPinButton).ToArray())
			{
				var center = new Point((int)target.Polygon.Average(p => p.X), (int)target.Polygon.Average(p => p.Y));
				Check(renderer.HitTest(center)?.StageKey == target.StageKey, "Hit mismatch: " + edge + " " + target.StageKey);
				// Cards must stay inside the cross-axis at every DPI, even when scrolling along the other axis.
				if (target.Window is not null)
					Check(horizontal ? center.Y >= 0 && center.Y < size.Height * dpi : center.X >= 0 && center.X < size.Width * dpi, "Cross-axis clipping: " + edge);
			}
			renderer.Scroll(-120);
			var explorer = hits.Single(h => h.IsExplorerButton);
			var moved = new Point((int)(explorer.Polygon.Average(p => p.X) + renderer.ScrollTranslationX), (int)(explorer.Polygon.Average(p => p.Y) + renderer.ScrollTranslationY));
			Check(renderer.HitTest(moved)?.IsExplorerButton == true, "Scrolled target mismatch: " + edge);
			renderer.SetSidebarVisible(false, false);
			Check(edge switch { SidebarEdge.Left => root.Offset.X < 0, SidebarEdge.Right => root.Offset.X > 0, SidebarEdge.Top => root.Offset.Y < 0, _ => root.Offset.Y > 0 }, "Wrong hide direction.");
			renderer.SetSidebarVisible(true, false);
			Check(root.Offset == System.Numerics.Vector3.Zero, "Reveal offset not restored.");
		}
		try { HorizontalCardRegressionTests.Check(renderer, hits); }
		catch (Exception error) { Console.Error.WriteLine(error); throw; }
	}
	private static RectangleF Bounds(CardHitTarget h) => RectangleF.FromLTRB(h.Polygon.Min(p => p.X), h.Polygon.Min(p => p.Y), h.Polygon.Max(p => p.X), h.Polygon.Max(p => p.Y));
	private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
