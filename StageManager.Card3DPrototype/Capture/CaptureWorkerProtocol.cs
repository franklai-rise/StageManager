using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace StageManager.Card3DPrototype;

internal enum PreviewFitMode
{
    Cover = 0,
    Contain = 1
}

internal sealed record CaptureRequest(long Generation, long RequestId, CaptureWindow Window,
    int Width, int Height, bool ApplicationCard, string CountBadge, PreviewFitMode FitMode = PreviewFitMode.Cover);

internal static class CaptureWorkerProtocol
{
    internal const byte Version = 2;
    internal const int MaximumFrameBytes = 4 * 1024 * 1024;
    internal const int MaximumMessageBytes = 32768;

    internal static byte[] Encode(Action<BinaryWriter> write)
    {
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true)) write(writer);
        if (memory.Length > MaximumMessageBytes) throw new InvalidDataException("Capture message too large.");
        return memory.ToArray();
    }

    internal static T Decode<T>(byte[] bytes, Func<BinaryReader, T> read)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(memory, Encoding.UTF8);
        var result = read(reader);
        if (memory.Position != memory.Length) throw new InvalidDataException("Trailing capture message data.");
        return result;
    }

    internal static async Task WriteMessageAsync(Stream pipe, byte[] payload, CancellationToken token)
    {
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Capture message too large.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await pipe.WriteAsync(prefix, token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadMessageAsync(Stream pipe, CancellationToken token, byte? firstPrefixByte = null)
    {
        var prefix = new byte[4];
        if (firstPrefixByte is { } first)
        {
            prefix[0] = first;
            await pipe.ReadExactlyAsync(prefix.AsMemory(1), token).ConfigureAwait(false);
        }
        else await pipe.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length < 1 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid capture message length.");
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return payload;
    }

    internal static byte[] EncodeRequest(CaptureRequest request) => Encode(writer =>
    {
        ValidateFitMode(request.FitMode);
        writer.Write(Version);
        writer.Write(request.Generation);
        writer.Write(request.RequestId);
        writer.Write(request.Window.Handle.ToInt64());
        writer.Write(request.Window.OwnerProcessId);
        writer.Write(request.Window.ProcessStartTime.HasValue);
        if (request.Window.ProcessStartTime is { } created) writer.Write(created);
        writer.Write(request.Width);
        writer.Write(request.Height);
        writer.Write(request.ApplicationCard);
        writer.Write(request.Window.IsMinimized);
        writer.Write(CaptureWindow.Limit(request.Window.Title, 4096));
        writer.Write(CaptureWindow.Limit(request.Window.ProcessName, 512));
        writer.Write(CaptureWindow.Limit(request.Window.ProcessExecutable, 4096));
        writer.Write(CaptureWindow.Limit(request.CountBadge, 128));
        writer.Write((byte)request.FitMode);
    });

    internal static CaptureRequest DecodeRequest(byte[] bytes) => Decode(bytes, reader =>
    {
        if (reader.ReadByte() != Version) throw new InvalidDataException("Capture version mismatch.");
        var generation = reader.ReadInt64();
        var requestId = reader.ReadInt64();
        var handle = new IntPtr(reader.ReadInt64());
        var owner = reader.ReadUInt32();
        long? created = reader.ReadBoolean() ? reader.ReadInt64() : null;
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        ValidateDimensions(width, height);
        var applicationCard = reader.ReadBoolean();
        var minimized = reader.ReadBoolean();
        var title = reader.ReadString();
        var processName = reader.ReadString();
        var executable = reader.ReadString();
        var badge = reader.ReadString();
        var fitMode = (PreviewFitMode)reader.ReadByte();
        ValidateFitMode(fitMode);
        if (title.Length > 4096 || processName.Length > 512 || executable.Length > 4096 || badge.Length > 128)
            throw new InvalidDataException("Capture string too long.");
        return new CaptureRequest(generation, requestId,
            new CaptureWindow(handle, title, processName, executable, minimized, owner, created), width, height, applicationCard, badge, fitMode);
    });

    internal static void ValidateFitMode(PreviewFitMode fitMode)
    {
        if (fitMode is not PreviewFitMode.Cover and not PreviewFitMode.Contain)
            throw new InvalidDataException("Invalid preview fit mode.");
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width is < 32 or > 1024 || height is < 24 or > 1024 || (long)width * height * 4 > MaximumFrameBytes)
            throw new InvalidDataException("Invalid capture dimensions.");
    }

    internal static async Task WriteFrameAsync(Stream pipe, CaptureRequest request,
        CapturedCardFrame frame, bool recycle, CancellationToken token)
    {
        var header = Encode(writer =>
        {
            writer.Write(Version);
            writer.Write(request.Generation);
            writer.Write(request.RequestId);
            writer.Write(frame.Handle.ToInt64());
            writer.Write(frame.Width);
            writer.Write(frame.Height);
            writer.Write(frame.IsPlaceholder);
            writer.Write(frame.Pixels.Length);
            writer.Write(recycle);
        });
        await WriteMessageAsync(pipe, header, token).ConfigureAwait(false);
        await pipe.WriteAsync(frame.PixelMemory, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<CaptureReply> ReadFrameAsync(Stream pipe, CaptureRequest expected, CancellationToken token)
    {
        var header = await ReadMessageAsync(pipe, token).ConfigureAwait(false);
        var info = Decode(header, reader =>
        {
            if (reader.ReadByte() != Version || reader.ReadInt64() != expected.Generation ||
                reader.ReadInt64() != expected.RequestId || reader.ReadInt64() != expected.Window.Handle.ToInt64())
                throw new InvalidDataException("Stale or mismatched capture response.");
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            ValidateDimensions(width, height);
            var placeholder = reader.ReadBoolean();
            var length = reader.ReadInt32();
            var recycle = reader.ReadBoolean();
            if (width != expected.Width || height != expected.Height || length != (long)width * height * 4)
                throw new InvalidDataException("Capture frame size mismatch.");
            return (width, height, placeholder, length, recycle);
        });
        var buffer = ArrayPool<byte>.Shared.Rent(info.length);
        try
        {
            await pipe.ReadExactlyAsync(buffer.AsMemory(0, info.length), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new CaptureReply(new CapturedCardFrame(expected.Window.Handle, buffer,
                info.width, info.height, info.placeholder), info.recycle);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}

internal sealed record CaptureReply(CapturedCardFrame Frame, bool Recycle) : IDisposable
{
    public void Dispose() => Frame.Dispose();
}
