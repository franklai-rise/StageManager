using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace StageManager.Desktop.Lifecycle;

public sealed class DiagnosticBundleOptions
{
	public string? SettingsPath { get; init; }
	public IReadOnlyList<string>? LogDirectories { get; init; }
	public string? OutputDirectory { get; init; }
	public int MaximumLogFiles { get; init; } = 5;
	public int MaximumBytesPerLogFile { get; init; } = 1_048_576;
	public bool IncludeWindowTitles { get; init; }
	public bool IncludeUserPaths { get; init; }
}

public sealed record DiagnosticBundleResult(
	string ArchivePath,
	int IncludedLogFileCount,
	IReadOnlyList<string> Warnings);

/// <summary>
/// Creates a local-only diagnostic zip. No network APIs are used. Settings and
/// log text are redacted by default before they are written to the archive.
/// </summary>
public sealed class DiagnosticBundleExporter
{
	private static readonly Regex WindowTitleRegex = new(
		@"(?im)(\b(?:window\s*)?(?:title|caption|windowtext)|窗口标题)\s*[:=]\s*[^\r\n]+",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly string[] WindowTitlePropertyMarkers =
	{
		"windowtitle", "window_title", "windowcaption", "windowtext", "caption", "窗口标题"
	};
	private static readonly string[] PathPropertyMarkers =
	{
		"path", "directory", "folder", "filename", "file_name", "路径", "目录", "文件名"
	};

	private readonly DiagnosticBundleOptions _options;
	private readonly Assembly _applicationAssembly;
	private readonly TimeProvider _timeProvider;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
	private readonly IReadOnlyList<(string Path, string Token)> _pathReplacements;

	public DiagnosticBundleExporter(
		DiagnosticBundleOptions? options = null,
		Assembly? applicationAssembly = null,
		TimeProvider? timeProvider = null)
	{
		_options = options ?? new DiagnosticBundleOptions();
		if (_options.MaximumLogFiles < 0)
			throw new ArgumentOutOfRangeException(nameof(options), "MaximumLogFiles cannot be negative.");
		if (_options.MaximumBytesPerLogFile is < 1 or > 16_777_216)
			throw new ArgumentOutOfRangeException(nameof(options), "MaximumBytesPerLogFile must be between 1 byte and 16 MiB.");

		_applicationAssembly = applicationAssembly ?? Assembly.GetEntryAssembly() ?? typeof(DiagnosticBundleExporter).Assembly;
		_timeProvider = timeProvider ?? TimeProvider.System;
		_pathReplacements = BuildPathReplacements();
	}

	/// <summary>
	/// Exports a bundle to the supplied path, or to the default Diagnostics
	/// directory when destinationPath is null. Existing files are never overwritten.
	/// </summary>
	public async Task<DiagnosticBundleResult> ExportAsync(
		string? destinationPath = null,
		CancellationToken cancellationToken = default)
	{
		var warnings = new List<string>();
		var archivePath = ResolveUniqueArchivePath(destinationPath);
		var outputDirectory = Path.GetDirectoryName(archivePath)!;
		Directory.CreateDirectory(outputDirectory);
		var temporaryPath = Path.Combine(
			outputDirectory,
			$".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.tmp");
		var includedLogs = 0;

		try
		{
			await using (var file = new FileStream(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None,
				81920,
				FileOptions.Asynchronous | FileOptions.SequentialScan))
			using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
			{
				cancellationToken.ThrowIfCancellationRequested();
				await WriteJsonEntryAsync(
					archive,
					"system-info.json",
					CreateSystemSummary(),
					cancellationToken).ConfigureAwait(false);

				await AddSanitizedSettingsAsync(archive, warnings, cancellationToken).ConfigureAwait(false);
				includedLogs = await AddRecentLogsAsync(archive, warnings, cancellationToken).ConfigureAwait(false);

				await WriteJsonEntryAsync(
					archive,
					"privacy.json",
					new
					{
						WindowTitlesIncluded = _options.IncludeWindowTitles,
						UserPathsIncluded = _options.IncludeUserPaths,
						NetworkAccess = false,
						LogFilesIncluded = includedLogs,
						Warnings = warnings.Select(SanitizeText).ToArray()
					},
					cancellationToken).ConfigureAwait(false);
			}

			File.Move(temporaryPath, archivePath);
			return new DiagnosticBundleResult(
				archivePath,
				includedLogs,
				warnings.Select(SanitizeText).ToArray());
		}
		catch
		{
			try
			{
				File.Delete(temporaryPath);
			}
			catch
			{
			}

			throw;
		}
	}

	private object CreateSystemSummary()
	{
		var assemblyName = _applicationAssembly.GetName();
		var informationalVersion = _applicationAssembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion;
		var fileVersion = _applicationAssembly
			.GetCustomAttribute<AssemblyFileVersionAttribute>()?
			.Version;
		var product = _applicationAssembly
			.GetCustomAttribute<AssemblyProductAttribute>()?
			.Product;

		return new
		{
			GeneratedUtc = _timeProvider.GetUtcNow(),
			Application = new
			{
				Product = product ?? assemblyName.Name,
				AssemblyVersion = assemblyName.Version?.ToString(),
				FileVersion = fileVersion,
				InformationalVersion = informationalVersion
			},
			OperatingSystem = new
			{
				Description = RuntimeInformation.OSDescription,
				Architecture = RuntimeInformation.OSArchitecture.ToString(),
				Version = Environment.OSVersion.VersionString,
				Is64Bit = Environment.Is64BitOperatingSystem
			},
			Runtime = new
			{
				Framework = RuntimeInformation.FrameworkDescription,
				ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
				Is64BitProcess = Environment.Is64BitProcess
			},
			Displays = Screen.AllScreens.Select((screen, index) => new
			{
				Index = index,
				Device = screen.DeviceName,
				screen.Primary,
				screen.BitsPerPixel,
				Bounds = new { screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height },
				WorkingArea = new
				{
					screen.WorkingArea.X,
					screen.WorkingArea.Y,
					screen.WorkingArea.Width,
					screen.WorkingArea.Height
				}
			}).ToArray()
		};
	}

	private async Task AddSanitizedSettingsAsync(
		ZipArchive archive,
		ICollection<string> warnings,
		CancellationToken cancellationToken)
	{
		var settingsPath = _options.SettingsPath ?? Path.Combine(GetApplicationDataRoot(), "settings.json");
		if (!File.Exists(settingsPath))
		{
			await WriteJsonEntryAsync(
				archive,
				"settings.sanitized.json",
				new { Available = false },
				cancellationToken).ConfigureAwait(false);
			return;
		}

		try
		{
			var json = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
			var root = JsonNode.Parse(json);
			SanitizeJsonNode(root, propertyName: null);
			var entry = archive.CreateEntry("settings.sanitized.json", CompressionLevel.Optimal);
			await using var stream = entry.Open();
			await JsonSerializer.SerializeAsync(stream, root, _jsonOptions, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			warnings.Add($"Settings could not be included: {exception.Message}");
			await WriteJsonEntryAsync(
				archive,
				"settings.sanitized.json",
				new { Available = false, Error = SanitizeText(exception.Message) },
				cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<int> AddRecentLogsAsync(
		ZipArchive archive,
		ICollection<string> warnings,
		CancellationToken cancellationToken)
	{
		if (_options.MaximumLogFiles == 0)
			return 0;

		var candidates = new List<FileInfo>();
		foreach (var directory in ResolveLogDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
		{
			try
			{
				if (!Directory.Exists(directory))
					continue;

				candidates.AddRange(Directory
					.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
					.Select(path => new FileInfo(path)));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				warnings.Add($"A log directory could not be read: {exception.Message}");
			}
		}

		var recentLogs = candidates
			.GroupBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.OrderByDescending(file => file.LastWriteTimeUtc)
			.Take(_options.MaximumLogFiles)
			.ToArray();

		var included = 0;
		foreach (var log in recentLogs)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var text = await ReadTailAsync(log.FullName, _options.MaximumBytesPerLogFile, cancellationToken)
					.ConfigureAwait(false);
				var entry = archive.CreateEntry($"logs/log-{included + 1:D2}.txt", CompressionLevel.Optimal);
				await using var stream = entry.Open();
				await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				await writer.WriteAsync(SanitizeText(text).AsMemory(), cancellationToken).ConfigureAwait(false);
				included++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				warnings.Add($"A recent log could not be included: {exception.Message}");
			}
		}

		return included;
	}

	private void SanitizeJsonNode(JsonNode? node, string? propertyName)
	{
		if (node is JsonObject jsonObject)
		{
			foreach (var property in jsonObject.ToArray())
			{
				if (!_options.IncludeWindowTitles && ContainsMarker(property.Key, WindowTitlePropertyMarkers))
				{
					jsonObject[property.Key] = "<redacted-window-title>";
					continue;
				}

				if (!_options.IncludeUserPaths && ContainsMarker(property.Key, PathPropertyMarkers))
				{
					jsonObject[property.Key] = "<redacted-path>";
					continue;
				}

				SanitizeJsonNode(property.Value, property.Key);
			}
		}
		else if (node is JsonArray jsonArray)
		{
			for (var index = 0; index < jsonArray.Count; index++)
				SanitizeJsonNode(jsonArray[index], propertyName);
		}
		else if (node is JsonValue value && value.TryGetValue<string>(out var text))
		{
			node.ReplaceWith(JsonValue.Create(SanitizeText(text)));
		}
	}

	private string SanitizeText(string value)
	{
		var sanitized = value;
		if (!_options.IncludeWindowTitles)
			sanitized = WindowTitleRegex.Replace(sanitized, "$1: <redacted-window-title>");

		if (!_options.IncludeUserPaths)
		{
			foreach (var replacement in _pathReplacements)
			{
				sanitized = Regex.Replace(
					sanitized,
					Regex.Escape(replacement.Path),
					replacement.Token,
					RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			}

			if (!string.IsNullOrWhiteSpace(Environment.UserName))
			{
				sanitized = Regex.Replace(
					sanitized,
					Regex.Escape(Environment.UserName),
					"<user>",
					RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			}
		}

		return sanitized;
	}

	private IReadOnlyList<string> ResolveLogDirectories()
	{
		if (_options.LogDirectories is { Count: > 0 })
			return _options.LogDirectories.Select(Path.GetFullPath).ToArray();

		var root = GetApplicationDataRoot();
		return new[]
		{
			Path.Combine(root, "Logs"),
			Path.Combine(root, "3DRenderer")
		};
	}

	private string ResolveUniqueArchivePath(string? destinationPath)
	{
		var now = _timeProvider.GetUtcNow();
		var directory = _options.OutputDirectory ?? Path.Combine(GetApplicationDataRoot(), "Diagnostics");
		var candidate = destinationPath is null
			? Path.Combine(directory, $"Stage_Manager_Lai-diagnostics-{now:yyyyMMdd-HHmmss}.zip")
			: Path.GetFullPath(destinationPath);

		if (!candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			candidate += ".zip";
		if (!File.Exists(candidate))
			return candidate;

		var candidateDirectory = Path.GetDirectoryName(candidate)!;
		var baseName = Path.GetFileNameWithoutExtension(candidate);
		for (var suffix = 2; suffix < 10_000; suffix++)
		{
			var unique = Path.Combine(candidateDirectory, $"{baseName}-{suffix}.zip");
			if (!File.Exists(unique))
				return unique;
		}

		throw new IOException("Unable to allocate a unique diagnostic bundle path.");
	}

	private async Task WriteJsonEntryAsync<T>(
		ZipArchive archive,
		string entryName,
		T value,
		CancellationToken cancellationToken)
	{
		var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
		await using var stream = entry.Open();
		await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<string> ReadTailAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		var bytesToRead = (int)Math.Min(stream.Length, maximumBytes);
		if (stream.Length > bytesToRead)
			stream.Seek(-bytesToRead, SeekOrigin.End);

		var buffer = new byte[bytesToRead];
		var totalRead = 0;
		while (totalRead < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				break;
			totalRead += read;
		}

		var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
		if (stream.Length > bytesToRead)
		{
			var firstLineBreak = text.IndexOf('\n');
			if (firstLineBreak >= 0 && firstLineBreak + 1 < text.Length)
				text = text[(firstLineBreak + 1)..];
		}

		return text;
	}

	private IReadOnlyList<(string Path, string Token)> BuildPathReplacements()
	{
		var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		AddKnownPath(replacements, Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%");
		AddKnownPath(replacements, Environment.SpecialFolder.ApplicationData, "%APPDATA%");
		AddKnownPath(replacements, Environment.SpecialFolder.DesktopDirectory, "%DESKTOP%");
		AddKnownPath(replacements, Environment.SpecialFolder.MyDocuments, "%DOCUMENTS%");
		AddKnownPath(replacements, Environment.SpecialFolder.UserProfile, "%USERPROFILE%");
		return replacements
			.OrderByDescending(pair => pair.Key.Length)
			.Select(pair => (pair.Key, pair.Value))
			.ToArray();
	}

	private static void AddKnownPath(
		IDictionary<string, string> replacements,
		Environment.SpecialFolder folder,
		string token)
	{
		var path = Environment.GetFolderPath(folder);
		if (!string.IsNullOrWhiteSpace(path))
			replacements[path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)] = token;
	}

	private static bool ContainsMarker(string propertyName, IEnumerable<string> markers)
	{
		var normalized = propertyName.Replace("-", string.Empty).Replace(" ", string.Empty);
		return markers.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
	}

	private static string GetApplicationDataRoot() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Stage_Manager_Lai");
}
