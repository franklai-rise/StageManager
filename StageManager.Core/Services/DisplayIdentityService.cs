using StageManager.Infrastructure;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StageManager.Services;

public sealed record DisplayIdentity(
	string GdiDeviceName,
	string StableId,
	string FriendlyName,
	bool IsPrimary,
	Rectangle Bounds,
	Rectangle WorkingArea);

/// <summary>
/// Maps the transient GDI display name used by Screen to the monitor device
/// path exposed by QueryDisplayConfig. The latter normally survives display
/// reordering and primary-screen changes.
/// </summary>
public sealed class DisplayIdentityService
{
	private const uint QdcOnlyActivePaths = 0x00000002;
	private const int ErrorInsufficientBuffer = 122;
	private const int MaximumAttempts = 3;

	public IReadOnlyList<DisplayIdentity> GetActiveDisplays()
	{
		var targets = QueryActiveTargets();
		return Screen.AllScreens.Select(screen =>
		{
			targets.TryGetValue(screen.DeviceName, out var target);
			var stableId = target?.StableId ?? NormalizeStableId(screen.DeviceName);
			var friendlyName = string.IsNullOrWhiteSpace(target?.FriendlyName)
				? screen.DeviceName
				: target.FriendlyName;
			return new DisplayIdentity(
				screen.DeviceName,
				stableId,
				friendlyName,
				screen.Primary,
				screen.Bounds,
				screen.WorkingArea);
		}).ToArray();
	}

	public string GetStableId(Screen screen)
	{
		ArgumentNullException.ThrowIfNull(screen);
		return GetActiveDisplays()
			.FirstOrDefault(display => string.Equals(
				display.GdiDeviceName,
				screen.DeviceName,
				StringComparison.OrdinalIgnoreCase))?
			.StableId ?? NormalizeStableId(screen.DeviceName);
	}

	public static string NormalizeStableId(string value) =>
		value.Trim().TrimEnd('\0').Replace('/', '\\').ToUpperInvariant();

	private static Dictionary<string, TargetIdentity> QueryActiveTargets()
	{
		try
		{
			for (var attempt = 0; attempt < MaximumAttempts; attempt++)
			{
				var result = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
				if (result != 0)
					break;

				var paths = new DisplayConfigPathInfo[pathCount];
				var modes = new DisplayConfigModeInfo[modeCount];
				result = QueryDisplayConfig(
					QdcOnlyActivePaths,
					ref pathCount,
					paths,
					ref modeCount,
					modes,
					IntPtr.Zero);
				if (result == ErrorInsufficientBuffer)
					continue;
				if (result != 0)
					break;

				var identities = new Dictionary<string, TargetIdentity>(StringComparer.OrdinalIgnoreCase);
				foreach (var path in paths.Take((int)pathCount))
				{
					var source = new DisplayConfigSourceDeviceName
					{
						Header = DisplayConfigDeviceInfoHeader.Create(
							DisplayConfigDeviceInfoType.GetSourceName,
							path.SourceInfo.AdapterId,
							path.SourceInfo.Id,
							Marshal.SizeOf<DisplayConfigSourceDeviceName>())
					};
					if (DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrWhiteSpace(source.ViewGdiDeviceName))
						continue;

					var target = new DisplayConfigTargetDeviceName
					{
						Header = DisplayConfigDeviceInfoHeader.Create(
							DisplayConfigDeviceInfoType.GetTargetName,
							path.TargetInfo.AdapterId,
							path.TargetInfo.Id,
							Marshal.SizeOf<DisplayConfigTargetDeviceName>())
					};
					var targetResult = DisplayConfigGetDeviceInfo(ref target);
					var monitorPath = targetResult == 0 ? target.MonitorDevicePath : string.Empty;
					var fallbackId = $"LUID:{path.TargetInfo.AdapterId.HighPart:X8}{path.TargetInfo.AdapterId.LowPart:X8}:TARGET:{path.TargetInfo.Id:X8}";
					identities[source.ViewGdiDeviceName] = new TargetIdentity(
						string.IsNullOrWhiteSpace(monitorPath) ? fallbackId : NormalizeStableId(monitorPath),
						targetResult == 0 ? target.MonitorFriendlyDeviceName.TrimEnd('\0') : string.Empty);
				}

				return identities;
			}
		}
		catch (Exception exception)
		{
			AppLogger.Warn($"Stable display identities are unavailable: {exception.Message}");
		}

		return new Dictionary<string, TargetIdentity>(StringComparer.OrdinalIgnoreCase);
	}

	private sealed record TargetIdentity(string StableId, string FriendlyName);

	private enum DisplayConfigDeviceInfoType : uint
	{
		GetSourceName = 1,
		GetTargetName = 2
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Luid
	{
		public uint LowPart;
		public int HighPart;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DisplayConfigPathSourceInfo
	{
		public Luid AdapterId;
		public uint Id;
		public uint ModeInfoIdx;
		public uint StatusFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DisplayConfigRational
	{
		public uint Numerator;
		public uint Denominator;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DisplayConfigPathTargetInfo
	{
		public Luid AdapterId;
		public uint Id;
		public uint ModeInfoIdx;
		public uint OutputTechnology;
		public uint Rotation;
		public uint Scaling;
		public DisplayConfigRational RefreshRate;
		public uint ScanLineOrdering;
		[MarshalAs(UnmanagedType.Bool)]
		public bool TargetAvailable;
		public uint StatusFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DisplayConfigPathInfo
	{
		public DisplayConfigPathSourceInfo SourceInfo;
		public DisplayConfigPathTargetInfo TargetInfo;
		public uint Flags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DisplayConfigModeInfo
	{
		public uint InfoType;
		public uint Id;
		public Luid AdapterId;
		public DisplayConfigModeInfoUnion ModeInfo;
	}

	[StructLayout(LayoutKind.Explicit, Size = 48)]
	private struct DisplayConfigModeInfoUnion
	{
		[FieldOffset(0)]
		public long Alignment;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DisplayConfigDeviceInfoHeader
	{
		public DisplayConfigDeviceInfoType Type;
		public int Size;
		public Luid AdapterId;
		public uint Id;

		public static DisplayConfigDeviceInfoHeader Create(
			DisplayConfigDeviceInfoType type,
			Luid adapterId,
			uint id,
			int size) => new()
		{
			Type = type,
			Size = size,
			AdapterId = adapterId,
			Id = id
		};
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DisplayConfigSourceDeviceName
	{
		public DisplayConfigDeviceInfoHeader Header;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string ViewGdiDeviceName;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DisplayConfigTargetDeviceName
	{
		public DisplayConfigDeviceInfoHeader Header;
		public uint Flags;
		public uint OutputTechnology;
		public ushort EdidManufactureId;
		public ushort EdidProductCodeId;
		public uint ConnectorInstance;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string MonitorFriendlyDeviceName;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string MonitorDevicePath;
	}

	[DllImport("user32.dll")]
	private static extern int GetDisplayConfigBufferSizes(
		uint flags,
		out uint pathInfoArraySize,
		out uint modeInfoArraySize);

	[DllImport("user32.dll")]
	private static extern int QueryDisplayConfig(
		uint flags,
		ref uint pathInfoArraySize,
		[Out] DisplayConfigPathInfo[] pathInfoArray,
		ref uint modeInfoArraySize,
		[Out] DisplayConfigModeInfo[] modeInfoArray,
		IntPtr currentTopologyId);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);
}
