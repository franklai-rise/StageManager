global using System;
using System.Drawing;
using StageManager.Card3DPrototype;

// No form/renderer or test framework required. RunAll can be wired into the main
// runner. For isolated compilation, include only this file and the two production
// files and define HORIZONTAL_EDGE_SCROLL_TESTS to enable the standalone Main.
internal static class HorizontalEdgeScrollTests
{
#if HORIZONTAL_EDGE_SCROLL_TESTS
	public static void Main()
	{
		RunAll();
		Console.WriteLine("HorizontalEdgeScrollTests: all checks passed.");
	}
#endif

	public static void RunAll()
	{
		ZonesAndDpi();
		StopsAndWheelPriority();
		BoundedMotion();
	}

	private static void ZonesAndDpi()
	{
		var controller = new HorizontalEdgeScrollController(() => 0);
		var range = new SidebarScrollRange(-40, 260);
		foreach (var dpi in new[] { 0.75f, 1f, 1.25f, 1.5f, 2f, 3f })
		{
			var band = new RectangleF(-200 * dpi, 300 * dpi, 400 * dpi, 60 * dpi);
			float At(float x, float y = 330) => controller.Update(new(x * dpi, y * dpi), band, dpi, range, true) / dpi;
			Near(At(-200), -480, "Left outer edge");
			Near(At(-188), -290, "Left proportional midpoint");
			Near(At(-176), -100, "Left inner boundary");
			Near(At(176), 100, "Right inner boundary");
			Near(At(188), 290, "Right proportional midpoint");
			Near(At(199.99f), 479.8417f, "Right outer edge", .01f);
			foreach (var x in new[] { -201f, -175f, 0f, 175f, 200f, 201f })
				Near(At(x), 0, "Outside activation zone");
			Near(At(-199, 299), 0, "Above visible band");
			Near(At(-199, 360), 0, "Below visible band");
		}
		Near(controller.Update(new(10, 5), new(0, 0, 20, 10), 1, range, true), 0, "Narrow midpoint");
		Check(controller.Update(new(9, 5), new(0, 0, 20, 10), 1, range, true) < 0, "Narrow left");
		Check(controller.Update(new(11, 5), new(0, 0, 20, 10), 1, range, true) > 0, "Narrow right");
	}

	private static void StopsAndWheelPriority()
	{
		double now = 10;
		var controller = new HorizontalEdgeScrollController(() => now);
		var band = new RectangleF(0, 0, 400, 60);
		var range = new SidebarScrollRange(0, 300);
		float Update(bool enabled = true) => controller.Update(new(1, 30), band, 1, range, enabled);
		foreach (var reason in new[] { "blocked", "button", "menu", "AI", "hidden", "vertical dock" })
		{
			Check(Update() < 0, "Activation before " + reason);
			Near(Update(false), 0, "Immediate disable: " + reason);
		}
		Update();
		controller.Stop();
		Near(controller.Velocity, 0, "Explicit stop");
		foreach (var invalidRange in new[] { new SidebarScrollRange(0, 0), new SidebarScrollRange(5, 5), new SidebarScrollRange(2, 1), new SidebarScrollRange(0, float.NaN) })
			Near(controller.Update(new(1, 30), band, 1, invalidRange, true), 0, "No overflow / invalid range");
		foreach (var dpi in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
			Near(controller.Update(new(1, 30), band, dpi, range, true), 0, "Invalid DPI");
		Near(controller.Update(new(1, 30), RectangleF.Empty, 1, range, true), 0, "Empty band");
		Near(controller.Update(new(float.NaN, 30), band, 1, range, true), 0, "Invalid pointer");
		Update();
		controller.SuppressForWheel();
		Near(controller.Velocity, 0, "Wheel immediately stops edge input");
		Check(controller.IsWheelSuppressed, "Suppression flag");
		now = 10.249;
		Near(Update(), 0, "Within cooldown");
		controller.Stop();
		Near(Update(), 0, "Stop preserves cooldown");
		now = 10.250;
		Check(Update() < 0 && !controller.IsWheelSuppressed, "Cooldown exact expiry");
		controller.SuppressForWheel();
		now = 10.4;
		controller.SuppressForWheel();
		now = 10.6;
		Near(Update(), 0, "Repeated wheel extends priority");
		now = 10.651;
		Check(Update() < 0, "Resumes after last wheel");
	}

	private static void BoundedMotion()
	{
		var motion = new SidebarScrollMotion();
		motion.SetRange(new(-100, 500), 1);
		var actual = new SidebarScrollRange(-20, 200);
		motion.AddWheel(-120, 80, true);
		motion.Advance(.016);
		var before = motion.Position;
		Near(motion.MoveBounded(5, actual), 5, "Applied continuous delta");
		Near(motion.Position, before + 5, "Movement starts at current not target");
		Near(motion.Target, motion.Position, "Target synchronized");
		Check(!motion.IsMoving, "Spring velocity cleared");
		before = motion.Position;
		motion.Advance(.1);
		Near(motion.Position, before, "No residual drift");
		for (var i = 0; i < 100; i++) motion.MoveBounded(10, actual);
		Near(motion.Position, 200, "Actual maximum, not wheel maximum");
		Near(motion.MoveBounded(10, actual), 0, "Blocked at maximum");
		motion.MoveBounded(-1000, actual);
		Near(motion.Position, -20, "Actual minimum, not zero");
		Near(motion.MoveBounded(-10, actual), 0, "Blocked at minimum");
		motion.MoveBounded(1, actual);
		Near(motion.Position, -19, "Immediate reversal");
		motion.MoveBounded(0, new(15, 15));
		Near(motion.Position, 15, "Collapsed range clamps current");
		motion.AddWheel(-120, 80, true);
		motion.Advance(.01);
		before = motion.Position;
		motion.FreezeAtCurrent();
		motion.Advance(.1);
		Near(motion.Position, before, "Freeze does not jump to old target");
		Check(!motion.IsMoving, "Freeze clears spring");
		motion.AddWheel(-120, 1000, false);
		Near(motion.Position, 500, "Legacy wheel range retained");
		motion.AddWheel(-60, 80, true);
		Check(motion.Target > 500 && motion.Target < 528, "Legacy fractional wheel overscroll retained");
		motion.MoveBounded(0, actual);
		Near(motion.Position, 200, "Edge takeover clamps wheel overscroll");
		Check(!motion.IsMoving, "Takeover clears spring");
		var controller = new HorizontalEdgeScrollController(() => 0);
		for (var i = 0; i < 120; i++)
			motion.MoveBounded(controller.Update(new(0, 20), new(0, 0, 400, 60), 1, actual, true) / 60, actual);
		Near(motion.Position, -20, "Continuous ticks clamp actual lower bound");
	}

	private static void Near(float actual, float expected, string message, float tolerance = .001f)
		=> Check(float.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{message}: expected {expected}, got {actual}");
	private static void Check(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}
}
