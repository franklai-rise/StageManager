using Windows.UI.Composition;

namespace StageManager.Desktop;

internal interface IRenderSurfacePool : IDisposable
{
	RenderSurfaceLease? Rent(object ownerKey, Action onSurfaceReassigned, int width, int height);
	void TrimExpired();
	void ReleaseAllIdle();
}

internal static class RenderSurfacePoolPolicy
{
	public const int MaximumSurfaceCount = 16;
	public const long MaximumSurfaceBytes = 16L * 1024 * 1024;
	public static readonly TimeSpan IdleLifetime = TimeSpan.FromSeconds(15);

	public static long EstimateBytes(int width, int height) =>
		checked((long)Math.Max(1, width) * Math.Max(1, height) * 4 * 2);

	public static bool CanAllocate(int count, long bytes, int width, int height) =>
		count < MaximumSurfaceCount && bytes + EstimateBytes(width, height) <= MaximumSurfaceBytes;
}

internal sealed class RenderSurfacePool : IRenderSurfacePool
{
	private readonly object _sync = new();
	private readonly D3DCompositionDevice _graphics;
	private readonly Compositor _compositor;
	private readonly List<IdleSurface> _idle = new();
	private int _totalCount;
	private long _totalBytes;
	private bool _disposed;

	public RenderSurfacePool(D3DCompositionDevice graphics, Compositor compositor)
	{
		_graphics = graphics;
		_compositor = compositor;
	}

	public RenderSurfaceLease? Rent(object ownerKey, Action onSurfaceReassigned, int width, int height)
	{
		ArgumentNullException.ThrowIfNull(ownerKey);
		ArgumentNullException.ThrowIfNull(onSurfaceReassigned);
		width = Math.Max(1, width);
		height = Math.Max(1, height);
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			TrimExpiredCore(DateTime.UtcNow);
			var reusableIndex = _idle.FindIndex(item =>
				ReferenceEquals(item.OwnerKey, ownerKey) && item.Surface.Width == width && item.Surface.Height == height);
			if (reusableIndex < 0)
				reusableIndex = _idle.FindIndex(item => item.Surface.Width == width && item.Surface.Height == height);
			if (reusableIndex >= 0)
			{
				var idle = _idle[reusableIndex];
				_idle.RemoveAt(reusableIndex);
				if (!ReferenceEquals(idle.OwnerKey, ownerKey))
					idle.OnSurfaceReassigned();
				return new RenderSurfaceLease(this, idle.Surface, ownerKey, onSurfaceReassigned);
			}

			while (!RenderSurfacePoolPolicy.CanAllocate(_totalCount, _totalBytes, width, height) && _idle.Count > 0)
				DisposeIdleAt(FindOldestIdleIndex());
			if (!RenderSurfacePoolPolicy.CanAllocate(_totalCount, _totalBytes, width, height))
				return null;

			var surface = _graphics.CreateSurface(_compositor, width, height);
			_totalCount++;
			_totalBytes += RenderSurfacePoolPolicy.EstimateBytes(width, height);
			return new RenderSurfaceLease(this, surface, ownerKey, onSurfaceReassigned);
		}
	}

	public void TrimExpired()
	{
		lock (_sync)
		{
			if (!_disposed)
				TrimExpiredCore(DateTime.UtcNow);
		}
	}

	public void ReleaseAllIdle()
	{
		lock (_sync)
		{
			while (_idle.Count > 0)
				DisposeIdleAt(_idle.Count - 1);
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			while (_idle.Count > 0)
				DisposeIdleAt(_idle.Count - 1);
		}
	}

	internal void Return(CardSwapChain surface, object ownerKey, Action onSurfaceReassigned)
	{
		lock (_sync)
		{
			if (_disposed)
			{
				onSurfaceReassigned();
				DisposeSurface(surface);
				return;
			}
			_idle.Add(new IdleSurface(surface, ownerKey, onSurfaceReassigned, DateTime.UtcNow));
			TrimExpiredCore(DateTime.UtcNow);
		}
	}

	private void TrimExpiredCore(DateTime nowUtc)
	{
		for (var index = _idle.Count - 1; index >= 0; index--)
		{
			if (nowUtc - _idle[index].ReturnedUtc >= RenderSurfacePoolPolicy.IdleLifetime)
				DisposeIdleAt(index);
		}
	}

	private int FindOldestIdleIndex()
	{
		var oldestIndex = 0;
		for (var index = 1; index < _idle.Count; index++)
		{
			if (_idle[index].ReturnedUtc < _idle[oldestIndex].ReturnedUtc)
				oldestIndex = index;
		}
		return oldestIndex;
	}

	private void DisposeIdleAt(int index)
	{
		var idle = _idle[index];
		_idle.RemoveAt(index);
		idle.OnSurfaceReassigned();
		DisposeSurface(idle.Surface);
	}

	private void DisposeSurface(CardSwapChain surface)
	{
		_totalCount--;
		_totalBytes -= RenderSurfacePoolPolicy.EstimateBytes(surface.Width, surface.Height);
		surface.Dispose();
	}

	private sealed record IdleSurface(
		CardSwapChain Surface,
		object OwnerKey,
		Action OnSurfaceReassigned,
		DateTime ReturnedUtc);
}

internal sealed class RenderSurfaceLease : IDisposable
{
	private RenderSurfacePool? _owner;
	private CardSwapChain? _surface;
	private object? _ownerKey;
	private Action? _onSurfaceReassigned;

	internal RenderSurfaceLease(
		RenderSurfacePool owner,
		CardSwapChain surface,
		object ownerKey,
		Action onSurfaceReassigned)
	{
		_owner = owner;
		_surface = surface;
		_ownerKey = ownerKey;
		_onSurfaceReassigned = onSurfaceReassigned;
	}

	public ICompositionSurface CompositionSurface =>
		(_surface ?? throw new ObjectDisposedException(nameof(RenderSurfaceLease))).CompositionSurface;

	public void Upload(ReadOnlySpan<byte> pixels) =>
		(_surface ?? throw new ObjectDisposedException(nameof(RenderSurfaceLease))).Upload(pixels);

	public void Dispose()
	{
		var surface = Interlocked.Exchange(ref _surface, null);
		var owner = Interlocked.Exchange(ref _owner, null);
		var ownerKey = Interlocked.Exchange(ref _ownerKey, null);
		var callback = Interlocked.Exchange(ref _onSurfaceReassigned, null);
		if (surface is not null && owner is not null && ownerKey is not null && callback is not null)
			owner.Return(surface, ownerKey, callback);
	}
}
