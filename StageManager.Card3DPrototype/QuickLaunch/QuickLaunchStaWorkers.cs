namespace StageManager.Card3DPrototype.QuickLaunch;

/// <summary>A slot belongs to the native worker, not its potentially timed-out caller.</summary>
internal sealed class QuickLaunchStaWorkers
{
	private readonly SemaphoreSlim _slots;
	private readonly string _name;

	public QuickLaunchStaWorkers(int capacity, string name)
	{
		_slots = new SemaphoreSlim(capacity, capacity);
		_name = name;
	}

	internal int AvailableSlots => _slots.CurrentCount;

	// Launch admission never queues: a later free slot must not replay an old click.
	public bool TryRun<T>(Func<T> action, out Task<T> task)
	{
		if (!_slots.Wait(0)) { task = null!; return false; }
		task = Start(action);
		return true;
	}

	public async Task<T> RunAsync<T>(Func<T> action, CancellationToken token)
	{
		await _slots.WaitAsync(token).ConfigureAwait(false);
		var worker = Start(() =>
		{
			token.ThrowIfCancellationRequested();
			var result = action();
			if (token.IsCancellationRequested)
			{
				if (result is IDisposable disposable) disposable.Dispose();
				token.ThrowIfCancellationRequested();
			}
			return result;
		});
		try { return await worker.WaitAsync(token).ConfigureAwait(false); }
		catch (OperationCanceledException)
		{
			// Observe late faults and dispose results if cancellation won the delivery race.
			_ = DisposeAbandonedResultAsync(worker);
			throw;
		}
	}

	private static async Task DisposeAbandonedResultAsync<T>(Task<T> worker)
	{
		try { if (await worker.ConfigureAwait(false) is IDisposable disposable) disposable.Dispose(); }
		catch (Exception) { }
	}

	private Task<T> Start<T>(Func<T> action)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			var thread = new Thread(() =>
			{
				try { completion.TrySetResult(action()); }
				catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
				catch (Exception ex) { completion.TrySetException(ex); }
				finally { _slots.Release(); }
			}) { IsBackground = true, Name = _name };
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
		}
		catch (Exception ex)
		{
			_slots.Release();
			completion.TrySetException(ex);
		}
		return completion.Task;
	}
}
