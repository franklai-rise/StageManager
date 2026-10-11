using StageManager.Settings;

namespace StageManager.Card3DPrototype.QuickLaunch;

/// <summary>Owns the bitmap even while a UI callback is queued during shutdown.</summary>
internal sealed class QuickLaunchIconRequest : IDisposable
{
	private readonly object _gate = new();
	private readonly CancellationTokenSource _cancellation = new();
	private readonly CancellationToken _token;
	private Bitmap? _pending;
	private bool _disposed;
	private volatile bool _loaded;
	private long _finishedTick;
	public Task Completion { get; }
	public bool CanRetry => Completion.IsCompleted && !_loaded &&
		Environment.TickCount64 - Volatile.Read(ref _finishedTick) >= 10_000;

	public QuickLaunchIconRequest(QuickLaunchDefinition app, SynchronizationContext context, Action<Bitmap> accept,
		Func<QuickLaunchDefinition, CancellationToken, Task<Bitmap>>? load = null, TimeSpan? retryDelay = null)
	{
		_token = _cancellation.Token;
		Completion = LoadAsync(app, context, accept, load ?? QuickLaunchIcons.LoadRequiredAsync,
			retryDelay ?? TimeSpan.FromMilliseconds(500));
	}

	private async Task LoadAsync(QuickLaunchDefinition app, SynchronizationContext context, Action<Bitmap> accept,
		Func<QuickLaunchDefinition, CancellationToken, Task<Bitmap>> load, TimeSpan retryDelay)
	{
		try
		{
			for (var attempt = 1; attempt <= 3; attempt++)
			{
				_token.ThrowIfCancellationRequested();
				try
				{
					var icon = await load(app, _token).ConfigureAwait(false);
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
					_loaded = true;
					return;
				}
				catch (OperationCanceledException) when (_token.IsCancellationRequested) { return; }
				catch (Exception exception)
				{
					lock (_gate) { _pending?.Dispose(); _pending = null; }
					QuickLaunchIcons.RecordFailure(app, attempt, exception);
					if (attempt < 3) await Task.Delay(retryDelay, _token).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
		finally { Volatile.Write(ref _finishedTick, Environment.TickCount64); }
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_cancellation.Cancel();
			_pending?.Dispose();
			_pending = null;
		}
		// Keep the token source alive until a possibly late native result has been
		// disposed. Completion also lets tests inspect bitmap state without racing it.
		_ = Completion.ContinueWith(_ => _cancellation.Dispose(), CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}
}
