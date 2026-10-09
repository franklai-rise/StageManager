using System.Runtime.InteropServices;
using WinRT;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace StageManager.Desktop;

internal static class CompositionInterop
{
	public static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr windowHandle, bool isTopmost)
	{
		var nativeObject = ((IWinRTObject)compositor).NativeObject;
		var interfaceId = typeof(ICompositorDesktopInterop).GUID;
		var queryResult = Marshal.QueryInterface(nativeObject.ThisPtr, ref interfaceId, out var rawInterop);
		Marshal.ThrowExceptionForHR(queryResult);
		IntPtr rawTarget;
		try
		{
			var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(rawInterop);
			interop.CreateDesktopWindowTarget(windowHandle, isTopmost, out rawTarget);
		}
		finally
		{
			Marshal.Release(rawInterop);
		}
		if (rawTarget == IntPtr.Zero)
			throw new InvalidOperationException("Composition did not return a desktop target.");

		try { return MarshalInspectable<DesktopWindowTarget>.FromAbi(rawTarget); }
		finally { MarshalInspectable<DesktopWindowTarget>.DisposeAbi(rawTarget); }
	}

	public static ICompositionSurface CreateCompositionSurfaceForSwapChain(Compositor compositor, IntPtr swapChain)
	{
		var nativeObject = ((IWinRTObject)compositor).NativeObject;
		var interfaceId = typeof(ICompositorInterop).GUID;
		var queryResult = Marshal.QueryInterface(nativeObject.ThisPtr, ref interfaceId, out var rawInterop);
		Marshal.ThrowExceptionForHR(queryResult);
		IntPtr rawSurface;
		try
		{
			var interop = (ICompositorInterop)Marshal.GetObjectForIUnknown(rawInterop);
			var result = interop.CreateCompositionSurfaceForSwapChain(swapChain, out rawSurface);
			Marshal.ThrowExceptionForHR(result);
		}
		finally
		{
			Marshal.Release(rawInterop);
		}

		try { return MarshalInspectable<ICompositionSurface>.FromAbi(rawSurface); }
		finally { MarshalInspectable<ICompositionSurface>.DisposeAbi(rawSurface); }
	}

	[ComImport]
	[Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ICompositorDesktopInterop
	{
		void CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
	}

	[ComImport]
	[Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ICompositorInterop
	{
		[PreserveSig]
		int CreateCompositionSurfaceForHandle(IntPtr swapChainHandle, out IntPtr surface);

		[PreserveSig]
		int CreateCompositionSurfaceForSwapChain(IntPtr swapChain, out IntPtr surface);

		[PreserveSig]
		int CreateGraphicsDevice(IntPtr renderingDevice, out IntPtr graphicsDevice);
	}
}
