using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using StageManager.Card3DPrototype;

internal static class CaptureIsolationRegressionTests
{
    // Registration is deliberately left to the integration owner.
    internal static void RunAll()
    {
        SourceBudgetAndMainProcessGuard();
        ProtocolRoundTripAndIdentity().GetAwaiter().GetResult();
        PartialReadsAndBlockedWritesAreBounded().GetAwaiter().GetResult();
        LateCompletionIsDisposedAndNextRequestRuns().GetAwaiter().GetResult();
        CancellationAndShutdownAreBounded().GetAwaiter().GetResult();
        WorkerProcessRoundTrip().GetAwaiter().GetResult();
        OwnedHungWindowIsBounded().GetAwaiter().GetResult();
        StalledWorkerIsKilledAndNextRequestSucceeds().GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void SourceBudgetAndMainProcessGuard()
    {
        Check(WindowFrameCapture.IsSourceSizeAllowed(4000, 4000), "Exact 16Mpx boundary rejected.");
        Check(!WindowFrameCapture.IsSourceSizeAllowed(4001, 4000), "Over-16Mpx source accepted.");
        Check(!WindowFrameCapture.IsSourceSizeAllowed(long.MaxValue, long.MaxValue), "Overflow dimensions accepted.");
        Check(!WindowFrameCapture.IsSourceSizeAllowed(8193, 2), "Excessive side accepted.");
        Check(!WindowFrameCapture.IsSourceSizeAllowed(1, 100), "Degenerate source accepted.");
        Check(!CaptureWorkerEntryPoint.IsWorkerProcess, "Test process became a capture worker.");
        var identity = new CaptureWindow(new IntPtr(123), "", "", "", false, 100, 200);
        Check(CaptureWindowIdentity.Matches(identity, (100, 200)), "Matching owner rejected.");
        Check(!CaptureWindowIdentity.Matches(identity, (101, 200)), "Reused HWND owner accepted.");
        Check(!CaptureWindowIdentity.Matches(identity, (100, 201)), "Recycled PID accepted.");
        Check(!CaptureWindowIdentity.Matches(identity, (100, null)), "Unknown creation time accepted.");
        Check(!CaptureWindowIdentity.Matches(identity with { ProcessStartTime = null }, (100, 200)),
            "Missing snapshot identity allowed capture.");
        var permission = WindowFrameCapture.PermissionBadgeBounds(160, 100, false);
        Check(permission.Top > 40 && permission.Left >= 33 && permission.Bottom < 100,
            "Permission badge overlaps the top-row status/count or bottom-left icon.");
        Check(WindowFrameCapture.PermissionBadgeBounds(32, 24, false).IsEmpty,
            "Tiny card cannot fit independent badges.");
        using var renderer = new WindowFrameCapture();
        using var first = renderer.Capture(new CaptureWindow(IntPtr.Zero, "Released placeholder title", "test", "", false), 160, 100);
        using var second = renderer.Capture(new CaptureWindow(IntPtr.Zero, "A different title", "test", "", false), 160, 100);
        Check(first.IsPlaceholder && second.IsPlaceholder, "Main process attempted native preview capture.");
        Check(!first.Pixels.SequenceEqual(second.Pixels), "Placeholder title was lost.");
    }

    internal static async Task ProtocolRoundTripAndIdentity()
    {
        var request = Request();
        Check(CaptureWorkerProtocol.DecodeRequest(CaptureWorkerProtocol.EncodeRequest(request)) == request,
            "Request snapshot failed round trip.");
        using var frame = Frame();
        using var pipe = new MemoryStream();
        await CaptureWorkerProtocol.WriteFrameAsync(pipe, request, frame, true, CancellationToken.None);
        var packet = pipe.ToArray();
        pipe.Position = 0;
        using var reply = await CaptureWorkerProtocol.ReadFrameAsync(pipe, request, CancellationToken.None);
        Check(reply.Recycle && reply.Frame.IsPlaceholder && reply.Frame.Pixels.SequenceEqual(frame.Pixels),
            "Frame/recycle metadata failed round trip.");
        foreach (var mismatch in new[]
        {
            request with { Generation = request.Generation + 1 },
            request with { RequestId = request.RequestId + 1 },
            request with { Window = request.Window with { Handle = new IntPtr(999) } },
            request with { Width = 33 }
        })
        {
            using var bad = new MemoryStream(packet);
            await ExpectAsync<InvalidDataException>(async () =>
            {
                using var unexpected = await CaptureWorkerProtocol.ReadFrameAsync(bad, mismatch, CancellationToken.None);
            });
        }
        using var truncated = new MemoryStream(packet[..^1]);
        await ExpectAsync<EndOfStreamException>(async () =>
        {
            using var unexpected = await CaptureWorkerProtocol.ReadFrameAsync(truncated, request, CancellationToken.None);
        });
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, int.MaxValue);
        using var oversized = new MemoryStream(prefix);
        await ExpectAsync<InvalidDataException>(async () =>
            await CaptureWorkerProtocol.ReadMessageAsync(oversized, CancellationToken.None));
    }

    internal static async Task PartialReadsAndBlockedWritesAreBounded()
    {
        // Real async named pipes: no BinaryReader.ReadByte or writer.Flush may sit
        // outside the aggregate timeout, including a connected-but-silent handshake.
        await WithPipe(async (server, client) =>
        {
            await AssertTimeout(async token =>
                await CaptureWorkerProtocol.ReadMessageAsync(server, token));
        });
        await WithPipe(async (server, client) =>
        {
            await client.WriteAsync(new byte[] { 20, 0 }); // Half a length prefix.
            await AssertTimeout(async token =>
                await CaptureWorkerProtocol.ReadMessageAsync(server, token));
        });
        await WithPipe(async (server, client) =>
        {
            await client.WriteAsync(new byte[] { 20, 0, 0, 0, 1 }); // Partial body.
            await AssertTimeout(async token =>
                await CaptureWorkerProtocol.ReadMessageAsync(server, token));
        });
        await WithPipe(async (server, client) =>
        {
            await AssertTimeout(async token =>
            {
                // The peer deliberately never reads; eventually even large OS
                // pipe buffers fill. One deadline covers every full write.
                for (var i = 0; i < 512; i++)
                    await CaptureWorkerProtocol.WriteMessageAsync(server, new byte[32768], token);
                return true;
            });
        });
        await WithPipe(async (server, client) =>
        {
            using var frame = Frame();
            using var serialized = new MemoryStream();
            await CaptureWorkerProtocol.WriteFrameAsync(serialized, Request(), frame, false, CancellationToken.None);
            var bytes = serialized.ToArray();
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            await client.WriteAsync(bytes.AsMemory(0, 4 + headerLength + 1)); // Pixel read stalls.
            await AssertTimeout(async token =>
                await CaptureWorkerProtocol.ReadFrameAsync(server, Request(), token));
        });
    }

    internal static async Task LateCompletionIsDisposedAndNextRequestRuns()
    {
        var late = new TaskCompletionSource<CapturedCardFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ExpectAsync<TimeoutException>(async () =>
            await CaptureDeadline.RunAsync(_ => late.Task, TimeSpan.FromMilliseconds(80), CancellationToken.None,
                frame => { frame.Dispose(); disposed.SetResult(); }));
        // An operation which ignores cancellation must not hold up a replacement.
        var next = await CaptureDeadline.RunAsync(_ => Task.FromResult(42), TimeSpan.FromSeconds(1), CancellationToken.None);
        Check(next == 42, "A retired operation blocked its replacement.");
        var releasedFrame = Frame();
        late.SetResult(releasedFrame);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try { _ = releasedFrame.Pixels.Length; throw new InvalidOperationException("Late frame leaked."); }
        catch (ObjectDisposedException) { }
    }

    internal static async Task CancellationAndShutdownAreBounded()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var client = new CaptureWorkerClient();
        await ExpectAsync<OperationCanceledException>(async () =>
            await client.CaptureAsync(Request().Window, 32, 24, false, null, cancel.Token));
        var clock = Stopwatch.StartNew();
        client.Dispose();
        client.Dispose();
        Check(clock.Elapsed < TimeSpan.FromSeconds(1), "Shutdown waited for an operation gate.");
        using var service = new PreviewCaptureService();
        service.Dispose();
        await ExpectAsync<ObjectDisposedException>(async () =>
            await service.CaptureAsync(Request().Window, 32, 24));
    }

    internal static async Task WorkerProcessRoundTrip()
    {
        var name = $"StageManager_Capture_Real_Test_{Guid.NewGuid():N}";
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = SelfStart();
        foreach (var value in new[] { "--capture-worker", "--pipe", name, "--token", secret }) start.ArgumentList.Add(value);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await pipe.WaitForConnectionAsync(startup.Token);
            Check(string.Equals(worker.MainModule!.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                "Worker did not launch the same executable.");
            await CaptureWorkerProtocol.WriteMessageAsync(pipe, CaptureWorkerProtocol.Encode(writer =>
            {
                writer.Write(CaptureWorkerProtocol.Version);
                writer.Write(7L);
                writer.Write(secret);
            }), startup.Token);
            var ack = await CaptureWorkerProtocol.ReadMessageAsync(pipe, startup.Token);
            CaptureWorkerProtocol.Decode(ack, reader =>
            {
                Check(reader.ReadByte() == CaptureWorkerProtocol.Version && reader.ReadInt64() == 7, "Real worker handshake mismatch.");
                return true;
            });
            var request = Request() with { Window = Request().Window with { Handle = IntPtr.Zero }, Width = 160, Height = 100 };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await CaptureWorkerProtocol.WriteMessageAsync(pipe, CaptureWorkerProtocol.EncodeRequest(request), deadline.Token);
            using var reply = await CaptureWorkerProtocol.ReadFrameAsync(pipe, request, deadline.Token);
            Check(reply.Frame.IsPlaceholder && reply.Frame.Width == 160 && reply.Frame.Height == 100,
                "Invalid HWND did not produce a released-style placeholder in the real worker.");
            Check(worker.MainWindowHandle == IntPtr.Zero, "Capture worker created a main window.");
            pipe.Dispose();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { KillOwned(worker); }
    }

    internal static async Task OwnedHungWindowIsBounded()
    {
        // Only this owned off-screen, non-activating probe is made unresponsive.
        // It pumps normally until our private WM_APP message, then hangs forever.
        const string script = """
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -TypeDefinition @'
            using System;
            using System.Drawing;
            using System.Threading;
            using System.Windows.Forms;
            public sealed class CaptureHungProbe : Form {
                public CaptureHungProbe() {
                    Text = "StageManager owned capture hang test";
                    ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                    Location = new Point(-30000,-30000); Size = new Size(320,240);
                }
                protected override bool ShowWithoutActivation { get { return true; } }
                protected override void OnShown(EventArgs e) {
                    base.OnShown(e); Console.WriteLine("READY:" + Handle.ToInt64()); Console.Out.Flush();
                }
                protected override void WndProc(ref Message m) {
                    if (m.Msg == 0x8001) {
                        Console.WriteLine("HUNG"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite);
                    }
                    base.WndProc(ref m);
                }
            }
            '@
            [System.Windows.Forms.Application]::Run([CaptureHungProbe]::new())
            """;
        var probeStart = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) probeStart.ArgumentList.Add(arg);
        using var probe = Process.Start(probeStart) ?? throw new InvalidOperationException("Hang probe did not start.");
        var errors = probe.StandardError.ReadToEndAsync();
        using var client = new CaptureWorkerClient();
        Process? captureWorker = null;
        try
        {
            var ready = await probe.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(ready is not null && ready.StartsWith("READY:", StringComparison.Ordinal),
                "The owned hung-window probe did not report a window handle.");
            var handle = new IntPtr(long.Parse(ready![6..], System.Globalization.CultureInfo.InvariantCulture));
            var owner = CaptureWindowIdentity.Read(handle);
            Check(owner.ProcessId == (uint)probe.Id && owner.StartTime.HasValue, "Probe identity was not captured.");
            var window = new CaptureWindow(handle, "Owned hung window", "capture-probe", "", false,
                owner.ProcessId, owner.StartTime);
            Check(PostMessage(handle, 0x8001, IntPtr.Zero, IntPtr.Zero), "Could not signal owned probe.");
            Check(await probe.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)) == "HUNG",
                "The owned probe did not enter its synthetic hang.");
            var clock = Stopwatch.StartNew();
            var pending = client.CaptureAsync(window, 160, 100, false, null);
            while (client.WorkerProcessId is null && !pending.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(10);
            if (client.WorkerProcessId is { } id) captureWorker = Process.GetProcessById(id);
            Check(captureWorker is not null, "Capture worker was not observable before hung capture.");
            Check(string.Equals(captureWorker!.MainModule!.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                "Hung capture used a different executable.");
            Exception? failure = null;
            try
            {
                using var result = await pending.WaitAsync(TimeSpan.FromSeconds(6));
                Check(result.IsPlaceholder, "A hung off-screen window returned unexpected live pixels.");
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
            { failure = exception; }
            Check(clock.Elapsed < TimeSpan.FromSeconds(6), "Hung worker exceeded startup plus capture budgets.");
            if (failure is not null) await captureWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            using var recovered = await client.CaptureAsync(Request().Window with { Handle = IntPtr.Zero }, 160, 100, false, null)
                .WaitAsync(TimeSpan.FromSeconds(6));
            Check(recovered.IsPlaceholder, "The next request did not recover with a new worker.");
            if (failure is not null) Check(client.WorkerProcessId != captureWorker.Id, "A killed session was reused.");
        }
        finally
        {
            client.Dispose();
            if (captureWorker is not null) { KillOwned(captureWorker); captureWorker.Dispose(); }
            KillOwned(probe);
            _ = await errors.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    internal static async Task StalledWorkerIsKilledAndNextRequestSucceeds()
    {
        using var client = new CaptureWorkerClient();
        var window = Request().Window with { Handle = IntPtr.Zero };
        using var first = await client.CaptureAsync(window, 160, 100, false, null);
        var id = client.WorkerProcessId ?? throw new InvalidOperationException("Worker recycled before fault injection.");
        using var worker = Process.GetProcessById(id);
        Check(string.Equals(worker.MainModule!.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
            "Refusing fault injection into a process other than our own same-EXE worker.");
        try
        {
            // Freeze ONLY the handle obtained from this test's private client. No
            // process enumeration, foreign app, desktop, or input is affected.
            Check(NtSuspendProcess(worker.Handle) == 0, "Could not suspend owned worker for timeout test.");
            var clock = Stopwatch.StartNew();
            await ExpectAsync<TimeoutException>(async () =>
            {
                using var unexpected = await client.CaptureAsync(window, 160, 100, false, null);
            });
            Check(clock.Elapsed < TimeSpan.FromSeconds(3), "Live worker timeout exceeded its two-second budget plus cleanup slack.");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            using var next = await client.CaptureAsync(window, 160, 100, false, null).WaitAsync(TimeSpan.FromSeconds(6));
            Check(next.IsPlaceholder && client.WorkerProcessId != id, "Capture did not recover after killing a stalled worker.");
            Console.WriteLine($"Capture isolation live worker timeout/recovery verified ({clock.Elapsed.TotalSeconds:F2}s including restart).");
        }
        finally { KillOwned(worker); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    private static ProcessStartInfo SelfStart()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test executable.");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(CaptureIsolationRegressionTests).Assembly.Location);
        return start;
    }

    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(1000); } }
        catch (InvalidOperationException) { }
    }

    private static CaptureRequest Request() => new(7, 21,
        new CaptureWindow(new IntPtr(123), "Chinese / 中文 title", "test", "", false, 1234, 5678), 32, 24, false, "2/3");

    private static CapturedCardFrame Frame()
    {
        var bytes = ArrayPool<byte>.Shared.Rent(32 * 24 * 4);
        bytes.AsSpan(0, 32 * 24 * 4).Fill(120);
        return new CapturedCardFrame(new IntPtr(123), bytes, 32, 24, true);
    }

    private static async Task WithPipe(Func<NamedPipeServerStream, NamedPipeClientStream, Task> body)
    {
        var name = $"StageManager_Capture_Test_{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1024, 1024);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        await body(server, client);
    }

    private static async Task AssertTimeout<T>(Func<CancellationToken, Task<T>> operation)
    {
        var clock = Stopwatch.StartNew();
        await ExpectAsync<TimeoutException>(async () =>
            await CaptureDeadline.RunAsync(operation, TimeSpan.FromMilliseconds(150), CancellationToken.None,
                value => (value as IDisposable)?.Dispose()));
        Check(clock.Elapsed < TimeSpan.FromSeconds(2), "Pipe timeout was not bounded.");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
