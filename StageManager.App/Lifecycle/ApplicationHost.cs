using StageManager.Infrastructure;
using System.Diagnostics;

namespace StageManager.Desktop.Lifecycle;

/// <summary>
/// Owns application-wide startup and shutdown. The visible shell owns its UI
/// and Composition resources; this host owns runtime state and IPC and releases
/// them in reverse startup order.
/// </summary>
internal sealed class ApplicationHost : IDisposable
{
	private readonly SingleInstanceCoordinator _singleInstance = new("Stage_Manager_Lai");
	private RuntimeStateService? _runtimeState;
	private RuntimeStartupDecision? _startupDecision;
	private Form? _shell;
	private int _pendingShowRequest;
	private bool _restartNormally;
	private bool _disposed;

	public int Run()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_singleInstance.IsPrimaryInstance)
		{
			var delivered = _singleInstance.SendShowSidebarAsync().GetAwaiter().GetResult();
			if (!delivered)
				AppLogger.Warn("A second instance could not contact the running instance.");
			return delivered ? 0 : 2;
		}

		_runtimeState = new RuntimeStateService();
		_startupDecision = _runtimeState.BeginSession(AppVersionInfo.InformationalVersion);
		if (!_startupDecision.StatePersisted && _runtimeState.LastPersistenceError is { } stateError)
			AppLogger.Warn($"Runtime state could not be persisted: {stateError.Message}");

		_shell = CreateShell(_startupDecision);
		_singleInstance.StartListening(HandleExternalCommandAsync);
		if (Interlocked.Exchange(ref _pendingShowRequest, 0) != 0)
			PostShowRequest();

		try
		{
			Application.Run(_shell);
			_runtimeState.MarkCleanExit();
			if (_restartNormally)
				StartFreshProcess();
			return 0;
		}
		finally
		{
			_singleInstance.StopListeningAsync().GetAwaiter().GetResult();
			_shell.Dispose();
			_shell = null;
		}
	}

	public void RecordFatalFailure(Exception exception)
	{
		AppLogger.Error("Stage_Manager_Lai stopped because of an unhandled error.", exception);
		WriteLastError(exception);
		if (_runtimeState is null)
			return;

		try
		{
			_runtimeState.MarkAbnormalExit();
			if (_startupDecision?.AutomaticRestartAvailable == true &&
				_runtimeState.TryRecordAutomaticRestart())
			{
				StartFreshProcess();
			}
		}
		catch (Exception stateException)
		{
			AppLogger.Error("The crash state could not be updated.", stateException);
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;

		try
		{
			_singleInstance.Dispose();
		}
		finally
		{
			_runtimeState?.Dispose();
			_runtimeState = null;
		}
	}

	private Form CreateShell(RuntimeStartupDecision decision)
	{
		if (!decision.ShouldEnterSafeMode)
			return new PrototypeForm();

		AppLogger.Warn($"Safe mode was activated after {decision.RecentAbnormalExitCount} recent abnormal starts.");
		var safeMode = new SafeModeForm(decision.RecentAbnormalExitCount);
		safeMode.RestartNormallyRequested += (_, _) =>
		{
			_restartNormally = true;
			safeMode.Close();
		};
		return safeMode;
	}

	private ValueTask HandleExternalCommandAsync(
		SingleInstanceCommand command,
		CancellationToken cancellationToken)
	{
		if (command == SingleInstanceCommand.ShowSidebar && !cancellationToken.IsCancellationRequested)
			PostShowRequest();
		return ValueTask.CompletedTask;
	}

	private void PostShowRequest()
	{
		var shell = _shell;
		if (shell is null || shell.IsDisposed || !shell.IsHandleCreated)
		{
			Interlocked.Exchange(ref _pendingShowRequest, 1);
			return;
		}

		try
		{
			shell.BeginInvoke(new Action(() =>
			{
				if (shell is PrototypeForm mainForm)
					mainForm.ShowSidebarFromExternalCommand();
				else if (shell is SafeModeForm safeModeForm)
					safeModeForm.ShowSafeModePanel();
			}));
		}
		catch (InvalidOperationException)
		{
			Interlocked.Exchange(ref _pendingShowRequest, 1);
		}
	}

	private static void StartFreshProcess()
	{
		var executable = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(executable))
			return;

		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = executable,
				UseShellExecute = true,
				WorkingDirectory = AppContext.BaseDirectory
			});
		}
		catch (Exception exception)
		{
			AppLogger.Error("Stage_Manager_Lai could not restart automatically.", exception);
		}
	}

	private static void WriteLastError(Exception exception)
	{
		try
		{
			var directory = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Stage_Manager_Lai",
				"3DRenderer");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "last-error.log"), exception.ToString());
		}
		catch
		{
		}
	}
}
