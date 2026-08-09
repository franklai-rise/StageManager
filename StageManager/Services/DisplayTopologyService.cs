using StageManager.Model;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StageManager.Services;

public enum StageLayout
{
	Free,
	TwoColumns,
	ThreeColumns
}

public sealed class DisplayTopologyService
{
	public Screen LeftmostDisplay => Screen.AllScreens
		.OrderBy(screen => screen.Bounds.Left)
		.ThenBy(screen => screen.Bounds.Top)
		.First();

	public IReadOnlyList<Screen> Displays => Screen.AllScreens
		.OrderBy(screen => screen.Bounds.Left)
		.ThenBy(screen => screen.Bounds.Top)
		.ToArray();

	public WindowLayoutSnapshot Capture(IWindow window, int zOrder)
	{
		var location = window.Location;
		var rectangle = new Rectangle(location.X, location.Y, Math.Max(1, location.Width), Math.Max(1, location.Height));
		var display = Screen.FromRectangle(rectangle);
		var work = display.WorkingArea;
		return new WindowLayoutSnapshot(
			window.Handle,
			location.X,
			location.Y,
			location.Width,
			location.Height,
			location.State,
			display.DeviceName,
			work.Left,
			work.Top,
			work.Width,
			work.Height,
			zOrder);
	}

	public void Restore(IWindow window, WindowLayoutSnapshot snapshot)
	{
		if (!Win32.IsWindow(window.Handle))
			return;

		var displays = Displays;
		var target = displays.FirstOrDefault(screen => string.Equals(screen.DeviceName, snapshot.DisplayDeviceName, StringComparison.OrdinalIgnoreCase));
		var x = snapshot.X;
		var y = snapshot.Y;
		var width = Math.Max(160, snapshot.Width);
		var height = Math.Max(120, snapshot.Height);

		if (target is null)
		{
			target = FindNearestDisplay(snapshot.X + snapshot.Width / 2, snapshot.Y + snapshot.Height / 2);
			var oldWidth = Math.Max(1, snapshot.DisplayWorkWidth);
			var oldHeight = Math.Max(1, snapshot.DisplayWorkHeight);
			var relativeX = (snapshot.X - snapshot.DisplayWorkLeft) / (double)oldWidth;
			var relativeY = (snapshot.Y - snapshot.DisplayWorkTop) / (double)oldHeight;
			x = target.WorkingArea.Left + (int)Math.Round(relativeX * target.WorkingArea.Width);
			y = target.WorkingArea.Top + (int)Math.Round(relativeY * target.WorkingArea.Height);
		}

		var clamped = ClampToWorkArea(new Rectangle(x, y, width, height), target.WorkingArea);
		Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_RESTORE);
		Win32.SetWindowPos(
			window.Handle,
			IntPtr.Zero,
			clamped.Left,
			clamped.Top,
			clamped.Width,
			clamped.Height,
			Win32.SetWindowPosFlags.IgnoreZOrder |
			Win32.SetWindowPosFlags.DoNotActivate |
			Win32.SetWindowPosFlags.AsynchronousWindowPosition);

		if (snapshot.State == WindowState.Maximized)
			Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_SHOWMAXIMIZED);
	}

	public void MoveToNextDisplay(IEnumerable<IWindow> windows)
	{
		var displays = Displays;
		if (displays.Count < 2)
			return;

		foreach (var window in windows.Where(window => Win32.IsWindow(window.Handle)))
		{
			var location = window.Location;
			var source = Screen.FromRectangle(new Rectangle(location.X, location.Y, Math.Max(1, location.Width), Math.Max(1, location.Height)));
			var sourceIndex = displays.ToList().FindIndex(screen => string.Equals(screen.DeviceName, source.DeviceName, StringComparison.OrdinalIgnoreCase));
			var target = displays[(Math.Max(0, sourceIndex) + 1) % displays.Count];
			var relativeX = (location.X - source.WorkingArea.Left) / (double)Math.Max(1, source.WorkingArea.Width);
			var relativeY = (location.Y - source.WorkingArea.Top) / (double)Math.Max(1, source.WorkingArea.Height);
			var targetRect = ClampToWorkArea(new Rectangle(
				target.WorkingArea.Left + (int)Math.Round(relativeX * target.WorkingArea.Width),
				target.WorkingArea.Top + (int)Math.Round(relativeY * target.WorkingArea.Height),
				Math.Min(location.Width, target.WorkingArea.Width),
				Math.Min(location.Height, target.WorkingArea.Height)), target.WorkingArea);

			Win32.ShowWindowAsync(window.Handle, Win32.SW.SW_RESTORE);
			Win32.SetWindowPos(window.Handle, IntPtr.Zero, targetRect.Left, targetRect.Top, targetRect.Width, targetRect.Height,
				Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.AsynchronousWindowPosition);
		}
	}

	public void Arrange(IEnumerable<IWindow> windows, StageLayout layout)
	{
		var candidates = windows.Where(window => Win32.IsWindow(window.Handle)).Take(layout == StageLayout.ThreeColumns ? 3 : 2).ToArray();
		if (layout == StageLayout.Free || candidates.Length == 0)
			return;

		var active = candidates.FirstOrDefault(window => window.IsFocused) ?? candidates[0];
		var location = active.Location;
		var display = Screen.FromRectangle(new Rectangle(location.X, location.Y, Math.Max(1, location.Width), Math.Max(1, location.Height)));
		var work = display.WorkingArea;
		var columns = layout == StageLayout.ThreeColumns ? 3 : 2;
		var columnWidth = work.Width / columns;

		for (var index = 0; index < candidates.Length; index++)
		{
			var width = index == columns - 1 ? work.Right - (work.Left + columnWidth * index) : columnWidth;
			Win32.ShowWindowAsync(candidates[index].Handle, Win32.SW.SW_RESTORE);
			Win32.SetWindowPos(candidates[index].Handle, IntPtr.Zero, work.Left + columnWidth * index, work.Top, width, work.Height,
				Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.AsynchronousWindowPosition);
		}
	}

	private Screen FindNearestDisplay(int x, int y)
	{
		return Displays.OrderBy(screen =>
		{
			var centerX = screen.WorkingArea.Left + screen.WorkingArea.Width / 2;
			var centerY = screen.WorkingArea.Top + screen.WorkingArea.Height / 2;
			return Math.Pow(centerX - x, 2) + Math.Pow(centerY - y, 2);
		}).First();
	}

	private static Rectangle ClampToWorkArea(Rectangle rectangle, Rectangle workArea)
	{
		var width = Math.Min(Math.Max(160, rectangle.Width), workArea.Width);
		var height = Math.Min(Math.Max(120, rectangle.Height), workArea.Height);
		var x = Math.Clamp(rectangle.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
		var y = Math.Clamp(rectangle.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
		return new Rectangle(x, y, width, height);
	}
}
