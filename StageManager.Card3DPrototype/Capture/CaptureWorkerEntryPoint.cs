using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace StageManager.Card3DPrototype;

internal static class CaptureWorkerEntryPoint
{
    internal const int MaximumCaptures = 100;
    internal const long MaximumPrivateBytes = 96L * 1024 * 1024;
    internal static bool IsWorkerProcess { get; private set; }

    internal static bool IsCaptureWorker(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], "--capture-worker", StringComparison.OrdinalIgnoreCase);

    // Must run before the normal app's singleton, form, hooks and tray startup.
    internal static int Run(IReadOnlyList<string> args)
    {
        if (!IsCaptureWorker(args)) return 64;
        var pipe = Argument(args, "--pipe");
        var secret = Argument(args, "--token");
        if (string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(secret) || secret.Length != 48) return 64;
        IsWorkerProcess = true;
        try { return RunAsync(pipe, secret).GetAwaiter().GetResult(); }
        catch { return 65; }
        finally { IsWorkerProcess = false; }
    }

    private static async Task<int> RunAsync(string pipeName, string secret)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        long generation;
        using (var startup = new CancellationTokenSource(CaptureWorkerClient.StartupTimeout))
        {
            await pipe.ConnectAsync(startup.Token).ConfigureAwait(false);
            var hello = await CaptureWorkerProtocol.ReadMessageAsync(pipe, startup.Token).ConfigureAwait(false);
            generation = CaptureWorkerProtocol.Decode(hello, reader =>
            {
                if (reader.ReadByte() != CaptureWorkerProtocol.Version) throw new InvalidDataException("Capture version mismatch.");
                var value = reader.ReadInt64();
                var received = Encoding.UTF8.GetBytes(reader.ReadString());
                if (!CryptographicOperations.FixedTimeEquals(received, Encoding.UTF8.GetBytes(secret)))
                    throw new InvalidDataException("Capture authentication failed.");
                return value;
            });
            await CaptureWorkerProtocol.WriteMessageAsync(pipe, CaptureWorkerProtocol.Encode(writer =>
            {
                writer.Write(CaptureWorkerProtocol.Version);
                writer.Write(generation);
            }), startup.Token).ConfigureAwait(false);
        }

        using var capture = new WindowFrameCapture();
        using var process = Process.GetCurrentProcess();
        var count = 0;
        long previousRequest = 0;
        while (true)
        {
            // Idle applies only before the first byte. A peer trickling a request
            // header/body cannot extend the subsequent two-second total deadline.
            var first = new byte[1];
            using (var idle = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    if (await pipe.ReadAsync(first, idle.Token).ConfigureAwait(false) == 0) return 0;
                }
                catch (OperationCanceledException) { return 0; }
                catch (IOException) { return 0; }
            }
            using var deadline = new CancellationTokenSource(CaptureWorkerClient.CaptureTimeout);
            // PrintWindow is uncancellable; this worker-local watchdog also bounds
            // it if the parent crashes. No UI or singleton has ever been created.
            using var watchdog = new System.Threading.Timer(_ => Environment.Exit(70), null,
                CaptureWorkerClient.CaptureTimeout, Timeout.InfiniteTimeSpan);
            var bytes = await CaptureWorkerProtocol.ReadMessageAsync(pipe, deadline.Token, first[0]).ConfigureAwait(false);
            var request = CaptureWorkerProtocol.DecodeRequest(bytes);
            if (request.Generation != generation || request.RequestId <= previousRequest) return 67;
            previousRequest = request.RequestId;
            using var frame = request.ApplicationCard
                ? capture.CaptureApplicationCard(request.Window, request.Width, request.Height)
                : capture.Capture(request.Window, request.Width, request.Height, request.CountBadge);
            process.Refresh();
            var recycle = ++count >= MaximumCaptures || process.PrivateMemorySize64 >= MaximumPrivateBytes;
            await CaptureWorkerProtocol.WriteFrameAsync(pipe, request, frame, recycle, deadline.Token).ConfigureAwait(false);
            if (recycle) return 0;
        }
    }

    private static string? Argument(IReadOnlyList<string> args, string key)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
