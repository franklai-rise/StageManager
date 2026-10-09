using StageManager.Native.Window;

namespace StageManager.Card3DPrototype;

internal sealed class PreviewCaptureService : IDisposable
{
    private readonly CaptureWorkerClient _worker = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public async Task<CapturedCardFrame> CaptureAsync(IWindow window, int width, int height,
        string? countBadge = null, bool applicationCard = false, CancellationToken cancellationToken = default,
        bool allowPlaceholderOnFailure = true)
    {
        // Async method semantics also turn snapshot/property failures into a
        // faulted task, so renderer completion always clears its in-flight slot.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return await CaptureAsync(CaptureWindow.FromWindow(window), width, height, countBadge, applicationCard,
            cancellationToken, allowPlaceholderOnFailure).ConfigureAwait(false);
    }

    public async Task<CapturedCardFrame> CaptureAsync(CaptureWindow window, int width, int height,
        string? countBadge = null, bool applicationCard = false, CancellationToken cancellationToken = default,
        bool allowPlaceholderOnFailure = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var frame = await _worker.CaptureAsync(window, width, height, applicationCard, countBadge, linked.Token)
                .ConfigureAwait(false);
            if (!allowPlaceholderOnFailure && frame.IsPlaceholder && !window.IsMinimized)
            {
                frame.Dispose();
                throw new InvalidOperationException("No new preview is available; retain the previous frame.");
            }
            return frame;
        }
        catch (Exception) when (allowPlaceholderOnFailure && !linked.IsCancellationRequested)
        {
            // Own this renderer per fallback, not per service: Dispose never races
            // an in-flight GDI drawing operation. All main-process native capture
            // and foreign-window icon extraction are forbidden by WindowFrameCapture.
            return await CaptureDeadline.RunAsync(_ =>
            {
                using var fallback = new WindowFrameCapture();
                return Task.FromResult(applicationCard
                    ? fallback.CaptureApplicationCard(window, width, height)
                    : fallback.Capture(window, width, height, countBadge));
            }, CaptureWorkerClient.CaptureTimeout, linked.Token, late => late.Dispose()).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _worker.Dispose();
    }
}
