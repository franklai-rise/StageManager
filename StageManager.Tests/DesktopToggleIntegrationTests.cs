using System.Diagnostics;
using StageManager.Card3DPrototype;

internal static class DesktopToggleIntegrationTests
{
	public static void RestoresNativeWindows()
	{
		using var normal = StartProbe("normal");
		using var maximized = StartProbe("maximized");
		try
		{
			var normalHandle = WaitForWindow(normal);
			var maximizedHandle = WaitForWindow(maximized);
			NativeMethods.ShowWindowAsync(maximizedHandle, NativeMethods.SwShowMaximized);
			WaitUntil(() => NativeMethods.IsZoomed(maximizedHandle), "The maximized probe did not maximize.");
			var service = new DesktopToggleService(() => new[] { normalHandle, maximizedHandle });

			Check(service.TryToggle(out var minimizeError) && minimizeError is null && service.IsDesktopShown,
				"The native desktop session did not start.");
			WaitUntil(() => NativeMethods.IsIconic(normalHandle) && NativeMethods.IsIconic(maximizedHandle),
				"The probe windows were not minimized.");
			// Tray-oriented programs can hide themselves while processing minimize.
			NativeMethods.ShowWindowAsync(normalHandle, NativeMethods.SwHide);
			WaitUntil(() => !NativeMethods.IsWindowVisible(normalHandle), "The hidden-window case was not established.");

			Check(service.TryToggle(out var restoreError) && restoreError is null && !service.IsDesktopShown,
				"The native desktop session did not complete.");
			WaitUntil(() => NativeMethods.IsWindowVisible(normalHandle) && !NativeMethods.IsIconic(normalHandle),
				"A hidden normal window was not restored.");
			WaitUntil(() => NativeMethods.IsWindowVisible(maximizedHandle) &&
				!NativeMethods.IsIconic(maximizedHandle) && NativeMethods.IsZoomed(maximizedHandle),
				"A previously maximized window did not return maximized.");
			Check(!NativeMethods.IsZoomed(normalHandle), "A normal window was incorrectly maximized during restore.");
		}
		finally
		{
			CloseProbe(normal);
			CloseProbe(maximized);
		}
	}

	private static Process StartProbe(string mode)
	{
		var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable.");
		return Process.Start(new ProcessStartInfo
		{
			FileName = executable,
			ArgumentList = { "--desktop-toggle-probe", mode },
			UseShellExecute = false
		}) ?? throw new InvalidOperationException($"Could not start the {mode} probe.");
	}

	private static IntPtr WaitForWindow(Process process)
	{
		try { process.WaitForInputIdle(5000); } catch (InvalidOperationException) { }
		IntPtr handle = IntPtr.Zero;
		WaitUntil(() =>
		{
			process.Refresh();
			handle = process.MainWindowHandle;
			return handle != IntPtr.Zero && NativeMethods.IsWindow(handle);
		}, "A probe window did not open.");
		return handle;
	}

	private static void WaitUntil(Func<bool> condition, string message)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(5))
		{
			if (condition()) return;
			Thread.Sleep(25);
		}
		throw new InvalidOperationException(message);
	}

	private static void CloseProbe(Process process)
	{
		if (process.HasExited) return;
		process.CloseMainWindow();
		if (!process.WaitForExit(3000))
		{
			process.Kill(true);
			process.WaitForExit(3000);
		}
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
