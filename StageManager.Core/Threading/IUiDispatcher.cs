namespace StageManager.Threading;

public interface IUiDispatcher
{
	bool CheckAccess();
	Task InvokeAsync(Func<Task> action);
}

public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
	private readonly SynchronizationContext _context;
	private readonly int _threadId;

	public SynchronizationContextUiDispatcher(SynchronizationContext context)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_threadId = Environment.CurrentManagedThreadId;
	}

	public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

	public Task InvokeAsync(Func<Task> action)
	{
		ArgumentNullException.ThrowIfNull(action);
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_context.Post(async _ =>
		{
			try
			{
				await action().ConfigureAwait(true);
				completion.SetResult();
			}
			catch (Exception exception)
			{
				completion.SetException(exception);
			}
		}, null);
		return completion.Task;
	}
}
