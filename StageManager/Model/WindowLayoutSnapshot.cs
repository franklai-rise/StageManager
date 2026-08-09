using StageManager.Native.Window;
using System;

namespace StageManager.Model;

public sealed record WindowLayoutSnapshot(
	IntPtr Handle,
	int X,
	int Y,
	int Width,
	int Height,
	WindowState State,
	string DisplayDeviceName,
	int DisplayWorkLeft,
	int DisplayWorkTop,
	int DisplayWorkWidth,
	int DisplayWorkHeight,
	int ZOrder);
