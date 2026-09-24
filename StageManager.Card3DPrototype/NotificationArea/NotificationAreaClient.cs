using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StageManager.Card3DPrototype.NotificationArea;

internal sealed class NotificationAreaClient : IDisposable
{
	private readonly SemaphoreSlim _operationGate = new(1, 1);
	private readonly object _lifetimeLock = new();
	private readonly CancellationTokenSource _lifetime = new();
	private readonly Func<ProcessStartInfo, Process?> _startProcess;
	private int _operationCount;
	private bool _disposed;
	private bool _resourcesDisposed;
	private Task? _unresolvedWorkerExit;
	private readonly TaskCompletionSource _shutdownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public NotificationAreaClient() : this(Process.Start) { }

	internal NotificationAreaClient(Func<ProcessStartInfo, Process?> startProcess) => _startProcess = startProcess;

	public async Task<IReadOnlyList<NotificationIconSnapshot>> CaptureAsync(CancellationToken cancellationToken = default)
	{
		var result = await RunWorkerAsync(["snapshot"], cancellationToken).ConfigureAwait(false);
		if (result?.Success != true)
			return [];
		var snapshots = new List<NotificationIconSnapshot>(result.Icons.Length);
		try
		{
			foreach (var item in result.Icons)
			{
				using var stream = new MemoryStream(Convert.FromBase64String(item.PngBase64));
				using var decoded = new Bitmap(stream);
				snapshots.Add(new NotificationIconSnapshot(item.Ordinal, item.Name, new Bitmap(decoded)));
			}
			return snapshots;
		}
		catch
		{
			foreach (var snapshot in snapshots)
				snapshot.Dispose();
			return [];
		}
	}

	public async Task<bool> InvokeAsync(NotificationIconActivation activation, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(activation.Name))
			return false;
		var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(activation.Name));
		var result = await RunWorkerAsync(["invoke", activation.Ordinal.ToString(), encodedName], cancellationToken).ConfigureAwait(false);
		return result?.Success == true;
	}

	public async Task ShowNativeOverflowAsync(CancellationToken cancellationToken = default) =>
		_ = await RunWorkerAsync(["show"], cancellationToken).ConfigureAwait(false);

	public async Task<bool> ActivateApplicationAsync(string applicationName, CancellationToken cancellationToken)
	{
		var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(applicationName));
		var result = await RunWorkerAsync(["activate-app", encoded], cancellationToken).ConfigureAwait(false);
		var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "Logs");
		Directory.CreateDirectory(directory);
		File.AppendAllText(Path.Combine(directory, "activation.log"),
			$"{DateTimeOffset.Now:O} Tray worker: success={result?.Success}, error={result?.Error ?? "no result"}{Environment.NewLine}");
		return result?.Success == true;
	}

	private async Task<NotificationAreaWorkerResult?> RunWorkerAsync(string[] workerArguments, CancellationToken cancellationToken)
	{
		CancellationTokenSource request;
		lock (_lifetimeLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
			_operationCount++;
		}
		var enteredGate = false;
		try
		{
			cancellationToken = request.Token;
			await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			enteredGate = true;
			cancellationToken.ThrowIfCancellationRequested();
			if (_unresolvedWorkerExit is { IsCompleted: false }) return null;
			var executable = Environment.ProcessPath;
			if (string.IsNullOrWhiteSpace(executable))
				return null;
			var workerDirectory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Stage_Manager_Lai",
				"NotificationAreaWorker");
			Directory.CreateDirectory(workerDirectory);
			var outputPath = Path.Combine(workerDirectory, $"{Guid.NewGuid():N}.json");
			var startInfo = new ProcessStartInfo
			{
				FileName = executable,
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			};
			startInfo.ArgumentList.Add("--notification-area-worker");
			foreach (var argument in workerArguments)
				startInfo.ArgumentList.Add(argument);
			var workerExited = true;
			Process? process = null;
			try
			{
				startInfo.ArgumentList.Add("--output");
				startInfo.ArgumentList.Add(outputPath);
				Process? startedProcess;
				lock (_lifetimeLock)
				{
					if (_disposed || cancellationToken.IsCancellationRequested) return null;
					startedProcess = _startProcess(startInfo);
				}
				process = startedProcess;
				if (process is null)
					return null;
				workerExited = false;
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				// A user-requested activation also includes revealing an auto-hidden
				// taskbar and cold UI Automation startup. Snapshot polling stays short.
				timeout.CancelAfter(TimeSpan.FromSeconds(workerArguments[0] == "activate-app" ? 8 : 3));
				try
				{
					await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
					workerExited = true;
				}
				catch (OperationCanceledException)
				{
					workerExited = await TerminateWorkerAsync(process).ConfigureAwait(false);
					// Never launch overlapping workers if termination could not be
					// confirmed. Do not delete an output file still owned by one.
					return null;
				}
				catch
				{
					workerExited = await TerminateWorkerAsync(process).ConfigureAwait(false);
					return null;
				}
				if (!File.Exists(outputPath))
					return process.ExitCode == 0
						? new NotificationAreaWorkerResult(true, [])
						: null;
				var output = await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false);
				return JsonSerializer.Deserialize<NotificationAreaWorkerResult>(output);
			}
			finally
			{
				if (!workerExited && process is not null)
					_unresolvedWorkerExit = ReapUnresolvedWorkerAsync(process, outputPath);
				else
				{
					process?.Dispose();
					try { File.Delete(outputPath); } catch { }
				}
			}
		}
		catch
		{
			return null;
		}
		finally
		{
			if (enteredGate) _operationGate.Release();
			request.Dispose();
			lock (_lifetimeLock)
			{
				_operationCount--;
				if (_disposed && _operationCount == 0) DisposeResources();
			}
		}
	}

	private static async Task<bool> TerminateWorkerAsync(Process process)
	{
		try
		{
			if (!process.HasExited) process.Kill(entireProcessTree: true);
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			return true;
		}
		catch { return false; }
	}

	private static async Task ReapUnresolvedWorkerAsync(Process process, string outputPath)
	{
		// Retain the handle and file ownership if Windows did not confirm a kill.
		// New requests remain blocked only until this particular worker exits.
		while (true)
		{
			try
			{
				await process.WaitForExitAsync().ConfigureAwait(false);
				break;
			}
			catch { await Task.Delay(1000).ConfigureAwait(false); }
		}
		process.Dispose();
		try { File.Delete(outputPath); } catch { }
	}

	public async Task ShutdownAsync()
	{
		Dispose();
		await _shutdownCompleted.Task.ConfigureAwait(false);
		if (_unresolvedWorkerExit is { } pending) await pending.ConfigureAwait(false);
	}

	public void Dispose()
	{
		lock (_lifetimeLock)
		{
			if (_disposed) return;
			_disposed = true;
			_lifetime.Cancel();
			if (_operationCount == 0) DisposeResources();
		}
	}

	private void DisposeResources()
	{
		if (_resourcesDisposed) return;
		_resourcesDisposed = true;
		_lifetime.Dispose();
		_operationGate.Dispose();
		_shutdownCompleted.TrySetResult();
	}
}
