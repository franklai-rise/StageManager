using System.Runtime.InteropServices;

namespace StageManager.Desktop;

internal sealed class DispatcherQueueHelper : IDisposable
{
	private object? _controller;

	public void EnsureDispatcherQueue()
	{
		if (_controller is not null)
			return;

		var options = new DispatcherQueueOptions
		{
			Size = Marshal.SizeOf<DispatcherQueueOptions>(),
			ThreadType = DispatcherQueueThreadType.Current,
			ApartmentType = DispatcherQueueApartmentType.ComSta
		};
		var result = CreateDispatcherQueueController(options, out _controller);
		Marshal.ThrowExceptionForHR(result);
	}

	public void Dispose()
	{
		if (_controller is not null && Marshal.IsComObject(_controller))
			Marshal.FinalReleaseComObject(_controller);
		_controller = null;
	}

	[DllImport("coremessaging.dll")]
	private static extern int CreateDispatcherQueueController(
		DispatcherQueueOptions options,
		[MarshalAs(UnmanagedType.IUnknown)] out object dispatcherQueueController);

	[StructLayout(LayoutKind.Sequential)]
	private struct DispatcherQueueOptions
	{
		public int Size;
		public DispatcherQueueThreadType ThreadType;
		public DispatcherQueueApartmentType ApartmentType;
	}

	private enum DispatcherQueueThreadType
	{
		Dedicated = 1,
		Current = 2
	}

	private enum DispatcherQueueApartmentType
	{
		None = 0,
		Asta = 1,
		ComSta = 2
	}
}
