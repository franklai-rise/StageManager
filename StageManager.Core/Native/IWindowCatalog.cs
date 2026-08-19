using StageManager.Native.Window;
using StageManager.Model;

namespace StageManager.Native;

internal interface IWindowCatalog : IDisposable
{
	event WindowCreateDelegate? WindowCreated;
	event WindowDelegate? WindowDestroyed;
	event WindowUpdateDelegate? WindowUpdated;
	event EventHandler? DesktopChanged;

	IEnumerable<IWindow> Windows { get; }
	Task Start();
	void Stop();
	void ReevaluateWindows();
	bool TryGetWindow(IntPtr handle, out IWindow? window);
	bool TryGetWindowInstanceId(IntPtr handle, out WindowInstanceId instanceId);
	Guid GetDesktopId(IWindow window);
	bool IsWindowOnCurrentDesktop(IWindow window);
	Guid GetCurrentDesktopId(IntPtr foregroundHandle);
}
