using System.Runtime.Versioning;
using System.Windows.Forms;

namespace StageManager.Card3DPrototype;

internal static class Program
{
	[STAThread]
	[SupportedOSPlatform("windows10.0.19041.0")]
	private static void Main(string[] args)
	{
		Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
		if (args.Length > 0 && string.Equals(args[0], "--notification-area-worker", StringComparison.Ordinal))
		{
			Environment.ExitCode = NotificationArea.NotificationAreaWorker.Run(args.Skip(1).ToArray());
			return;
		}

		using var singleInstance = new Mutex(true, "Stage_Manager_Lai_3D_SingleInstance", out var isFirstInstance);
		if (!isFirstInstance)
			return;

		try
		{
			Application.ThreadException += (_, eventArgs) => WriteError(eventArgs.Exception);
			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			using var form = new PrototypeForm();
			try { Application.Run(form); }
			finally
			{
				// The UI is already closed. Give owned notification workers time to
				// terminate before this process exits; never block a live UI thread.
				if (!form.WaitForBackgroundShutdownAsync().Wait(TimeSpan.FromSeconds(6)))
					WriteError(new TimeoutException("Notification worker shutdown could not be confirmed within six seconds."));
			}
		}
		catch (Exception exception)
		{
			WriteError(exception);
		}
		finally
		{
			singleInstance.ReleaseMutex();
		}
	}

	private static void WriteError(Exception exception)
	{
		var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "3DRenderer");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "last-error.log"), exception.ToString());
	}
}
