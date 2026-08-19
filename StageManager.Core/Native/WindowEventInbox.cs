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
			return true;

		RequestReconcile();
		return false;
	}

	public void RequestReconcile() => Interlocked.Exchange(ref _reconcileRequested, 1);

	public WindowEventBatch DrainBatch()
	{
		var events = new List<WindowEventEnvelope>();
		while (_channel.Reader.TryRead(out var item))
			events.Add(item);

		var requiresReconcile = Interlocked.Exchange(ref _reconcileRequested, 0) != 0;
		return WindowEventBatch.Create(events, requiresReconcile);
	}

	public void Complete() => _channel.Writer.TryComplete();
}
