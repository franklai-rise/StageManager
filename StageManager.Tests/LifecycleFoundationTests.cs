using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StageManager.Desktop.Lifecycle;

[TestClass]
public sealed class LifecycleFoundationTests
{
	[TestMethod]
	public void ThreeAbnormalExits_RequestSafeModeOnNextStartup()
	{
		using var temporaryDirectory = new TemporaryDirectory();
		var statePath = temporaryDirectory.GetPath("runtime-state.json");
		var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 19, 1, 0, 0, TimeSpan.Zero));

		for (var failureNumber = 1; failureNumber <= RuntimeStateService.SafeModeFailureThreshold; failureNumber++)
		{
			using var service = new RuntimeStateService(statePath, timeProvider);
			var startup = service.BeginSession("test");

			Assert.AreEqual(failureNumber - 1, startup.RecentAbnormalExitCount);
			Assert.IsFalse(startup.ShouldEnterSafeMode);
			Assert.IsTrue(service.MarkAbnormalExit());
			Assert.AreEqual(failureNumber, service.GetSnapshot().RecentAbnormalExitCount);
			timeProvider.Advance(TimeSpan.FromMinutes(1));
		}

		using var nextService = new RuntimeStateService(statePath, timeProvider);
		var nextStartup = nextService.BeginSession("test");

		Assert.IsTrue(nextStartup.ShouldEnterSafeMode);
		Assert.AreEqual(RuntimeStateService.SafeModeFailureThreshold, nextStartup.RecentAbnormalExitCount);
		Assert.IsFalse(nextStartup.AutomaticRestartAvailable);
	}

	[TestMethod]
	public void CleanExit_ResetsFailureChainAndRestartBudget()
	{
		using var temporaryDirectory = new TemporaryDirectory();
		var statePath = temporaryDirectory.GetPath("runtime-state.json");
		var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 19, 2, 0, 0, TimeSpan.Zero));

		RecordAbnormalExit(statePath, timeProvider);
		timeProvider.Advance(TimeSpan.FromMinutes(1));
		RecordAbnormalExit(statePath, timeProvider);

		using (var cleanService = new RuntimeStateService(statePath, timeProvider))
		{
			var startup = cleanService.BeginSession("test");
			Assert.AreEqual(2, startup.RecentAbnormalExitCount);
			Assert.IsTrue(cleanService.MarkCleanExit());

			var cleanSnapshot = cleanService.GetSnapshot();
			Assert.AreEqual(RuntimeSessionOutcome.Clean, cleanSnapshot.SessionOutcome);
			Assert.AreEqual(0, cleanSnapshot.RecentAbnormalExitCount);
			Assert.AreEqual(0, cleanSnapshot.AutomaticRestartCount);
			Assert.IsFalse(cleanSnapshot.ShouldEnterSafeMode);
		}

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		using var nextService = new RuntimeStateService(statePath, timeProvider);
		var nextStartup = nextService.BeginSession("test");

		Assert.AreEqual(0, nextStartup.RecentAbnormalExitCount);
		Assert.IsFalse(nextStartup.ShouldEnterSafeMode);
		Assert.IsTrue(nextStartup.AutomaticRestartAvailable);
	}

	[TestMethod]
	public void AutomaticRestart_IsLimitedToOneReservationPerFailureChain()
	{
		using var temporaryDirectory = new TemporaryDirectory();
		var statePath = temporaryDirectory.GetPath("runtime-state.json");
		var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 19, 3, 0, 0, TimeSpan.Zero));

		using (var firstService = new RuntimeStateService(statePath, timeProvider))
		{
			firstService.BeginSession("test");
			Assert.IsTrue(firstService.TryRecordAutomaticRestart());
			Assert.IsFalse(firstService.TryRecordAutomaticRestart());
			Assert.AreEqual(
				RuntimeStateService.MaximumAutomaticRestarts,
				firstService.GetSnapshot().AutomaticRestartCount);
		}

		timeProvider.Advance(TimeSpan.FromMinutes(1));
		using var restartedService = new RuntimeStateService(statePath, timeProvider);
		var restartedStartup = restartedService.BeginSession("test");

		Assert.IsFalse(restartedStartup.AutomaticRestartAvailable);
		Assert.IsFalse(restartedService.TryRecordAutomaticRestart());
		Assert.AreEqual(
			RuntimeStateService.MaximumAutomaticRestarts,
			restartedService.GetSnapshot().AutomaticRestartCount);
	}

	[TestMethod]
	public async Task SecondaryInstance_SendsShowSidebarToPrimaryInstance()
	{
		var applicationId = $"StageManager_LifecycleTests_{Guid.NewGuid():N}";
		var receivedCommand = new TaskCompletionSource<SingleInstanceCommand>(
			TaskCreationOptions.RunContinuationsAsynchronously);

		await using var primary = new SingleInstanceCoordinator(applicationId);
		Assert.IsTrue(primary.IsPrimaryInstance);
		primary.StartListening((command, _) =>
		{
			receivedCommand.TrySetResult(command);
			return ValueTask.CompletedTask;
		});

		await using var secondary = new SingleInstanceCoordinator(applicationId);
		Assert.IsFalse(secondary.IsPrimaryInstance);

		var acknowledged = await secondary.SendShowSidebarAsync(TimeSpan.FromSeconds(5));
		var command = await receivedCommand.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.IsTrue(acknowledged);
		Assert.AreEqual(SingleInstanceCommand.ShowSidebar, command);
		Assert.IsNull(primary.LastListenerError);
		await primary.StopListeningAsync();
	}

	[TestMethod]
	public async Task DiagnosticBundle_RedactsPrivateDataAndDoesNotOverwriteExistingFile()
	{
		using var temporaryDirectory = new TemporaryDirectory();
		var settingsPath = temporaryDirectory.GetPath("settings.json");
		var logDirectory = temporaryDirectory.GetPath("logs");
		var destinationPath = temporaryDirectory.GetPath("diagnostics.zip");
		Directory.CreateDirectory(logDirectory);

		const string secretWindowTitle = "Confidential quarterly plan";
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		Assert.IsFalse(string.IsNullOrWhiteSpace(userProfile));
		var secretUserPath = Path.Combine(userProfile, "StageManagerPrivate", "confidential.txt");
		var settings = new
		{
			WindowTitle = secretWindowTitle,
			RecentPath = secretUserPath,
			Description = $"Window title: {secretWindowTitle}",
			LastOpenedValue = secretUserPath
		};
		await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings));
		await File.WriteAllTextAsync(
			Path.Combine(logDirectory, "stage-manager.log"),
			$"WindowTitle={secretWindowTitle}{Environment.NewLine}OpenPath={secretUserPath}{Environment.NewLine}");

		var originalFileContents = Encoding.UTF8.GetBytes("existing diagnostic archive must remain untouched");
		await File.WriteAllBytesAsync(destinationPath, originalFileContents);

		var exporter = new DiagnosticBundleExporter(new DiagnosticBundleOptions
		{
			SettingsPath = settingsPath,
			LogDirectories = new[] { logDirectory },
			OutputDirectory = temporaryDirectory.RootPath,
			MaximumLogFiles = 1
		});
		var result = await exporter.ExportAsync(destinationPath);

		Assert.AreNotEqual(destinationPath, result.ArchivePath);
		Assert.AreEqual("diagnostics-2.zip", Path.GetFileName(result.ArchivePath));
		CollectionAssert.AreEqual(originalFileContents, await File.ReadAllBytesAsync(destinationPath));
		Assert.IsTrue(File.Exists(result.ArchivePath));
		Assert.AreEqual(1, result.IncludedLogFileCount);

		using var archive = ZipFile.OpenRead(result.ArchivePath);
		var sanitizedSettings = await ReadEntryAsync(archive, "settings.sanitized.json");
		var sanitizedLog = await ReadEntryAsync(archive, "logs/log-01.txt");
		var privacy = await ReadEntryAsync(archive, "privacy.json");

		using var sanitizedSettingsDocument = JsonDocument.Parse(sanitizedSettings);
		var sanitizedSettingsRoot = sanitizedSettingsDocument.RootElement;
		Assert.AreEqual("<redacted-window-title>", sanitizedSettingsRoot.GetProperty("WindowTitle").GetString());
		Assert.AreEqual("<redacted-path>", sanitizedSettingsRoot.GetProperty("RecentPath").GetString());
		Assert.IsFalse(
			sanitizedSettingsRoot.GetProperty("Description").GetString()!.Contains(secretWindowTitle, StringComparison.Ordinal));
		Assert.IsFalse(
			sanitizedSettingsRoot.GetProperty("LastOpenedValue").GetString()!.Contains(
				secretUserPath,
				StringComparison.OrdinalIgnoreCase));
		StringAssert.Contains(sanitizedLog, "<redacted-window-title>");
		Assert.IsFalse(sanitizedLog.Contains(secretWindowTitle, StringComparison.Ordinal));
		Assert.IsFalse(sanitizedLog.Contains(secretUserPath, StringComparison.OrdinalIgnoreCase));
		using var privacyDocument = JsonDocument.Parse(privacy);
		Assert.IsFalse(privacyDocument.RootElement.GetProperty("WindowTitlesIncluded").GetBoolean());
		Assert.IsFalse(privacyDocument.RootElement.GetProperty("UserPathsIncluded").GetBoolean());
	}

	private static void RecordAbnormalExit(string statePath, TimeProvider timeProvider)
	{
		using var service = new RuntimeStateService(statePath, timeProvider);
		service.BeginSession("test");
		Assert.IsTrue(service.MarkAbnormalExit());
	}

	private static async Task<string> ReadEntryAsync(ZipArchive archive, string entryName)
	{
		var entry = archive.GetEntry(entryName);
		Assert.IsNotNull(entry, $"The diagnostic archive did not contain '{entryName}'.");
		await using var stream = entry.Open();
		using var reader = new StreamReader(stream, Encoding.UTF8);
		return await reader.ReadToEndAsync();
	}

	private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		private DateTimeOffset _utcNow = utcNow;

		public override DateTimeOffset GetUtcNow() => _utcNow;

		public void Advance(TimeSpan interval) => _utcNow += interval;
	}

	private sealed class TemporaryDirectory : IDisposable
	{
		public TemporaryDirectory()
		{
			RootPath = Path.Combine(
				Path.GetTempPath(),
				"StageManager.Tests",
				"Lifecycle",
				Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(RootPath);
		}

		public string RootPath { get; }

		public string GetPath(string relativePath) => Path.Combine(RootPath, relativePath);

		public void Dispose()
		{
			if (!Directory.Exists(RootPath))
				return;

			Exception? lastError = null;
			for (var attempt = 1; attempt <= 5; attempt++)
			{
				try
				{
					foreach (var file in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
						File.SetAttributes(file, FileAttributes.Normal);
					Directory.Delete(RootPath, recursive: true);
					return;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					lastError = exception;
					if (attempt < 5)
						Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
				}
			}

			throw new IOException($"Could not clean temporary test directory '{RootPath}'.", lastError);
		}
	}
}
