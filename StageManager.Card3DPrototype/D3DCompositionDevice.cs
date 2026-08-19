using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.UI.Composition;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace StageManager.Card3DPrototype;

internal sealed class D3DCompositionDevice : IDisposable
{
	private readonly ID3D11Device _device;
	private readonly ID3D11DeviceContext _context;
	private readonly IDXGIFactory2 _factory;
	private bool _disposed;

	public D3DCompositionDevice(bool lowMemoryRendering)
	{
		var featureLevels = new[]
		{
			FeatureLevel.Level_11_1,
			FeatureLevel.Level_11_0,
			FeatureLevel.Level_10_1,
			FeatureLevel.Level_10_0
		};
		// A hardware D3D device loads the vendor's complete user-mode driver into this
		// small utility process. On discrete NVIDIA systems that context alone can retain
		// tens of megabytes of private memory. The cards are static, low-resolution
		// snapshots, so WARP is a better default: Composition still performs the visual
		// transforms, while uploads avoid a per-process vendor GPU context.
		if (lowMemoryRendering)
		{
			try
			{
				_device = D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, featureLevels);
			}
			catch
			{
				_device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels);
			}
		}
		else
		{
			_device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels);
		}
		_context = _device.ImmediateContext;
		_factory = CreateDXGIFactory2<IDXGIFactory2>(false);
	}

	public CardSwapChain CreateSurface(Compositor compositor, int width, int height)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return new CardSwapChain(_device, _context, _factory, compositor, width, height);
	}

	public void Trim()
	{
		if (!_disposed)
			_context.Flush();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_factory.Dispose();
		_context.Dispose();
		_device.Dispose();
	}
}

internal sealed class CardSwapChain : IDisposable
{
	private readonly ID3D11DeviceContext _context;
	private readonly IDXGISwapChain1 _swapChain;
	private readonly ID3D11Texture2D _backBuffer;
	private bool _disposed;

	public CardSwapChain(
		ID3D11Device device,
		ID3D11DeviceContext context,
		IDXGIFactory2 factory,
		Compositor compositor,
		int width,
		int height)
	{
		Width = Math.Max(1, width);
		Height = Math.Max(1, height);
		_context = context;
		var description = new SwapChainDescription1(
			(uint)Width,
			(uint)Height,
			Format.B8G8R8A8_UNorm,
			false,
			Usage.RenderTargetOutput,
			2,
			Scaling.Stretch,
			SwapEffect.FlipSequential,
			AlphaMode.Premultiplied,
			SwapChainFlags.None);
		_swapChain = factory.CreateSwapChainForComposition(device, description);
		_backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
		CompositionSurface = CompositionInterop.CreateCompositionSurfaceForSwapChain(compositor, _swapChain.NativePointer);
	}

	public int Width { get; }
	public int Height { get; }
	public ICompositionSurface CompositionSurface { get; }

	public unsafe void Upload(ReadOnlySpan<byte> pixels)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (pixels.Length != Width * Height * 4)
			throw new ArgumentException("Pixel buffer size does not match the surface.", nameof(pixels));

		fixed (byte* source = pixels)
			_context.UpdateSubresource(_backBuffer, 0, null, (IntPtr)source, (uint)(Width * 4), 0);
		_swapChain.Present(0, PresentFlags.None);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		if (CompositionSurface is IDisposable disposableSurface)
			disposableSurface.Dispose();
		_backBuffer.Dispose();
		_swapChain.Dispose();
	}
}
