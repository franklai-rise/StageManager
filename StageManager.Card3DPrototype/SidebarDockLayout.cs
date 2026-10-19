using StageManager.Settings;
using System.Numerics;

namespace StageManager.Card3DPrototype;

internal static class SidebarDockLayout
{
	public static bool IsHorizontal(SidebarEdge edge) => edge is SidebarEdge.Top or SidebarEdge.Bottom;

	public static Vector3 Position(SidebarEdge edge, float along, Vector2 size, Vector2 viewport, float inset)
		=> edge switch
		{
			SidebarEdge.Right => new(viewport.X - size.X - inset, along, 0),
			SidebarEdge.Top => new(along, inset, 0),
			SidebarEdge.Bottom => new(along, viewport.Y - size.Y - inset, 0),
			_ => new(inset, along, 0)
		};

	public static bool IsNearEdge(Point p, Rectangle area, SidebarEdge edge, int width)
	{
		if (!area.Contains(p)) return false;
		width = Math.Max(1, width);
		return edge switch
		{
			SidebarEdge.Right => p.X >= area.Right - width,
			SidebarEdge.Top => p.Y <= area.Top + width,
			SidebarEdge.Bottom => p.Y >= area.Bottom - width,
			_ => p.X <= area.Left + width
		};
	}

	public static Rectangle Band(Rectangle area, SidebarEdge edge, int thickness)
	{
		thickness = Math.Clamp(thickness, 1, IsHorizontal(edge) ? area.Height : area.Width);
		return edge switch
		{
			SidebarEdge.Right => new(area.Right - thickness, area.Top, thickness, area.Height),
			SidebarEdge.Top => new(area.Left, area.Top, area.Width, thickness),
			SidebarEdge.Bottom => new(area.Left, area.Bottom - thickness, area.Width, thickness),
			_ => new(area.Left, area.Top, thickness, area.Height)
		};
	}

	public static Rectangle AvailableArea(Rectangle work, Rectangle reserved, SidebarEdge edge) => edge switch
	{
		SidebarEdge.Right => Rectangle.FromLTRB(work.Left, work.Top, Math.Min(work.Right, reserved.Left), work.Bottom),
		SidebarEdge.Top => Rectangle.FromLTRB(work.Left, Math.Max(work.Top, reserved.Bottom), work.Right, work.Bottom),
		SidebarEdge.Bottom => Rectangle.FromLTRB(work.Left, work.Top, work.Right, Math.Min(work.Bottom, reserved.Top)),
		_ => Rectangle.FromLTRB(Math.Max(work.Left, reserved.Right), work.Top, work.Right, work.Bottom)
	};

	public static Rectangle KeepOutside(Rectangle window, Rectangle available, SidebarEdge edge)
	{
		if (available.Width <= 0 || available.Height <= 0 || window.Width <= 0 || window.Height <= 0) return window;
		if (edge == SidebarEdge.Left && window.Left < available.Left)
			return new(available.Left, window.Top, Math.Min(window.Width, available.Width), window.Height);
		if (edge == SidebarEdge.Right && window.Right > available.Right)
		{
			var width = Math.Min(window.Width, available.Width);
			return new(available.Right - width, window.Top, width, window.Height);
		}
		if (edge == SidebarEdge.Top && window.Top < available.Top)
			return new(window.Left, available.Top, window.Width, Math.Min(window.Height, available.Height));
		if (edge == SidebarEdge.Bottom && window.Bottom > available.Bottom)
		{
			var height = Math.Min(window.Height, available.Height);
			return new(window.Left, available.Bottom - height, window.Width, height);
		}
		return window;
	}
}
