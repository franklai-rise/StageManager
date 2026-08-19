using StageManager.Card3DPrototype.Lifecycle;
using StageManager.Infrastructure;
using System.Runtime.Versioning;

namespace StageManager.Card3DPrototype;

internal static class Program
{
	[STAThread]
	[SupportedOSPlatform("windows10.0.19041.0")]
	private static int Main()
	{
		Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
		Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);

		AppLogger.Initialize();
		AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
		{
			if (eventArgs.ExceptionObject is Exception exception)
				AppLogger.Error("An unhandled AppDomain exception occurred.", exception);
		};
		TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
		{
			AppLogger.Error("An unobserved background task failed.", eventArgs.Exception);
			eventArgs.SetObserved();
		};

		using var host = new ApplicationHost();
		try
		{
			return host.Run();
		}
		catch (Exception exception)
		{
			host.RecordFatalFailure(exception);
			return 1;
		}
	}
}
