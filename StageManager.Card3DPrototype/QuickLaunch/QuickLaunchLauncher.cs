using System.Diagnostics;
using StageManager.Settings;

namespace StageManager.Card3DPrototype.QuickLaunch;

internal enum QuickLaunchStatus { Succeeded, Failed, Busy, TimedOut, Cancelled }
internal readonly record struct QuickLaunchResult(QuickLaunchStatus Status, bool ShellDispatched = false);

internal sealed class QuickLaunchLauncher
{
	private readonly QuickLaunchStaWorkers _workers = new(2, "StageManager shell launch");
	private readonly Func<QuickLaunchDefinition, ProcessStartInfo> _prepare;
	private readonly Action<ProcessStartInfo> _dispatch;
	private readonly TimeSpan _timeout;

	// Injection is local to a launcher instance; tests never replace the live Shell service.
	internal QuickLaunchLauncher(Func<QuickLaunchDefinition, ProcessStartInfo>? prepare = null,
		Action<ProcessStartInfo>? dispatch = null, TimeSpan? timeout = null)
	{
		_prepare = prepare ?? QuickLaunchCatalog.CreateStartInfo;
		_dispatch = dispatch ?? (info => { using var process = Process.Start(info); });
		_timeout = timeout ?? TimeSpan.FromSeconds(5);
		if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(5))
			throw new ArgumentOutOfRangeException(nameof(timeout));
	}

	internal int AvailableSlots => _workers.AvailableSlots;

	public async Task<QuickLaunchResult> LaunchAsync(QuickLaunchDefinition app, CancellationToken token = default)
	{
		if (token.IsCancellationRequested) return new(QuickLaunchStatus.Cancelled);
		var request = new Request(_timeout, token);
		using var registration = token.Register(request.Abandon);
		var snapshot = app with { };
		if (!_workers.TryRun(() => Execute(snapshot, request), out var worker))
			return new(QuickLaunchStatus.Busy);
		try
		{
			var remaining = _timeout - request.Elapsed;
			if (remaining <= TimeSpan.Zero) throw new TimeoutException();
			return await worker.WaitAsync(remaining, token).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			request.Abandon();
			_ = ObserveAsync(worker);
			return new(QuickLaunchStatus.TimedOut, request.ShellDispatched);
		}
		catch (OperationCanceledException)
		{
			request.Abandon();
			_ = ObserveAsync(worker);
			return new(QuickLaunchStatus.Cancelled, request.ShellDispatched);
		}
		catch (Exception) { return new(QuickLaunchStatus.Failed, request.ShellDispatched); }
	}

	private QuickLaunchResult Execute(QuickLaunchDefinition app, Request request)
	{
		try
		{
			if (request.IsStopped) return request.StoppedResult;
			var info = _prepare(app); // May block (legacy registry/path lookup); never runs on UI.
			// Last cancellation/deadline check immediately before committing Shell dispatch.
			if (!request.TryBeginDispatch()) return request.StoppedResult;
			_dispatch(info);
			return new(QuickLaunchStatus.Succeeded, true);
		}
		catch (Exception) { return new(QuickLaunchStatus.Failed, request.ShellDispatched); }
	}

	private static async Task ObserveAsync(Task<QuickLaunchResult> worker)
	{
		try { await worker.ConfigureAwait(false); } catch (Exception) { }
	}

	private sealed class Request(TimeSpan timeout, CancellationToken token)
	{
		private readonly long _started = Stopwatch.GetTimestamp();
		private int _state; // 0 preparing, 1 Shell dispatch committed, 2 abandoned before dispatch
		public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);
		public bool ShellDispatched => Volatile.Read(ref _state) == 1;
		public bool IsStopped => token.IsCancellationRequested || Elapsed >= timeout || Volatile.Read(ref _state) == 2;
		public QuickLaunchResult StoppedResult => new(token.IsCancellationRequested ? QuickLaunchStatus.Cancelled : QuickLaunchStatus.TimedOut);
		public void Abandon() => Interlocked.CompareExchange(ref _state, 2, 0);
		public bool TryBeginDispatch()
		{
			if (IsStopped) { Abandon(); return false; }
			// Linearization point: cancellation winning this race prevents launch. Once
			// dispatch wins, Windows may still open the app after timeout/cancellation;
			// never retry automatically or claim the external launch was undone.
			return Interlocked.CompareExchange(ref _state, 1, 0) == 0;
		}
	}
}
