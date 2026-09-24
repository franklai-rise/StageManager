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

	public static void FailedRestoreRetainsOnlyPendingWindows()
	{
		using var first = StartProbe("normal");
		using var second = StartProbe("normal");
		try
		{
			var firstHandle = WaitForWindow(first);
			var secondHandle = WaitForWindow(second);
			var failSecond = true;
			var firstRestores = 0;
			var secondRestores = 0;
			bool Restore(IntPtr handle, ref NativeWindowPlacement placement)
			{
				if (handle == firstHandle) firstRestores++;
				else { secondRestores++; if (failSecond) return false; }
				var placed = NativeMethods.SetWindowPlacement(handle, ref placement);
				var shown = NativeMethods.ShowWindowAsync(handle, placement.ShowCommand);
				return placed && shown;
			}
			var service = new DesktopToggleService(() => new[] { firstHandle, secondHandle }, Restore);
			Check(service.TryToggle(out _), "Could not start the test-owned restore session.");
			WaitUntil(() => NativeMethods.IsIconic(firstHandle) && NativeMethods.IsIconic(secondHandle), "Probe minimization did not settle.");
			Check(!service.TryRestore(out var error) && service.IsDesktopShown &&
				error is not null && error.Contains("PID", StringComparison.Ordinal),
				"A partial failure was incorrectly reported as a completed restore.");
			WaitUntil(() => !NativeMethods.IsIconic(firstHandle), "The successful probe did not restore.");
			Check(NativeMethods.IsIconic(secondHandle), "The failing restore unexpectedly changed the second probe.");
			failSecond = false;
			Check(service.TryRestore(out error) && error is null && !service.IsDesktopShown && firstRestores == 1 && secondRestores == 2,
				"Retry lost a pending window or reprocessed a successfully restored window.");
			WaitUntil(() => !NativeMethods.IsIconic(secondHandle), "The retained probe did not restore on retry.");
		}
		finally { CloseProbe(first); CloseProbe(second); }
	}

	public static void AlreadyRestoredWindowDoesNotCauseFailure()
	{
		using var probe = StartProbe("normal");
		try
		{
			var handle = WaitForWindow(probe);
			var restoreCalls = 0;
			bool RejectRestore(IntPtr _, ref NativeWindowPlacement placement)
			{
				restoreCalls++;
				return false;
			}
			var service = new DesktopToggleService(() => new[] { handle }, RejectRestore);
			Check(service.TryToggle(out _), "Could not start the already-restored window test.");
			WaitUntil(() => NativeMethods.IsIconic(handle), "Probe minimization did not settle.");
			NativeMethods.ShowWindowAsync(handle, NativeMethods.SwRestore);
			WaitUntil(() => NativeMethods.IsWindowVisible(handle) && !NativeMethods.IsIconic(handle),
				"The probe did not restore independently.");
			Check(service.TryRestore(out var error) && error is null && !service.IsDesktopShown && restoreCalls == 0,
				"An already restored window was treated as a failed desktop command.");
		}
		finally { CloseProbe(probe); }
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
