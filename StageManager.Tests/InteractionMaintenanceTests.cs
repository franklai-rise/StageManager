using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using StageManager.Card3DPrototype.NotificationArea;
using StageManager.Native.Window;
using Windows.UI.Composition;

internal static class InteractionMaintenanceTests
{
	public static int RunNotificationWorker(string[] args)
	{
		var result = 1;
		OnSta(() => result = NotificationAreaWorker.Run(args));
		return result;
	}

	public static void VerifyLiveActivation() => OnSta(() =>
	{
		using var form = new PrototypeForm();
		_ = form.Handle;
		SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
		var method = typeof(PrototypeForm).GetMethod("ActivateSelectedWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
		var pending = typeof(PrototypeForm).GetField("_activationVerification", BindingFlags.NonPublic | BindingFlags.Instance)!;
		foreach (var name in new[] { "Clash for Windows", "VpnManager" })
		{
			using var process = System.Diagnostics.Process.GetProcessesByName(name).First(p => p.MainWindowHandle != IntPtr.Zero);
			var window = new StageManager.Native.WindowsWindow(process.MainWindowHandle);
			method.Invoke(form, [window, false, WindowClickAction.ActivatePreservingPlacement]);
			var until = DateTime.UtcNow.AddSeconds(12);
			do { Application.DoEvents(); Thread.Sleep(15); }
			while (pending.GetValue(form) is not null && DateTime.UtcNow < until);
			var foreground = StageManager.Native.PInvoke.Win32Helper.IsForegroundForWindow(window.Handle);
			Console.WriteLine($"LIVE {name}: processIdentity={window.ProcessName}, foreground={foreground}, minimized={window.IsMinimized}");
			Check(foreground && !window.IsMinimized, $"Live activation did not restore {name} to the foreground.");
			method.Invoke(form, [window, true, WindowClickAction.Minimize]);
			until = DateTime.UtcNow.AddSeconds(2);
			while (!window.IsMinimized && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); }
			Check(window.IsMinimized, $"Second card click failed to minimize {name}.");
			method.Invoke(form, [window, true, WindowClickAction.RestoreAndActivate]);
			until = DateTime.UtcNow.AddSeconds(12);
			do { Application.DoEvents(); Thread.Sleep(15); }
			while (pending.GetValue(form) is not null && DateTime.UtcNow < until);
			Check(StageManager.Native.PInvoke.Win32Helper.IsForegroundForWindow(window.Handle),
				$"Third card click failed to restore {name}.");
			Console.WriteLine($"LIVE {name}: restore -> minimize -> restore PASS");
		}
	});

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	private static void OnSta(Action body)
	{
		Exception? error = null;
		var thread = new Thread(() => { try { body(); } catch (Exception exception) { error = exception; } });
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Check(thread.Join(TimeSpan.FromSeconds(20)), "Interaction check timed out.");
		if (error is not null) throw new InvalidOperationException("Interaction regression failed.", error);
	}

	public static void HoverExpansionStartsImmediatelyAndCascades() => OnSta(() =>
	{
		using var dispatcher = new DispatcherQueueHelper();
		dispatcher.EnsureDispatcherQueue();
		using var compositor = new Compositor();
		using var root = compositor.CreateContainerVisual();
		using var owner = new Form();
		using var renderer = new CompositionStageRenderer(owner, compositor, root, 0.8, true, true);
		renderer.Resize(900, 1500, 1);
		renderer.Synchronize([new PrototypeStageSnapshot("cascade", "Cascade",
			Enumerable.Range(1, 5).Select(index => (IWindow)new FakeWindow(71000 + index, "Cascade", "cascade.exe")).ToArray(),
			DateTime.UtcNow)]);
		var targets = (List<CardHitTarget>)typeof(CompositionStageRenderer)
			.GetField("_hitTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
		var primary = targets.Single(target => target.StageKey == "cascade" && target.IsPrimaryCard);
		var center = new Point((int)primary.Polygon.Average(point => point.X), (int)primary.Polygon.Average(point => point.Y));
		Check(renderer.TryExpandHoveredPrimaryCard(center, "cascade") && renderer.IsStageExpanded("cascade"),
			"Hover contact did not expand in the same input turn.");

		var stages = (Dictionary<string, StageCardVisual>)typeof(CompositionStageRenderer)
			.GetField("_stages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
		var visible = stages["cascade"].Windows.Where(card => card.IsVisible).ToArray();
		Check(visible.Length == 5, "The first expanded page changed its existing size.");
		for (var index = 0; index < visible.Length; index++)
		{
			Check(visible[index].Motion.AnimationDelay == TimeSpan.FromMilliseconds(index * 45),
				"Child cards did not receive increasing cascade delays.");
			Check(visible[index].Motion.AnimationDuration == TimeSpan.FromMilliseconds(300),
				"Cascade movement duration changed unexpectedly.");
		}
		var lastTarget = targets.Single(target => target.Window?.Handle == visible[^1].Window.Handle);
		Check(lastTarget.Projection?.IsInteractive == false,
			"A delayed child became clickable before it started flowing out.");
		var currentPolygon = lastTarget.Projection!.CurrentPolygon();
		var currentCenter = new Point((int)currentPolygon.Average(point => point.X), (int)currentPolygon.Average(point => point.Y));
		Check(renderer.HitTest(currentCenter)?.Window?.Handle != visible[^1].Window.Handle,
			"Hit testing selected a child whose delayed animation had not started.");
		// Native window regions must retain the connector even after motion ends;
		// card polygons alone leave holes between the children and clip the line.
		renderer.SetAnimationsEnabled(false);
		using var region = new Region();
		region.MakeEmpty();
		foreach (var polygon in renderer.GetInteractivePolygons(false))
		{
			using var path = new System.Drawing.Drawing2D.GraphicsPath();
			path.AddPolygon(polygon);
			region.Union(path);
		}
		var stage = stages["cascade"];
		var connector = stage.ExpandedConnector;
		var line = (SpriteVisual)typeof(ExpandedConnectorVisual).GetField("_verticalLine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connector)!;
		for (var sample = 1; sample < 20; sample++)
		{
			var position = connector.Root.Offset + line.Offset + new Vector3(line.Size.X / 2, line.Size.Y * sample / 20, 0);
			var point = Card3DGeometry.ProjectCard(stage.Motion.Target.Offset, stage.Motion.Target.Scale.X,
				position, Vector3.One, 0, Vector2.One, Vector2.Zero, new Vector2(450, 750), 1200)[0];
			Check(region.IsVisible(point.X, point.Y), "The expanded connector is clipped by the native window region.");
		}
	});

	public static void NotificationIconMatchingIsSpecific()
	{
		var icons = new[]
		{
			new NotificationIconActivation(1, "OneDrive - Up to date"),
			new NotificationIconActivation(2, "Clash for Windows"),
			new NotificationIconActivation(3, "VPN 管理器")
		};
		Check(NotificationIconMatcher.FindBest(icons, ["Clash for Windows.exe", "Clash for Windows"])?.Ordinal == 2,
			"Clash did not resolve to its exact native notification icon.");
		Check(NotificationIconMatcher.FindBest(icons, ["VPN 管理器 · 1.1.0.0", "VpnManager"])?.Ordinal == 3,
			"The localized VPN Manager title did not resolve to its icon.");
		var detailedIcons = new[]
		{
			new NotificationIconActivation(4, "VPN 管理器 美国 加利福尼亚州 洛杉矶 · 规则分流 HTTP/SOCKS5 :7890 · Clash")
		};
		Check(NotificationIconMatcher.FindBest(detailedIcons, ["VPN 管理器 · 1.1.1.0", "VpnManager"])?.Ordinal == 4,
			"A versioned VPN Manager title did not match its detailed notification status.");
		Check(NotificationIconMatcher.FindBest(icons, ["Unrelated application"]) is null,
			"An unrelated application was matched to the wrong tray icon.");
		Check(NotificationIconMatcher.FindBest([new NotificationIconActivation(1, "")], ["Clash"]) is null,
			"An empty notification name matched an application.");
	}
}
