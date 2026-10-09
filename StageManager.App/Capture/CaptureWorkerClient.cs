using StageManager.Infrastructure;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace StageManager.Desktop;

internal sealed class CaptureWorkerClient : IDisposable
{
	private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(2);
	private readonly SemaphoreSlim _operationGate = new(1, 1);
	private NamedPipeServerStream? _pipe;
	private BinaryReader? _reader;
	private BinaryWriter? _writer;
	private Process? _process;
	private long _nextRequestId;
	private bool _disposed;

	public async Task<CapturedCardFrame> CaptureAsync(
		IntPtr handle,
		int width,
		int height,
		bool applicationCard,
		string? countBadge,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
			var requestId = Interlocked.Increment(ref _nextRequestId);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(CaptureTimeout);
			try
			{
				var operation = Task.Run(() => CaptureCore(
					requestId,
					handle,
					width,
					height,
					applicationCard,
					countBadge), CancellationToken.None);
				return await operation.WaitAsync(timeout.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				AppLogger.Warn($"Preview capture for window {handle} exceeded the two-second worker limit.");
				ResetWorker(kill: true);
				throw new TimeoutException("The capture worker exceeded its two-second limit.");
			}
			catch
			{
				ResetWorker(kill: true);
				throw;
			}
		}
		finally
		{
			_operationGate.Release();
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_operationGate.Wait();
		try
		{
			ResetWorker(kill: true);
		}
		finally
		{
			_operationGate.Release();
			_operationGate.Dispose();
		}
	}

	private CapturedCardFrame CaptureCore(
		long requestId,
		IntPtr handle,
		int width,
		int height,
		bool applicationCard,
		string? countBadge)
	{
		var writer = _writer ?? throw new InvalidOperationException("The capture worker is not connected.");
		var reader = _reader ?? throw new InvalidOperationException("The capture worker is not connected.");
		writer.Write(CaptureWorkerProtocol.CaptureCommand);
		writer.Write(requestId);
		writer.Write(handle.ToInt64());
		writer.Write(width);
		writer.Write(height);
		writer.Write(applicationCard);
		CaptureWorkerProtocol.WriteString(writer, countBadge);
		writer.Flush();

		var frame = CaptureWorkerProtocol.ReadFrame(reader, handle, requestId, out var recycleWorker);
		if (recycleWorker)
			ResetWorker(kill: false);
		return frame;
	}

	private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
	{
		if (_pipe?.IsConnected == true && _process is { HasExited: false })
			return;
		ResetWorker(kill: true);

		var executable = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(executable))
			throw new InvalidOperationException("The current executable path is unavailable.");
		var pipeName = $"Stage_Manager_Lai_Capture_{Environment.ProcessId}_{Guid.NewGuid():N}";
		var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
		_pipe = new NamedPipeServerStream(
			pipeName,
			PipeDirection.InOut,
			1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
			64 * 1024,
			64 * 1024);

		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
			WorkingDirectory = AppContext.BaseDirectory
		};
		startInfo.ArgumentList.Add("--capture-worker");
		startInfo.ArgumentList.Add("--pipe");
		startInfo.ArgumentList.Add(pipeName);
		startInfo.ArgumentList.Add("--token");
		startInfo.ArgumentList.Add(token);
		_process = Process.Start(startInfo) ?? throw new InvalidOperationException("The capture worker could not be started.");

		using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		startupCancellation.CancelAfter(StartupTimeout);
		try
		{
			await _pipe.WaitForConnectionAsync(startupCancellation.Token).ConfigureAwait(false);
			_reader = new BinaryReader(_pipe, System.Text.Encoding.UTF8, leaveOpen: true);
			_writer = new BinaryWriter(_pipe, System.Text.Encoding.UTF8, leaveOpen: true);
			_writer.Write(CaptureWorkerProtocol.Version);
			CaptureWorkerProtocol.WriteString(_writer, token);
			_writer.Flush();
			if (_reader.ReadByte() != CaptureWorkerProtocol.Version)
				throw new InvalidDataException("The capture worker handshake failed.");
		}
		catch
		{
			ResetWorker(kill: true);
			throw;
		}
	}

	private void ResetWorker(bool kill)
	{
		try { _writer?.Dispose(); } catch { }
		try { _reader?.Dispose(); } catch { }
		try { _pipe?.Dispose(); } catch { }
		_writer = null;
		_reader = null;
		_pipe = null;

		var process = _process;
		_process = null;
		if (process is null)
			return;
		try
		{
			if (kill && !process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch
		{
		}
		finally
		{
			process.Dispose();
		}
	}
}
