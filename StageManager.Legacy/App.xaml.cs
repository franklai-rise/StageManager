using StageManager.Infrastructure;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace StageManager;

public partial class App : Application
{
	private Mutex? _singleInstanceMutex;
	private bool _ownsSingleInstanceMutex;

	protected override void OnStartup(StartupEventArgs e)
	{
		_singleInstanceMutex = new Mutex(true, "Stage_Manager_Lai_SingleInstance", out var isFirstInstance);
		_ownsSingleInstanceMutex = isFirstInstance;
		if (!isFirstInstance)
		{
			Shutdown();
			return;
		}

		AppServices.Initialize();
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		base.OnStartup(e);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		try
		{
			AppServices.StartupGuard?.MarkCleanExit();
			AppLogger.Info("Stage_Manager_Lai exited cleanly.");
		}
		finally
		{
			if (_ownsSingleInstanceMutex)
				_singleInstanceMutex?.ReleaseMutex();
			_singleInstanceMutex?.Dispose();
		}
		base.OnExit(e);
	}

	private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		AppLogger.Error("Unhandled UI exception.", e.Exception);
		e.Handled = true;
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		AppLogger.Error("Unhandled application exception.", e.ExceptionObject as Exception);
	}

	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		AppLogger.Error("Unobserved background task exception.", e.Exception);
		e.SetObserved();
	}
}
