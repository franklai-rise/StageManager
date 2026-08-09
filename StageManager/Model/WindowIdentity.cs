using System;

namespace StageManager.Model;

public sealed record WindowIdentity(
	IntPtr Handle,
	int ProcessId,
	string ProcessName,
	string ExecutablePath,
	string? AppUserModelId,
	string WindowClass,
	Guid VirtualDesktopId);
