using StageManager.Model;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace StageManager.Native;

/// <summary>
/// A non-blocking, bounded hand-off from WinEvent callbacks to the single event
/// pump. Overflow deliberately requests a full reconciliation instead of
/// retaining an unbounded backlog.
/// </summary>
internal sealed class WindowEventInbox
{
	public const int DefaultCapacity = 2048;

	private readonly Channel<WindowEventEnvelope> _channel;
	private readonly SemaphoreSlim _workSignal = new(0, 1);
	private int _reconcileRequested;

	public WindowEventInbox(int capacity = DefaultCapacity)
	{
		if (capacity <= 0)
			throw new ArgumentOutOfRangeException(nameof(capacity));

		_channel = Channel.CreateBounded<WindowEventEnvelope>(new BoundedChannelOptions(capacity)
		{
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false,
			FullMode = BoundedChannelFullMode.Wait,
		});
	}

	public bool TryWrite(WindowEventEnvelope item)
	{
		if (_channel.Writer.TryWrite(item))
		{
			SignalWork();
			return true;
		}

		RequestReconcile();
		return false;
	}

	public void RequestReconcile()
	{
		Interlocked.Exchange(ref _reconcileRequested, 1);
		SignalWork();
	}

	public async Task<bool> WaitForWorkAsync(TimeSpan? timeout, CancellationToken cancellationToken)
	{
		if (timeout is null)
		{
			await _workSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
			return true;
		}

		return await _workSignal.WaitAsync(timeout.Value, cancellationToken).ConfigureAwait(false);
	}

	public void ClearPendingSignals()
	{
		while (_workSignal.Wait(0))
		{
		}
	}

	public WindowEventBatch DrainBatch()
	{
		var events = new List<WindowEventEnvelope>();
		while (_channel.Reader.TryRead(out var item))
			events.Add(item);

		var requiresReconcile = Interlocked.Exchange(ref _reconcileRequested, 0) != 0;
		return WindowEventBatch.Create(events, requiresReconcile);
	}

	public void Complete()
	{
		_channel.Writer.TryComplete();
		SignalWork();
	}

	private void SignalWork()
	{
		try
		{
			_workSignal.Release();
		}
		catch (SemaphoreFullException)
		{
			// One pending wake-up is enough; the bounded channel retains the work.
		}
	}
}
