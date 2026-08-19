using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace StageManager.Card3DPrototype.Lifecycle;

public enum SingleInstanceCommand : byte
{
	ShowSidebar = 1
}

/// <summary>
/// Coordinates one application instance per Windows user session. The named
/// pipe uses <see cref="PipeOptions.CurrentUserOnly"/> and carries only a small,
/// versioned command message. Callbacks run on a background thread; a WinForms
/// caller should marshal UI work with Control.BeginInvoke.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable, IAsyncDisposable
{
	private const byte ProtocolVersion = 1;
	private const byte SuccessResponse = 0;
	private const byte FailureResponse = 1;
	private static readonly TimeSpan ConnectionOperationTimeout = TimeSpan.FromSeconds(2);

	private readonly object _sync = new();
	private readonly Mutex _mutex;
	private readonly string _pipeName;
	private CancellationTokenSource? _listenerCancellation;
	private Task? _listenerTask;
	private NamedPipeServerStream? _activeServer;
	private bool _disposed;

	public SingleInstanceCoordinator(string applicationId = "Stage_Manager_Lai_3D")
	{
		if (string.IsNullOrWhiteSpace(applicationId))
			throw new ArgumentException("An application identifier is required.", nameof(applicationId));

		var identitySuffix = CreateIdentitySuffix(applicationId.Trim());
		var safeApplicationId = SanitizeName(applicationId.Trim());
		var mutexName = $"Local\\{safeApplicationId}_{identitySuffix}";
		_pipeName = $"{safeApplicationId}_{identitySuffix}";

		_mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
		IsPrimaryInstance = createdNew;
	}

	public bool IsPrimaryInstance { get; }

	/// <summary>The most recent listener error. Transient pipe errors do not stop later commands.</summary>
	public Exception? LastListenerError { get; private set; }

	/// <summary>
	/// Starts the primary instance's cancellable command listener. The handler
	/// should post work to the UI thread and complete promptly.
	/// </summary>
	public void StartListening(Func<SingleInstanceCommand, CancellationToken, ValueTask> commandHandler)
	{
		ArgumentNullException.ThrowIfNull(commandHandler);
		lock (_sync)
		{
			ThrowIfDisposed();
			if (!IsPrimaryInstance)
				throw new InvalidOperationException("Only the primary instance can listen for commands.");
			if (_listenerTask is not null)
				throw new InvalidOperationException("The single-instance listener is already running.");

			_listenerCancellation = new CancellationTokenSource();
			_listenerTask = Task.Run(() => ListenLoopAsync(commandHandler, _listenerCancellation.Token));
		}
	}

	/// <summary>Sends ShowSidebar to the primary instance and waits for acknowledgement.</summary>
	public Task<bool> SendShowSidebarAsync(
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default) =>
		SendCommandAsync(SingleInstanceCommand.ShowSidebar, timeout, cancellationToken);

	public async Task<bool> SendCommandAsync(
		SingleInstanceCommand command,
		TimeSpan? timeout = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		if (IsPrimaryInstance)
			throw new InvalidOperationException("The primary instance cannot send a command to itself.");
		if (!Enum.IsDefined(command))
			throw new ArgumentOutOfRangeException(nameof(command));

		var effectiveTimeout = timeout ?? ConnectionOperationTimeout;
		if (effectiveTimeout <= TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
			throw new ArgumentOutOfRangeException(nameof(timeout));

		using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (effectiveTimeout != Timeout.InfiniteTimeSpan)
			timeoutCancellation.CancelAfter(effectiveTimeout);

		try
		{
			await using var client = new NamedPipeClientStream(
				".",
				_pipeName,
				PipeDirection.InOut,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);

			var request = new[] { ProtocolVersion, (byte)command };
			await client.WriteAsync(request, timeoutCancellation.Token).ConfigureAwait(false);
			await client.FlushAsync(timeoutCancellation.Token).ConfigureAwait(false);

			var response = new byte[2];
			await ReadExactlyAsync(client, response, timeoutCancellation.Token).ConfigureAwait(false);
			return response[0] == ProtocolVersion && response[1] == SuccessResponse;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	public Task StopListeningAsync()
	{
		lock (_sync)
		{
			ThrowIfDisposed();
		}

		return StopListeningCoreAsync();
	}

	public void Dispose()
	{
		DisposeAsyncCore().AsTask().GetAwaiter().GetResult();
		GC.SuppressFinalize(this);
	}

	public async ValueTask DisposeAsync()
	{
		await DisposeAsyncCore().ConfigureAwait(false);
		GC.SuppressFinalize(this);
	}

	private async Task ListenLoopAsync(
		Func<SingleInstanceCommand, CancellationToken, ValueTask> commandHandler,
		CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				LastListenerError = null;
				await AcceptOneCommandAsync(commandHandler, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (OperationCanceledException exception)
			{
				LastListenerError = exception;
			}
			catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				LastListenerError = exception;
				try
				{
					await Task.Delay(100, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
	}

	private async Task AcceptOneCommandAsync(
		Func<SingleInstanceCommand, CancellationToken, ValueTask> commandHandler,
		CancellationToken cancellationToken)
	{
		await using var server = new NamedPipeServerStream(
			_pipeName,
			PipeDirection.InOut,
			maxNumberOfServerInstances: 1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
			inBufferSize: 256,
			outBufferSize: 256);

		lock (_sync)
		{
			if (_disposed || cancellationToken.IsCancellationRequested)
				return;
			_activeServer = server;
		}

		try
		{
			await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
			using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			operationCancellation.CancelAfter(ConnectionOperationTimeout);

			var request = new byte[2];
			await ReadExactlyAsync(server, request, operationCancellation.Token).ConfigureAwait(false);
			var isValid = request[0] == ProtocolVersion &&
				Enum.IsDefined(typeof(SingleInstanceCommand), request[1]);

			var responseCode = FailureResponse;
			if (isValid)
			{
				var command = (SingleInstanceCommand)request[1];
				try
				{
					await commandHandler(command, operationCancellation.Token)
						.AsTask()
						.WaitAsync(operationCancellation.Token)
						.ConfigureAwait(false);
					responseCode = SuccessResponse;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception exception)
				{
					LastListenerError = exception;
				}
			}

			var response = new[] { ProtocolVersion, responseCode };
			await server.WriteAsync(response, operationCancellation.Token).ConfigureAwait(false);
			await server.FlushAsync(operationCancellation.Token).ConfigureAwait(false);
		}
		finally
		{
			lock (_sync)
			{
				if (ReferenceEquals(_activeServer, server))
					_activeServer = null;
			}
		}
	}

	private async Task StopListeningCoreAsync()
	{
		Task? listenerTask;
		CancellationTokenSource? listenerCancellation;
		NamedPipeServerStream? activeServer;

		lock (_sync)
		{
			listenerTask = _listenerTask;
			listenerCancellation = _listenerCancellation;
			activeServer = _activeServer;
			_listenerTask = null;
			_listenerCancellation = null;
			_activeServer = null;
		}

		if (listenerTask is null)
			return;

		listenerCancellation!.Cancel();
		if (activeServer is not null)
		{
			try
			{
				await activeServer.DisposeAsync().ConfigureAwait(false);
			}
			catch
			{
			}
		}

		try
		{
			await listenerTask.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			LastListenerError = exception;
		}
		finally
		{
			listenerCancellation.Dispose();
		}
	}

	private async ValueTask DisposeAsyncCore()
	{
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
		}

		await StopListeningCoreAsync().ConfigureAwait(false);
		_mutex.Dispose();
	}

	private static async Task ReadExactlyAsync(
		Stream stream,
		Memory<byte> destination,
		CancellationToken cancellationToken)
	{
		var bytesRead = 0;
		while (bytesRead < destination.Length)
		{
			var count = await stream.ReadAsync(destination[bytesRead..], cancellationToken).ConfigureAwait(false);
			if (count == 0)
				throw new EndOfStreamException("The single-instance pipe closed before a complete message was received.");
			bytesRead += count;
		}
	}

	private static string CreateIdentitySuffix(string applicationId)
	{
		string userIdentity;
		try
		{
			userIdentity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
		}
		catch
		{
			userIdentity = Environment.UserName;
		}

		var source = $"{applicationId}|{userIdentity}|{Process.GetCurrentProcess().SessionId}";
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
		return Convert.ToHexString(hash.AsSpan(0, 10));
	}

	private static string SanitizeName(string value)
	{
		var sanitized = new string(value
			.Select(character => char.IsLetterOrDigit(character) || character is '_' or '-'
				? character
				: '_')
			.Take(80)
			.ToArray());
		return string.IsNullOrEmpty(sanitized) ? "StageManager" : sanitized;
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}
}
