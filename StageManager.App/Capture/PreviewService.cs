using StageManager.Infrastructure;
using StageManager.Native.Window;
using StageManager.Settings;

namespace StageManager.Desktop;

internal enum PreviewCapturePriority
{
	Manual = 0,
	Current = 1,
	Hovered = 2,
	Periodic = 3
}

internal interface IPreviewService : IDisposable
{
	Task<CapturedCardFrame> CaptureAsync(
		IWindow window,
		int width,
		int height,
		string? countBadge,
		bool applicationCard,
		PreviewMode mode,
		PreviewCapturePriority priority,
		CancellationToken cancellationToken = default);
}

internal sealed class PreviewService : IPreviewService
{
	private const int MaximumQueueLength = 64;
	private readonly object _sync = new();
	private readonly PriorityQueue<QueuedCapture, (int Priority, long Sequence)> _queue = new();
	private readonly Dictionary<CaptureKey, QueuedCapture> _pending = new();
	private readonly SemaphoreSlim _signal = new(0);
	private readonly CancellationTokenSource _shutdown = new();
	private readonly CaptureWorkerClient _worker = new();
	private readonly WindowFrameCapture _fallback = new();
	private readonly Task _pump;
	private long _sequence;
	private bool _disposed;

	public PreviewService() => _pump = Task.Run(ProcessQueueAsync);

	public Task<CapturedCardFrame> CaptureAsync(
		IWindow window,
		int width,
		int height,
		string? countBadge,
		bool applicationCard,
		PreviewMode mode,
		PreviewCapturePriority priority,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (applicationCard || mode == PreviewMode.IconOnly)
			return Task.FromResult(_fallback.CaptureApplicationCard(window, width, height));
		if (cancellationToken.IsCancellationRequested)
			return Task.FromCanceled<CapturedCardFrame>(cancellationToken);

		var key = new CaptureKey(window.Handle, applicationCard);
		lock (_sync)
		{
			if (_pending.TryGetValue(key, out var existing))
			{
				if (priority < existing.Priority)
				{
					existing.Priority = priority;
					existing.Version++;
					_queue.Enqueue(existing, ((int)priority, Interlocked.Increment(ref _sequence)));
				}
				return existing.Completion.Task;
			}

			if (_pending.Count >= MaximumQueueLength)
			{
				var worst = _pending.Values
					.OrderByDescending(request => request.Priority)
					.ThenBy(request => request.Sequence)
					.First();
				if (priority >= worst.Priority)
					return Task.FromException<CapturedCardFrame>(new InvalidOperationException("The bounded preview queue is full."));
				_pending.Remove(worst.Key);
				worst.Canceled = true;
				worst.Completion.TrySetException(new InvalidOperationException("The preview was superseded by a higher-priority request."));
			}

			var request = new QueuedCapture(
				key,
				window,
				Math.Clamp(width, 24, 1024),
				Math.Clamp(height, 24, 1024),
				countBadge,
				applicationCard,
				mode,
				priority,
				Interlocked.Increment(ref _sequence));
			_pending.Add(key, request);
			_queue.Enqueue(request, ((int)priority, request.Sequence));
			_signal.Release();
			return request.Completion.Task;
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_shutdown.Cancel();
		_signal.Release();
		try { _pump.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
		lock (_sync)
		{
			foreach (var request in _pending.Values)
				request.Completion.TrySetCanceled();
			_pending.Clear();
			_queue.Clear();
		}
		_worker.Dispose();
		_fallback.Dispose();
		_signal.Dispose();
		_shutdown.Dispose();
	}

	private async Task ProcessQueueAsync()
	{
		while (!_shutdown.IsCancellationRequested)
		{
			await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
			QueuedCapture? request = null;
			lock (_sync)
			{
				while (_queue.TryDequeue(out var candidate, out _))
				{
					if (candidate.Canceled || !_pending.TryGetValue(candidate.Key, out var current) || !ReferenceEquals(candidate, current))
						continue;
					_pending.Remove(candidate.Key);
					request = candidate;
					break;
				}
			}
			if (request is null)
				continue;

			try
			{
				var frame = await _worker.CaptureAsync(
					request.Window.Handle,
					request.Width,
					request.Height,
					request.ApplicationCard,
					request.CountBadge,
					_shutdown.Token).ConfigureAwait(false);
				request.Completion.TrySetResult(frame);
			}
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
			{
				request.Completion.TrySetCanceled(_shutdown.Token);
			}
			catch (Exception exception)
			{
				AppLogger.Warn($"Isolated preview capture failed for {request.Window.ProcessName}: {exception.Message}");
				try
				{
					var fallback = _fallback.Capture(
						request.Window,
						request.Width,
						request.Height,
						request.CountBadge,
						allowWindowCapture: false,
						fallbackStatus: "CAPTURE FAILED");
					request.Completion.TrySetResult(fallback);
				}
				catch (Exception fallbackException)
				{
					request.Completion.TrySetException(fallbackException);
				}
			}
		}
	}

	private readonly record struct CaptureKey(IntPtr Handle, bool ApplicationCard);

	private sealed class QueuedCapture
	{
		public QueuedCapture(
			CaptureKey key,
			IWindow window,
			int width,
			int height,
			string? countBadge,
			bool applicationCard,
			PreviewMode mode,
			PreviewCapturePriority priority,
			long sequence)
		{
			Key = key;
			Window = window;
			Width = width;
			Height = height;
			CountBadge = countBadge;
			ApplicationCard = applicationCard;
			Mode = mode;
			Priority = priority;
			Sequence = sequence;
		}

		public CaptureKey Key { get; }
		public IWindow Window { get; }
		public int Width { get; }
		public int Height { get; }
		public string? CountBadge { get; }
		public bool ApplicationCard { get; }
		public PreviewMode Mode { get; }
		public PreviewCapturePriority Priority { get; set; }
		public long Sequence { get; }
		public int Version { get; set; }
		public bool Canceled { get; set; }
		public TaskCompletionSource<CapturedCardFrame> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
