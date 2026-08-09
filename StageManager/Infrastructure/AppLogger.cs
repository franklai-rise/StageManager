using System;
using System.IO;
using System.Linq;

namespace StageManager.Infrastructure;

internal static class AppLogger
{
	private static readonly object Sync = new();
	private static readonly string LogDirectory = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Stage_Manager_Lai",
		"Logs");

	public static string CurrentLogPath => Path.Combine(LogDirectory, $"stage-manager-{DateTime.Now:yyyy-MM-dd}.log");

	public static void Initialize()
	{
		try
		{
			Directory.CreateDirectory(LogDirectory);
			foreach (var staleLog in Directory.EnumerateFiles(LogDirectory, "stage-manager-*.log")
				.Select(path => new FileInfo(path))
				.Where(file => file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)))
			{
				staleLog.Delete();
			}
		}
		catch
		{
			// Logging must never be able to stop the application from starting.
		}
	}

	public static void Info(string message) => Write("INFO", message, null);

	public static void Warn(string message) => Write("WARN", message, null);

	public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

	private static void Write(string level, string message, Exception? exception)
	{
		try
		{
			lock (Sync)
			{
				Directory.CreateDirectory(LogDirectory);
				var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
				File.AppendAllText(CurrentLogPath, $"{DateTime.Now:O} [{level}] {message}{detail}{Environment.NewLine}");
			}
		}
		catch
		{
		}
	}
}
