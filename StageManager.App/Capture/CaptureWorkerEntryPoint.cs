using StageManager.Native;
using System.Diagnostics;
using System.IO.Pipes;

namespace StageManager.Desktop;

internal static class CaptureWorkerEntryPoint
{
	private const int MaximumCapturesPerWorker = 100;
	private const long MaximumPrivateBytes = 96L * 1024 * 1024;
	private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

	public static bool IsCaptureWorker(IReadOnlyList<string> args) =>
		args.Any(argument => string.Equals(argument, "--capture-worker", StringComparison.OrdinalIgnoreCase));

	public static int Run(IReadOnlyList<string> args)
	{
		try
		{
			var pipeName = GetArgument(args, "--pipe");
			var token = GetArgument(args, "--token");
			if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(token))
				return 64;
			return RunAsync(pipeName, token).GetAwaiter().GetResult();
		}
		catch
		{
			return 65;
		}
	}

	private static async Task<int> RunAsync(string pipeName, string token)
	{
		await using var pipe = new NamedPipeClientStream(
			".",
			pipeName,
			PipeDirection.InOut,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		using var connectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
		using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
		using var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
		if (reader.ReadByte() != CaptureWorkerProtocol.Version ||
			!CryptographicEquals(CaptureWorkerProtocol.ReadString(reader, 256), token))
		{
			return 66;
		}
		writer.Write(CaptureWorkerProtocol.Version);
		writer.Flush();

		using var capture = new WindowFrameCapture();
		var captureCount = 0;
		while (true)
		{
			using var idleCancellation = new CancellationTokenSource(IdleTimeout);
			var commandBuffer = new byte[1];
			try
			{
				var read = await pipe.ReadAsync(commandBuffer, idleCancellation.Token).ConfigureAwait(false);
				if (read == 0)
					return 0;
			}
			catch (OperationCanceledException)
			{
				return 0;
			}
			catch (IOException)
			{
				return 0;
			}

			if (commandBuffer[0] == CaptureWorkerProtocol.ShutdownCommand)
				return 0;
			if (commandBuffer[0] != CaptureWorkerProtocol.CaptureCommand)
				return 67;
			var requestId = reader.ReadInt64();
			var handle = new IntPtr(reader.ReadInt64());
			var width = reader.ReadInt32();
			var height = reader.ReadInt32();
			var applicationCard = reader.ReadBoolean();
			var countBadge = CaptureWorkerProtocol.ReadString(reader, 128);

			try
			{
				if (width is < 24 or > 1024 || height is < 24 or > 1024 ||
					(long)width * height * 4 > CaptureWorkerProtocol.MaximumFrameBytes)
				{
					throw new InvalidDataException("Requested capture dimensions are outside the worker limits.");
				}

				var window = new WindowsWindow(handle);
				using var frame = applicationCard
					? capture.CaptureApplicationCard(window, width, height)
					: capture.Capture(window, width, height, countBadge);
				captureCount++;
				using var process = Process.GetCurrentProcess();
				process.Refresh();
				var recycle = captureCount >= MaximumCapturesPerWorker || process.PrivateMemorySize64 > MaximumPrivateBytes;

				writer.Write(CaptureWorkerProtocol.SuccessResponse);
				writer.Write(recycle);
				writer.Write(requestId);
				writer.Write(frame.Width);
				writer.Write(frame.Height);
				writer.Write(frame.IsPlaceholder);
				writer.Write(frame.Pixels.Length);
				writer.Write(frame.Pixels);
				writer.Flush();
				if (recycle)
					return 0;
			}
			catch (Exception exception)
			{
				writer.Write(CaptureWorkerProtocol.FailureResponse);
				writer.Write(true);
				writer.Write(requestId);
				CaptureWorkerProtocol.WriteString(writer, exception.GetType().Name + ": " + exception.Message);
				writer.Flush();
				return 68;
			}
		}
	}

	private static string? GetArgument(IReadOnlyList<string> args, string name)
	{
		for (var index = 0; index + 1 < args.Count; index++)
		{
			if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
				return args[index + 1];
		}
		return null;
	}

	private static bool CryptographicEquals(string left, string right)
	{
		var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
		var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
		return leftBytes.Length == rightBytes.Length &&
			System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
	}
}
