namespace StageManager.Card3DPrototype;

internal static class CaptureDeadline
{
    // WaitAsync is also a backstop for injected/broken transports which ignore
    // cancellation. The abandoned operation owns only its retired session.
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout, CancellationToken cancellationToken, Action<T>? discard = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var pending = Task.Run(() => operation(deadline.Token), CancellationToken.None);
        try
        {
            var result = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            // WaitAsync may choose a just-completed task over simultaneous token
            // cancellation. Such a result still belongs to the discard path.
            deadline.Token.ThrowIfCancellationRequested();
            deadline.Dispose();
            return result;
        }
        catch
        {
            var timedOut = !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested;
            // Do not dispose the CTS until late code finishes using its token.
            _ = pending.ContinueWith(completed =>
            {
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion) discard?.Invoke(completed.Result);
                    else _ = completed.Exception;
                }
                finally { deadline.Dispose(); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (timedOut)
                throw new TimeoutException("The isolated capture operation exceeded its deadline.");
            throw;
        }
    }
}
