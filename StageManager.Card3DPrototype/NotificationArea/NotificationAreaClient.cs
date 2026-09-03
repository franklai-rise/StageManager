using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StageManager.Card3DPrototype.NotificationArea;

internal sealed class NotificationAreaClient : IDisposable
{
	private readonly SemaphoreSlim _operationGate = new(1, 1);
	private bool _disposed;

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

	private async Task<NotificationAreaWorkerResult?> RunWorkerAsync(string[] workerArguments, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
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
			try
			{
				startInfo.ArgumentList.Add("--output");
				startInfo.ArgumentList.Add(outputPath);
				using var process = Process.Start(startInfo);
				if (process is null)
					return null;
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				timeout.CancelAfter(TimeSpan.FromSeconds(3));
				try
				{
					await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					try { process.Kill(entireProcessTree: true); }
					catch { }
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
				try { File.Delete(outputPath); }
				catch { }
			}
		}
		catch
		{
			return null;
		}
		finally
		{
			_operationGate.Release();
		}
	}

	public void Dispose()
	{
		_disposed = true;
	}
}
