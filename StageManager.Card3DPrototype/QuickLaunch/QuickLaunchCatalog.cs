using StageManager.Settings;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StageManager.Card3DPrototype.QuickLaunch;

internal static class QuickLaunchCatalog
{
	private static readonly QuickLaunchStaWorkers DiscoveryWorkers = new(2, "StageManager app discovery");
	private static readonly QuickLaunchStaWorkers IconWorkers = new(2, "StageManager app icon");
	private static readonly QuickLaunchLauncher Launcher = new();
	public static QuickLaunchDefinition FromFile(string path)
	{
		path = Path.GetFullPath(path);
		var kind = Path.GetExtension(path).ToLowerInvariant() switch
		{
			".exe" => QuickLaunchTargetKind.Executable,
			".lnk" => QuickLaunchTargetKind.Shortcut,
			_ => throw new ArgumentException("Choose an EXE or LNK file.", nameof(path))
		};
		return Create(Path.GetFileNameWithoutExtension(path), kind, path);
	}

	public static QuickLaunchDefinition Create(string name, QuickLaunchTargetKind kind, string target) => new()
	{
		Id = "launcher:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{target.ToUpperInvariant()}"))),
		DisplayName = name,
		Kind = kind,
		Target = target
	};

	// No probing the target or its icon on the launch path. Shell owns shortcut arguments,
	// working directory and activation semantics. Never request the runas verb or retry elevated.
	internal static ProcessStartInfo CreateStartInfo(QuickLaunchDefinition app)
	{
		if (string.IsNullOrWhiteSpace(app.Target)) throw new ArgumentException("Empty launcher target.");
		var target = app.Target;
		switch (app.Kind)
		{
			case QuickLaunchTargetKind.Executable:
			case QuickLaunchTargetKind.Shortcut:
				var extension = app.Kind == QuickLaunchTargetKind.Executable ? ".exe" : ".lnk";
				if (!Path.IsPathFullyQualified(target) || !Path.GetExtension(target).Equals(extension, StringComparison.OrdinalIgnoreCase))
					throw new ArgumentException("Invalid file launcher target.");
				break;
			case QuickLaunchTargetKind.AppsFolder:
				if (target.Contains('"') || target.Any(char.IsControl)) throw new ArgumentException("Invalid AppsFolder target.");
				// Use a separate, quoted shell argument, not a command interpreter.
				return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
				{
					UseShellExecute = true,
					Arguments = "\"shell:AppsFolder\\" + target + "\""
				};
			case QuickLaunchTargetKind.LegacyBrowser:
				var browser = target switch
				{
					"chrome.exe" => QuickLaunchApp.Chrome,
					"msedge.exe" => QuickLaunchApp.Edge,
					_ => throw new ArgumentException("Unknown legacy browser.")
				};
				target = QuickLaunchAppResolver.ResolveExecutable(browser) ?? QuickLaunchAppResolver.CommandName(browser);
				break;
			default: throw new ArgumentException("Unknown launcher kind.");
		}
		return new ProcessStartInfo(target) { UseShellExecute = true };
	}

	public static bool Launch(QuickLaunchDefinition app)
		// Compatibility only. UI integrations must await LaunchAsync, never this wrapper.
		=> LaunchAsync(app).GetAwaiter().GetResult().Status == QuickLaunchStatus.Succeeded;

	public static Task<QuickLaunchResult> LaunchAsync(QuickLaunchDefinition app, CancellationToken token = default) =>
		Launcher.LaunchAsync(app, token);

	public static Task<IReadOnlyList<QuickLaunchDefinition>> DiscoverAsync(CancellationToken token) =>
		RunStaAsync<IReadOnlyList<QuickLaunchDefinition>>(() =>
		{
			var apps = new Dictionary<string, QuickLaunchDefinition>(StringComparer.OrdinalIgnoreCase);
			foreach (var root in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
			{
				var directory = Environment.GetFolderPath(root);
				if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;
				try
				{
					var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
					foreach (var path in Directory.EnumerateFiles(directory, "*.lnk", options))
					{
						token.ThrowIfCancellationRequested();
						var app = FromFile(path);
						apps.TryAdd(app.Id, app);
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
			}
			AddAppsFolder(apps, token);
			return apps.Values.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(app => app.Target).ToArray();
		}, token);

	private static void AddAppsFolder(Dictionary<string, QuickLaunchDefinition> apps, CancellationToken token)
	{
		object? shell = null, folder = null, items = null;
		try
		{
			var type = Type.GetTypeFromProgID("Shell.Application");
			if (type is null) return;
			shell = Activator.CreateInstance(type);
			if (shell is null) return;
			folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
			if (folder is null) return;
			items = ((dynamic)folder).Items();
			int count = ((dynamic)items).Count;
			// Do not use foreach on a COM collection (its hidden enumerator would leak).
			for (var index = 0; index < count; index++)
			{
				token.ThrowIfCancellationRequested();
				object? item = null;
				try
				{
					item = ((dynamic)items).Item(index);
					string name = ((dynamic)item).Name;
					string? id = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
					if (string.IsNullOrWhiteSpace(id)) continue;
					var app = Create(name, QuickLaunchTargetKind.AppsFolder, id);
					apps.TryAdd(app.Id, app);
				}
				catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
				finally { Release(item); }
			}
		}
		catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException) { }
		finally { Release(items); Release(folder); Release(shell); }
	}

	private static void Release(object? value)
	{
		if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
	}

	internal static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken token) => DiscoveryWorkers.RunAsync(action, token);
	internal static Task<T> RunIconStaAsync<T>(Func<T> action, CancellationToken token) => IconWorkers.RunAsync(action, token);
}

internal static class QuickLaunchIcons
{
	public static Task<Bitmap> LoadAsync(QuickLaunchDefinition app, CancellationToken token) =>
		QuickLaunchCatalog.RunIconStaAsync(() => Load(app), token);

	private static Bitmap Load(QuickLaunchDefinition app)
	{
		IntPtr pidl = IntPtr.Zero;
		var info = new ShellFileInfo();
		try
		{
			var target = app.Kind == QuickLaunchTargetKind.AppsFolder ? "shell:AppsFolder\\" + app.Target :
				app.Kind == QuickLaunchTargetKind.LegacyBrowser ? QuickLaunchCatalog.CreateStartInfo(app).FileName : app.Target;
			if (SHParseDisplayName(target, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
			{
				SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x8); // ICON | PIDL
				if (info.Icon != IntPtr.Zero)
				{
					using var icon = Icon.FromHandle(info.Icon);
					return icon.ToBitmap();
				}
			}
		}
		catch (Exception) { /* Icon failure never invalidates an otherwise launchable target. */ }
		finally
		{
			if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
			if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
		}
		return SystemIcons.Application.ToBitmap();
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
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes, out uint resultAttributes);
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref ShellFileInfo info, uint size, uint flags);
	[DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
