using StageManager.Settings;

namespace StageManager.Card3DPrototype.QuickLaunch;

/// <summary>Owns the bitmap even while a UI callback is queued during shutdown.</summary>
internal sealed class QuickLaunchIconRequest : IDisposable
{
	private readonly object _gate = new();
	private readonly CancellationTokenSource _cancellation = new();
	private Bitmap? _pending;
	private bool _disposed;

	public QuickLaunchIconRequest(QuickLaunchDefinition app, SynchronizationContext context, Action<Bitmap> accept,
		Func<QuickLaunchDefinition, CancellationToken, Task<Bitmap>>? load = null)
	{
		_ = LoadAsync(app, context, accept, load ?? QuickLaunchIcons.LoadAsync);
	}

	private async Task LoadAsync(QuickLaunchDefinition app, SynchronizationContext context, Action<Bitmap> accept,
		Func<QuickLaunchDefinition, CancellationToken, Task<Bitmap>> load)
	{
		try
		{
			var icon = await load(app, _cancellation.Token).ConfigureAwait(false);
			lock (_gate)
			{
				if (_disposed) { icon.Dispose(); return; }
				_pending = icon;
			}
			context.Post(_ =>
			{
				lock (_gate)
				{
					if (_disposed || _pending is null) return;
					var pending = _pending;
					_pending = null;
					accept(pending); // UI-thread consumer takes ownership.
				}
			}, null);
		}
		catch (Exception)
		{
			lock (_gate) { _pending?.Dispose(); _pending = null; }
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_cancellation.Cancel();
			_cancellation.Dispose();
			_pending?.Dispose();
			_pending = null;
		}
	}
}
