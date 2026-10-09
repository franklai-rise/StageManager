using System.Buffers;
using System.Text;

namespace StageManager.Desktop;

internal static class CaptureWorkerProtocol
{
	public const byte Version = 1;
	public const byte CaptureCommand = 1;
	public const byte ShutdownCommand = 2;
	public const byte SuccessResponse = 1;
	public const byte FailureResponse = 0;
	public const int MaximumFrameBytes = 4 * 1024 * 1024;

	public static void WriteString(BinaryWriter writer, string? value)
	{
		var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
		writer.Write(bytes.Length);
		writer.Write(bytes);
	}

	public static string ReadString(BinaryReader reader, int maximumBytes = 16_384)
	{
		var length = reader.ReadInt32();
		if (length < 0 || length > maximumBytes)
			throw new InvalidDataException("The capture worker sent an invalid string length.");
		return Encoding.UTF8.GetString(ReadExactly(reader, length));
	}

	public static byte[] ReadExactly(BinaryReader reader, int length)
	{
		if (length < 0)
			throw new ArgumentOutOfRangeException(nameof(length));
		var buffer = new byte[length];
		var offset = 0;
		while (offset < length)
		{
			var read = reader.Read(buffer, offset, length - offset);
			if (read == 0)
				throw new EndOfStreamException("The capture pipe closed before a complete message was received.");
			offset += read;
		}
		return buffer;
	}

	public static CapturedCardFrame ReadFrame(
		BinaryReader reader,
		IntPtr expectedHandle,
		long expectedRequestId,
		out bool recycleWorker)
	{
		var status = reader.ReadByte();
		recycleWorker = reader.ReadBoolean();
		var requestId = reader.ReadInt64();
		if (requestId != expectedRequestId)
			throw new InvalidDataException("The capture worker response did not match the request.");
		if (status != SuccessResponse)
			throw new InvalidOperationException(ReadString(reader));

		var width = reader.ReadInt32();
		var height = reader.ReadInt32();
		var placeholder = reader.ReadBoolean();
		var length = reader.ReadInt32();
		var expectedLength = checked(width * height * 4);
		if (width < 1 || height < 1 || length != expectedLength || length > MaximumFrameBytes)
			throw new InvalidDataException("The capture worker returned an invalid frame size.");

		var rented = ArrayPool<byte>.Shared.Rent(length);
		try
		{
			var offset = 0;
			while (offset < length)
			{
				var read = reader.Read(rented, offset, length - offset);
				if (read == 0)
					throw new EndOfStreamException("The capture worker returned an incomplete frame.");
				offset += read;
			}
			return new CapturedCardFrame(expectedHandle, rented, width, height, placeholder);
		}
		catch
		{
			ArrayPool<byte>.Shared.Return(rented);
			throw;
		}
	}
}
