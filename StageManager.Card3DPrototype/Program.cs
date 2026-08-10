using System.Runtime.Versioning;
using System.Windows.Forms;

namespace StageManager.Card3DPrototype;

internal static class Program
{
	[STAThread]
	[SupportedOSPlatform("windows10.0.19041.0")]
	private static void Main()
	{
		using var singleInstance = new Mutex(true, "Stage_Manager_Lai_3D_SingleInstance", out var isFirstInstance);
		if (!isFirstInstance)
			return;

		try
		{
			Application.ThreadException += (_, eventArgs) => WriteError(eventArgs.Exception);
			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
			Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			using var form = new PrototypeForm();
			Application.Run(form);
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
