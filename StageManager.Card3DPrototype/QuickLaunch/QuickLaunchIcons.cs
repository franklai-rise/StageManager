using System.Runtime.InteropServices;
using StageManager.Settings;

namespace StageManager.Card3DPrototype.QuickLaunch;

internal static class QuickLaunchIcons
{
	private static readonly object FallbackGate = new();
	private static readonly object LogGate = new();

	// A placeholder is only a compatibility result. Requests need failures to remain
	// observable so a transient Shell error does not become a permanent cached icon.
	public static async Task<Bitmap> LoadAsync(QuickLaunchDefinition app, CancellationToken token)
	{
		try { return await LoadRequiredAsync(app, token).ConfigureAwait(false); }
		catch (OperationCanceledException) { throw; }
		catch (Exception) { return CreateFallback(); }
	}

	internal static async Task<Bitmap> LoadRequiredAsync(QuickLaunchDefinition app, CancellationToken token)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
		deadline.CancelAfter(TimeSpan.FromSeconds(2));
		try
		{
			return await QuickLaunchCatalog.RunIconStaAsync(() => LoadNative(app), deadline.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
			throw new TimeoutException("Application icon lookup exceeded two seconds.");
		}
	}

	internal static Bitmap CreateFallback()
	{
		lock (FallbackGate)
		{
			using var owned = (Icon)SystemIcons.Application.Clone();
			return owned.ToBitmap();
		}
	}

	private static Bitmap LoadNative(QuickLaunchDefinition app)
	{
		// SetApartmentState alone is not the initialization required by SHGetFileInfo.
		var initialized = CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED
		Marshal.ThrowExceptionForHR(initialized);
		try
		{
			var target = app.Kind == QuickLaunchTargetKind.AppsFolder ? "shell:AppsFolder\\" + app.Target :
				app.Kind == QuickLaunchTargetKind.LegacyBrowser ? QuickLaunchCatalog.CreateStartInfo(app).FileName : app.Target;
			// Executable resources avoid a stale Shell icon-cache entry. Shortcuts keep
			// their own icon location; packaged apps continue to use Shell identities.
			if (app.Kind is QuickLaunchTargetKind.Executable or QuickLaunchTargetKind.LegacyBrowser &&
				Path.IsPathFullyQualified(target))
			{
				var direct = TryExecutableIcon(target);
				if (direct is not null) return direct;
			}
			return ReadShellIcon(target);
		}
		finally { CoUninitialize(); }
	}

	private static Bitmap? TryExecutableIcon(string target)
	{
		IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
		try
		{
			var count = ExtractIconEx(target, 0, out large, out small, 1);
			if (count == 0 || count == uint.MaxValue) return null;
			var handle = large != IntPtr.Zero ? large : small;
			return handle == IntPtr.Zero ? null : CopyIconPixels(handle);
		}
		catch (Exception) { return null; } // Let Shell try a different icon provider.
		finally
		{
			if (large != IntPtr.Zero) DestroyIcon(large);
			if (small != IntPtr.Zero && small != large) DestroyIcon(small);
		}
	}

	private static Bitmap ReadShellIcon(string target)
	{
		IntPtr pidl = IntPtr.Zero;
		var info = new ShellFileInfo();
		try
		{
			Marshal.ThrowExceptionForHR(SHParseDisplayName(target, IntPtr.Zero, out pidl, 0, out _));
			if (pidl == IntPtr.Zero) throw new InvalidOperationException("Shell returned no application identity.");
			var result = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x8);
			if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
				throw new InvalidOperationException("Shell returned no application icon.");
			return CopyIconPixels(info.Icon);
		}
		finally
		{
			if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
			if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
		}
	}

	private static Bitmap CopyIconPixels(IntPtr handle)
	{
		using var borrowed = Icon.FromHandle(handle);
		using var owned = (Icon)borrowed.Clone();
		return owned.ToBitmap();
	}

	internal static void RecordFailure(QuickLaunchDefinition app, int attempt, Exception exception)
	{
		try
		{
			lock (LogGate)
			{
				var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
					"Stage_Manager_Lai", "3DRenderer");
				Directory.CreateDirectory(directory);
				var path = Path.Combine(directory, "launcher-icons.log");
				if (File.Exists(path) && new FileInfo(path).Length > 32 * 1024)
					File.Move(path, path + ".1", overwrite: true);
				// No document titles, full personal paths, or exception messages.
				File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} kind={app.Kind} attempt={attempt} " +
					$"error={exception.GetType().Name} hr=0x{exception.HResult:X8}{Environment.NewLine}");
			}
		}
		catch (Exception) { /* Diagnostics never block icon delivery or application launch. */ }
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ShellFileInfo
	{
		public IntPtr Icon;
		public int IconIndex;
		public uint Attributes;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
	}
	[DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
	[DllImport("ole32.dll")] private static extern void CoUninitialize();
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes, out uint resultAttributes);
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref ShellFileInfo info, uint size, uint flags);
	[DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
