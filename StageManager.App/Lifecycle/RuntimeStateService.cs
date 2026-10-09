using System.Text.Json;
using System.Text.Json.Serialization;

namespace StageManager.Desktop.Lifecycle;

/// <summary>
/// Describes the persisted outcome of the most recent application session.
/// </summary>
public enum RuntimeSessionOutcome
{
	None,
	Running,
	Clean,
	Abnormal
}

/// <summary>
/// The decision returned when a new application session is registered.
/// </summary>
public sealed record RuntimeStartupDecision(
	Guid SessionId,
	bool ShouldEnterSafeMode,
	int RecentAbnormalExitCount,
	bool AutomaticRestartAvailable,
	bool StatePersisted);

/// <summary>
/// A read-only view of the current runtime state.
/// </summary>
public sealed record RuntimeStateSnapshot(
	int SchemaVersion,
	Guid? SessionId,
	RuntimeSessionOutcome SessionOutcome,
	DateTimeOffset? SessionStartedUtc,
	DateTimeOffset UpdatedUtc,
	int RecentAbnormalExitCount,
	bool ShouldEnterSafeMode,
	int AutomaticRestartCount);

/// <summary>
/// Persists crash-loop state without depending on the UI or logger.
/// Call <see cref="BeginSession"/> once at startup and
/// <see cref="MarkCleanExit"/> only after the message loop exits normally.
/// Disposing an uncompleted service deliberately leaves the session marked as
/// running so that the next launch treats it as an abnormal exit.
/// </summary>
public sealed class RuntimeStateService : IDisposable
{
	public const int CurrentSchemaVersion = 1;
	public const int SafeModeFailureThreshold = 3;
	public const int MaximumAutomaticRestarts = 1;
	public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

	private readonly object _sync = new();
	private readonly TimeProvider _timeProvider;
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};
	private RuntimeStateDocument _state = new();
	private bool _sessionBegun;
	private bool _disposed;

	public RuntimeStateService(string? statePath = null, TimeProvider? timeProvider = null)
	{
		var defaultDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Stage_Manager_Lai");
		StatePath = Path.GetFullPath(statePath ?? Path.Combine(defaultDirectory, "runtime-state.json"));
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <summary>The runtime-state.json path used by this instance.</summary>
	public string StatePath { get; }

	/// <summary>The most recent persistence error, if state storage was unavailable.</summary>
	public Exception? LastPersistenceError { get; private set; }

	/// <summary>
	/// Registers a new run and detects whether the previous run ended without a
	/// clean or explicit abnormal-exit marker.
	/// </summary>
	public RuntimeStartupDecision BeginSession(string? applicationVersion = null)
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			if (_sessionBegun)
				throw new InvalidOperationException("A runtime session has already been started by this service.");

			var now = _timeProvider.GetUtcNow();
			_state = LoadState();
			NormalizeLoadedState(now);

			if (_state.SessionOutcome == RuntimeSessionOutcome.Clean)
			{
				ResetFailureChain();
			}
			else if (_state.SessionOutcome == RuntimeSessionOutcome.Running)
			{
				RecordAbnormalExitCore(now);
			}
			else if (_state.SessionOutcome == RuntimeSessionOutcome.Abnormal &&
				_state.AbnormalExitUtc.Count == 0)
			{
				// An expired failure chain starts with a fresh restart budget.
				_state.AutomaticRestartCount = 0;
			}

			_state.SessionId = Guid.NewGuid();
			_state.ApplicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
				? null
				: applicationVersion.Trim();
			_state.SessionOutcome = RuntimeSessionOutcome.Running;
			_state.SessionStartedUtc = now;
			_state.SafeModeRequested = _state.AbnormalExitUtc.Count >= SafeModeFailureThreshold;
			_state.UpdatedUtc = now;
			_sessionBegun = true;

			var persisted = TrySaveState();
			return new RuntimeStartupDecision(
				_state.SessionId.Value,
				_state.SafeModeRequested,
				_state.AbnormalExitUtc.Count,
				!_state.SafeModeRequested && _state.AutomaticRestartCount < MaximumAutomaticRestarts,
				persisted);
		}
	}

	/// <summary>Marks the active run as successfully completed and resets the crash chain.</summary>
	public bool MarkCleanExit()
	{
		lock (_sync)
		{
			EnsureActiveSession();
			_state.SessionOutcome = RuntimeSessionOutcome.Clean;
			_state.UpdatedUtc = _timeProvider.GetUtcNow();
			ResetFailureChain();
			return TrySaveState();
		}
	}

	/// <summary>Records a caught fatal failure. Repeated calls for the same run are idempotent.</summary>
	public bool MarkAbnormalExit()
	{
		lock (_sync)
		{
			EnsureActiveSession();
			if (_state.SessionOutcome == RuntimeSessionOutcome.Running)
				RecordAbnormalExitCore(_timeProvider.GetUtcNow());

			return TrySaveState();
		}
	}

	/// <summary>
	/// Atomically reserves the single automatic-restart allowance for the current
	/// ten-minute failure chain. A failure is recorded first if necessary. The
	/// method returns true only when the reservation was persisted successfully.
	/// </summary>
	public bool TryRecordAutomaticRestart()
	{
		lock (_sync)
		{
			EnsureActiveSession();
			if (_state.SessionOutcome == RuntimeSessionOutcome.Running)
				RecordAbnormalExitCore(_timeProvider.GetUtcNow());

			if (_state.SafeModeRequested || _state.AutomaticRestartCount >= MaximumAutomaticRestarts)
				return false;

			_state.AutomaticRestartCount++;
			_state.UpdatedUtc = _timeProvider.GetUtcNow();
			if (TrySaveState())
				return true;

			// Never authorize a restart that could not be durably accounted for.
			return false;
		}
	}

	public RuntimeStateSnapshot GetSnapshot()
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			return CreateSnapshot();
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			_disposed = true;
		}
	}

	private RuntimeStateDocument LoadState()
	{
		LastPersistenceError = null;
		if (!File.Exists(StatePath))
			return new RuntimeStateDocument();

		try
		{
			using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			var loaded = JsonSerializer.Deserialize<RuntimeStateDocument>(stream, _jsonOptions);
			if (loaded is null || loaded.SchemaVersion != CurrentSchemaVersion)
				throw new InvalidDataException($"Unsupported runtime-state schema: {loaded?.SchemaVersion.ToString() ?? "null"}.");

			return loaded;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
		{
			LastPersistenceError = exception;
			QuarantineUnreadableState();
			return new RuntimeStateDocument();
		}
	}

	private void NormalizeLoadedState(DateTimeOffset now)
	{
		_state.SchemaVersion = CurrentSchemaVersion;
		_state.AbnormalExitUtc ??= new List<DateTimeOffset>();
		PruneFailureWindow(now);
		_state.AutomaticRestartCount = Math.Clamp(
			_state.AutomaticRestartCount,
			0,
			MaximumAutomaticRestarts);
	}

	private void RecordAbnormalExitCore(DateTimeOffset now)
	{
		PruneFailureWindow(now);
		if (_state.AbnormalExitUtc.Count == 0)
			_state.AutomaticRestartCount = 0;

		_state.AbnormalExitUtc.Add(now);
		_state.SessionOutcome = RuntimeSessionOutcome.Abnormal;
		_state.SafeModeRequested = _state.AbnormalExitUtc.Count >= SafeModeFailureThreshold;
		_state.UpdatedUtc = now;
	}

	private void PruneFailureWindow(DateTimeOffset now)
	{
		var cutoff = now - FailureWindow;
		var maximumReasonableFutureTime = now + TimeSpan.FromMinutes(1);
		_state.AbnormalExitUtc.RemoveAll(timestamp =>
			timestamp < cutoff || timestamp > maximumReasonableFutureTime);
		_state.AbnormalExitUtc.Sort();
	}

	private void ResetFailureChain()
	{
		_state.AbnormalExitUtc.Clear();
		_state.AutomaticRestartCount = 0;
		_state.SafeModeRequested = false;
	}

	private bool TrySaveState()
	{
		string? temporaryPath = null;
		try
		{
			var directory = Path.GetDirectoryName(StatePath)
				?? throw new InvalidOperationException("The runtime state path must include a directory.");
			Directory.CreateDirectory(directory);
			temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(StatePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

			using (var stream = new FileStream(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.WriteThrough))
			{
				JsonSerializer.Serialize(stream, _state, _jsonOptions);
				stream.Flush(flushToDisk: true);
			}

			File.Move(temporaryPath, StatePath, overwrite: true);
			temporaryPath = null;
			LastPersistenceError = null;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			LastPersistenceError = exception;
			return false;
		}
		finally
		{
			if (temporaryPath is not null)
			{
				try
				{
					File.Delete(temporaryPath);
				}
				catch
				{
				}
			}
		}
	}

	private void QuarantineUnreadableState()
	{
		try
		{
			if (!File.Exists(StatePath))
				return;

			var directory = Path.GetDirectoryName(StatePath)!;
			var quarantinePath = Path.Combine(
				directory,
				$"runtime-state.invalid-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
			File.Move(StatePath, quarantinePath);
		}
		catch
		{
			// State recovery must never prevent the application from starting.
		}
	}

	private RuntimeStateSnapshot CreateSnapshot() => new(
		_state.SchemaVersion,
		_state.SessionId,
		_state.SessionOutcome,
		_state.SessionStartedUtc,
		_state.UpdatedUtc,
		_state.AbnormalExitUtc.Count,
		_state.SafeModeRequested,
		_state.AutomaticRestartCount);

	private void EnsureActiveSession()
	{
		ThrowIfDisposed();
		if (!_sessionBegun)
			throw new InvalidOperationException("BeginSession must be called first.");
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}

	private sealed class RuntimeStateDocument
	{
		public int SchemaVersion { get; set; } = CurrentSchemaVersion;
		public Guid? SessionId { get; set; }
		public string? ApplicationVersion { get; set; }
		public RuntimeSessionOutcome SessionOutcome { get; set; } = RuntimeSessionOutcome.None;
		public DateTimeOffset? SessionStartedUtc { get; set; }
		public DateTimeOffset UpdatedUtc { get; set; }
		public List<DateTimeOffset> AbnormalExitUtc { get; set; } = new();
		public bool SafeModeRequested { get; set; }
		public int AutomaticRestartCount { get; set; }
	}
}
