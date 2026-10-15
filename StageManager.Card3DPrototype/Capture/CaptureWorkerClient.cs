using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace StageManager.Card3DPrototype;

internal sealed class CaptureWorkerClient : IDisposable
{
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private Session? _current;
    private long _generation;
    private long _requestId;
    private bool _disposed;

    internal int? WorkerProcessId
    {
        get { lock (_sync) return _current?.ProcessId; }
    }

    public async Task<CapturedCardFrame> CaptureAsync(CaptureWindow window, int width, int height,
        bool applicationCard, string? countBadge, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        Session? session = null;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                session = _current;
            }
            if (session is not null && !session.IsReusable)
            {
                Retire(session);
                session = null;
            }
            if (session is null)
            {
                session = new Session(Interlocked.Increment(ref _generation));
                lock (_sync)
                {
                    if (_disposed) { session.Stop(); throw new ObjectDisposedException(nameof(CaptureWorkerClient)); }
                    _current = session;
                }
                // Includes Process.Start, connection, both handshake writes and all reads.
                var starting = session;
                await CaptureDeadline.RunAsync(async token =>
                {
                    await starting.StartAsync(token).ConfigureAwait(false);
                    return true;
                }, StartupTimeout, linked.Token).ConfigureAwait(false);
            }

            var active = session;
            var request = new CaptureRequest(active.Generation, Interlocked.Increment(ref _requestId),
                window, Math.Clamp(width, 32, 1024), Math.Clamp(height, 24, 1024), applicationCard,
                CaptureWindow.Limit(countBadge, 128));
            var reply = await CaptureDeadline.RunAsync(async token =>
            {
                await CaptureWorkerProtocol.WriteMessageAsync(active.Pipe,
                    CaptureWorkerProtocol.EncodeRequest(request), token).ConfigureAwait(false);
                return await CaptureWorkerProtocol.ReadFrameAsync(active.Pipe, request, token).ConfigureAwait(false);
            }, CaptureTimeout, linked.Token, late => late.Dispose()).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_current, active) || linked.IsCancellationRequested)
                {
                    reply.Dispose();
                    throw new OperationCanceledException(linked.Token);
                }
            }
            active.MarkUsed();
            if (reply.Recycle) Retire(active);
            return reply.Frame;
        }
        catch
        {
            if (session is not null) Retire(session);
            throw;
        }
        finally { _gate.Release(); }
    }

    private void Retire(Session session)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, session)) _current = null;
        }
        session.Stop();
    }

    public void Dispose()
    {
        Session? session;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            session = _current;
            _current = null;
        }
        _shutdown.Cancel();
        session?.Stop();
        // No gate.Wait, pipe shutdown write, Flush, WaitForExit or synchronous task
        // join. Active/queued callers unwind on cancellation and release their gate.
        // Do not dispose synchronization objects while those callers still use them.
    }

    private sealed class Session
    {
        private readonly object _sync = new();
        private readonly string _pipeName = $"Stage_Manager_Lai_Capture_{Environment.ProcessId}_{Guid.NewGuid():N}";
        private readonly string _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        private Process? _process;
        private bool _stopped;
        private long _lastUse = Stopwatch.GetTimestamp();
        private int _captures;

        internal Session(long generation)
        {
            Generation = generation;
            Pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
        }

        internal long Generation { get; }
        internal NamedPipeServerStream Pipe { get; }
        internal int? ProcessId { get { lock (_sync) return _process?.Id; } }
        internal bool IsReusable
        {
            get
            {
                lock (_sync)
                {
                    try
                    {
                        return !_stopped && _captures < 100 && Stopwatch.GetElapsedTime(_lastUse) < TimeSpan.FromSeconds(30)
                            && Pipe.IsConnected && _process is { HasExited: false };
                    }
                    catch (InvalidOperationException) { return false; }
                }
            }
        }

        internal void MarkUsed() { _captures++; _lastUse = Stopwatch.GetTimestamp(); }

        internal async Task StartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No current executable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };
            // Support framework-dependent `dotnet app.dll` as well as the normal apphost EXE.
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entryAssembly = System.Reflection.Assembly.GetEntryAssembly() ??
                    throw new InvalidOperationException("No current application assembly.");
                var assemblyPath = Path.Combine(AppContext.BaseDirectory, entryAssembly.GetName().Name + ".dll");
                if (!File.Exists(assemblyPath))
                    throw new InvalidOperationException("The current application assembly was not found beside the executable.");
                start.ArgumentList.Add(assemblyPath);
            }
            foreach (var value in new[] { "--capture-worker", "--pipe", _pipeName, "--token", _secret })
                start.ArgumentList.Add(value);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start capture worker.");
            lock (_sync)
            {
                if (_stopped || token.IsCancellationRequested)
                {
                    Kill(process);
                    throw new OperationCanceledException(token);
                }
                _process = process;
            }
            await Pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            await CaptureWorkerProtocol.WriteMessageAsync(Pipe, CaptureWorkerProtocol.Encode(writer =>
            {
                writer.Write(CaptureWorkerProtocol.Version);
                writer.Write(Generation);
                writer.Write(_secret);
            }), token).ConfigureAwait(false);
            var acknowledgement = await CaptureWorkerProtocol.ReadMessageAsync(Pipe, token).ConfigureAwait(false);
            CaptureWorkerProtocol.Decode(acknowledgement, reader =>
            {
                if (reader.ReadByte() != CaptureWorkerProtocol.Version || reader.ReadInt64() != Generation)
                    throw new InvalidDataException("Capture handshake mismatch.");
                return true;
            });
        }

        internal void Stop()
        {
            Process? process;
            lock (_sync)
            {
                if (_stopped) return;
                _stopped = true;
                process = _process;
                _process = null;
            }
            // Closing the session pipe cancels outstanding I/O; never dispose a
            // BinaryWriter here (Dispose could synchronously flush into a full pipe).
            try { Pipe.Dispose(); } catch (IOException) { }
            if (process is not null) Kill(process);
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }
}
