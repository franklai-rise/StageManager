using StageManager.Settings;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StageManager.Card3DPrototype.AiUsage;

internal sealed class AiUsageService : IDisposable
{
	private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
	private readonly AiUsageCredentialStore _credentials;
	private readonly IReadOnlyList<IAiUsageProvider> _providers;
	private readonly Dictionary<string, SemaphoreSlim> _providerGates;
	private readonly string _historyPath;
	private readonly object _gate = new(), _historyGate = new();
	private readonly List<AiUsagePoint> _points = new();
	private readonly CancellationTokenSource _lifetime = new();
	private CancellationTokenSource _session = new();
	private readonly Func<DateTimeOffset> _clock;
	private Task _refreshTask = Task.CompletedTask;
	private readonly List<Task> _tests = new();
	private IReadOnlyList<AiUsageSnapshot> _current = Array.Empty<AiUsageSnapshot>();
	private AppSettings _settings = new();
	private string? _configuration;
	private int _generation, _runGeneration = -1;
	private DateTimeOffset _lastAutomatic, _lastManual;
	private bool _disposed, _historyLoaded;

	public AiUsageService(IReadOnlyList<IAiUsageProvider>? providers = null, string? dataDirectory = null, Func<DateTimeOffset>? clock = null)
	{
		var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai");
		_credentials = new AiUsageCredentialStore(directory);
		_historyPath = Path.Combine(directory, "ai-usage-history.json");
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
		_providers = providers ?? new IAiUsageProvider[]
		{
			new Provider("codex", s => s.AiUsageCodexEnabled, (s, token) => CodexUsageClient.ReadAsync(s.CodexCliPath, token)),
			new Provider("deepseek", s => s.AiUsageDeepSeekEnabled, (_, token) => ReadBalanceAsync("deepseek", _credentials.Get("deepseek"), false, token)),
			new Provider("gemini", s => s.AiUsageGeminiEnabled, (_, _) => Task.FromResult(GeminiBrowserUsage.Read(directory))),
			new Provider("kimi", s => s.AiUsageKimiEnabled, (s, token) => ReadBalanceAsync("kimi", _credentials.Get("kimi"), s.AiUsageKimiInternational, token))
		};
		_providerGates = _providers.ToDictionary(p => p.ProviderId, _ => new SemaphoreSlim(1, 1));
	}

	public IReadOnlyList<AiUsageSnapshot> Current => Volatile.Read(ref _current);
	public event EventHandler? Updated;
	public bool Configure(AppSettings settings)
	{
		lock (_gate)
		{
			if (_disposed) return false;
			var key = $"{settings.ShowAiUsageCard}:{settings.AiUsageEnabled}:{settings.AiUsageCodexEnabled}:{settings.AiUsageDeepSeekEnabled}:{settings.AiUsageGeminiEnabled}:{settings.AiUsageKimiEnabled}:{settings.AiUsageKimiInternational}:{settings.CodexCliPath}";
			if (key == _configuration) return false;
			_configuration = key; _generation++;
			_session.Cancel(); _session.Dispose(); _session = new CancellationTokenSource();
			_lastAutomatic = default;
			_settings = new AppSettings
			{
				ShowAiUsageCard = settings.ShowAiUsageCard, AiUsageEnabled = settings.AiUsageEnabled,
				AiUsageCodexEnabled = settings.AiUsageCodexEnabled, AiUsageDeepSeekEnabled = settings.AiUsageDeepSeekEnabled,
				AiUsageGeminiEnabled = settings.AiUsageGeminiEnabled,
				AiUsageKimiEnabled = settings.AiUsageKimiEnabled, AiUsageKimiInternational = settings.AiUsageKimiInternational,
				CodexCliPath = settings.CodexCliPath
			};
			// Configuration changes discard cached account data; no stale account gets relabelled.
			Volatile.Write(ref _current, settings.ShowAiUsageCard && settings.AiUsageEnabled
				? _providers.Where(p => p.IsEnabled(_settings)).Select(p => Empty(p.ProviderId, "Loading")).ToArray()
				: Array.Empty<AiUsageSnapshot>());
			return true;
		}
	}

	public Task RefreshAsync(AppSettings settings, bool manual, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
		Configure(settings);
		lock (_gate)
		{
			if (_disposed || !_settings.ShowAiUsageCard || !_settings.AiUsageEnabled) return Task.CompletedTask;
			if (!_refreshTask.IsCompleted && _runGeneration == _generation)
				return cancellationToken.CanBeCanceled ? _refreshTask.WaitAsync(cancellationToken) : _refreshTask;
			var now = _clock();
			if (manual && now - _lastManual < TimeSpan.FromSeconds(10)) return Task.CompletedTask;
			if (!manual && now - _lastAutomatic < TimeSpan.FromMinutes(5)) return Task.CompletedTask;
			if (manual) _lastManual = now;
			_lastAutomatic = now;
			var previous = _refreshTask; var generation = _generation; var captured = _settings;
			var sessionToken = _session.Token;
			_runGeneration = generation;
			_refreshTask = Task.Run(async () =>
			{
				try { await previous.ConfigureAwait(false); } catch (Exception) { /* A failed predecessor must not poison future refreshes. */ }
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, sessionToken);
				var token = linked.Token;
				token.ThrowIfCancellationRequested();
				LoadHistoryOnce();
				await Task.WhenAll(_providers.Where(p => p.IsEnabled(captured)).Select(p => RefreshProviderAsync(p, captured, generation, token))).ConfigureAwait(false);
				token.ThrowIfCancellationRequested();
				SaveHistory(token, generation);
			}, CancellationToken.None);
			return cancellationToken.CanBeCanceled ? _refreshTask.WaitAsync(cancellationToken) : _refreshTask;
		}
	}
	private async Task RefreshProviderAsync(IAiUsageProvider provider, AppSettings settings, int generation, CancellationToken token)
	{
		var gate = _providerGates[provider.ProviderId];
		await gate.WaitAsync(token).ConfigureAwait(false);
		AiUsageSnapshot snapshot;
		try
		{
			try { snapshot = await provider.RefreshAsync(settings, token).ConfigureAwait(false); }
			catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
			catch (Exception e) { snapshot = Empty(provider.ProviderId, e is OperationCanceledException ? "Request timed out" : e.GetType().Name); }
			token.ThrowIfCancellationRequested();
			lock (_gate)
			{
				if (_disposed || generation != _generation || token.IsCancellationRequested) return;
				var previous = Current.FirstOrDefault(p => p.ProviderId == provider.ProviderId);
				// Missing credentials/auth or changed configuration must never expose a previous account.
				if (!snapshot.HasData && !snapshot.NeedsConnection && previous?.HasData == true &&
					(snapshot.ContinuityKey.Length == 0 || previous.ContinuityKey.Split('|')[0] == snapshot.ContinuityKey.Split('|')[0]))
					snapshot = previous with { Status = snapshot.Status };
				var items = Current.ToArray();
				var index = Array.FindIndex(items, i => i.ProviderId == provider.ProviderId);
				if (index < 0) return;
				items[index] = snapshot; Volatile.Write(ref _current, items);
				if (snapshot.Status == "Connected" && snapshot.TrendValue.HasValue)
					lock (_historyGate) AppendPoint(snapshot);
				if (Updated is { } updated)
					foreach (EventHandler subscriber in updated.GetInvocationList())
						try { subscriber(this, EventArgs.Empty); } catch (Exception) { /* Presentation failures do not disable polling. */ }
			}
		}
		finally { gate.Release(); }
	}
	public AiUsageHistory GetHistory(string providerId)
	{
		var snapshot = Current.FirstOrDefault(p => p.ProviderId == providerId);
		lock (_historyGate)
		{
			var all = _points.Where(p => p.ProviderId == providerId && p.At >= _clock().AddHours(-24)).ToArray();
			var unit = snapshot?.TrendUnit is { Length: > 0 } u ? u : all.LastOrDefault()?.Unit ?? "";
			var scope = snapshot?.ContinuityKey.Split('|')[0] ?? all.LastOrDefault()?.ContinuityKey.Split('|')[0];
			return new(all.Where(p => p.Unit == unit && p.ContinuityKey.Split('|')[0] == scope).ToArray(), unit);
		}
	}
	public string? GetApiKey(string providerId) => _credentials.Get(providerId);
	public void SaveApiKeys(string deepSeekKey, string kimiKey)
	{
		lock (_gate)
		{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if ((_credentials.Get("deepseek") ?? "") == deepSeekKey.Trim() && (_credentials.Get("kimi") ?? "") == kimiKey.Trim()) return;
		_credentials.Save(deepSeekKey, kimiKey);
		_configuration = null; _generation++; _session.Cancel();
		Volatile.Write(ref _current, Current.Select(s => Empty(s.ProviderId, "Loading")).ToArray());
		}
	}
	public Task<string> TestCodexAsync(string path, CancellationToken token = default) =>
		RunTestAsync("codex", ct => CodexUsageClient.ReadAsync(path, ct), token);
	public Task<string> TestApiKeyAsync(string provider, string key, bool international, CancellationToken token = default) =>
		RunTestAsync(provider, ct => ReadBalanceAsync(provider, key, international, ct), token);
	private Task<string> RunTestAsync(string provider, Func<CancellationToken, Task<AiUsageSnapshot>> operation, CancellationToken token)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			var task = Task.Run(async () =>
			{
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
				var gate = _providerGates[provider];
				await gate.WaitAsync(linked.Token).ConfigureAwait(false);
				try { return (await operation(linked.Token).ConfigureAwait(false)).Status; }
				finally { gate.Release(); }
			});
			_tests.RemoveAll(t => t.IsCompleted); _tests.Add(task);
			return task;
		}
	}

	private static async Task<AiUsageSnapshot> ReadBalanceAsync(string id, string? key, bool international, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(key)) return Empty(id, "Waiting for API key");
		var endpoint = id == "deepseek" ? "https://api.deepseek.com/user/balance" :
			international ? "https://api.moonshot.ai/v1/users/me/balance" : "https://api.moonshot.cn/v1/users/me/balance";
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));
		using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			return Empty(id, response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "API key rejected" : $"Service error {(int)response.StatusCode}");
		await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
		using var data = new MemoryStream(); var buffer = new byte[8192];
		while (true)
		{
			var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
			if (count == 0) break;
			if (data.Length + count > 256 * 1024) throw new InvalidDataException("Oversized balance response");
			data.Write(buffer, 0, count);
		}
		using var json = JsonDocument.Parse(data.ToArray());
		var scope = $"{id}:{(international ? "global" : "cn")}:{AiUsageParsers.Fingerprint(key)}";
		return id == "deepseek" ? AiUsageParsers.DeepSeek(json.RootElement, scope, DateTimeOffset.UtcNow) :
			AiUsageParsers.Kimi(json.RootElement, international, scope, DateTimeOffset.UtcNow);
	}
	internal static AiUsageSnapshot Empty(string id, string status) =>
		new(id, id == "codex" ? "Codex" : id == "deepseek" ? "DeepSeek" : id == "gemini" ? "Gemini" : "Kimi", id, "—", status, null, Array.Empty<AiUsageDetail>(), null, "", "");

	private void AppendPoint(AiUsageSnapshot snapshot)
	{
		var now = _clock();
		var previous = _points.LastOrDefault(p => p.ProviderId == snapshot.ProviderId);
		var changed = previous is not null && (previous.ContinuityKey != snapshot.ContinuityKey || previous.Unit != snapshot.TrendUnit ||
			now - previous.At > TimeSpan.FromMinutes(10) || now < previous.At ||
			(snapshot.TrendUnit == "percent" && snapshot.TrendValue > previous.Value));
		var bucket = now.UtcTicks / TimeSpan.TicksPerMinute / 5;
		if (previous is not null && previous.At.UtcTicks / TimeSpan.TicksPerMinute / 5 == bucket)
		{
			_points.Remove(previous); changed |= previous.BreakBefore;
		}
		_points.Add(new(snapshot.ProviderId, now, snapshot.TrendValue!.Value, snapshot.TrendUnit, snapshot.ContinuityKey, changed));
		PruneHistory();
	}
	private void LoadHistoryOnce()
	{
		if (_historyLoaded) return;
		_historyLoaded = true;
		try
		{
			if (!File.Exists(_historyPath) || new FileInfo(_historyPath).Length > 1024 * 1024) return;
			var items = JsonSerializer.Deserialize<List<AiUsagePoint?>>(File.ReadAllText(_historyPath));
			lock (_historyGate)
			{
				_points.AddRange((items ?? new()).Where(p => p is not null && p.ProviderId is "codex" or "deepseek" or "kimi" or "gemini" &&
					p.Unit is not null && p.ContinuityKey is { Length: > 0 } && p.At <= _clock()).Select(p => p!).OrderBy(p => p.At));
				PruneHistory();
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
	}
	private void PruneHistory()
	{
		var cutoff = _clock().AddHours(-24);
		_points.RemoveAll(p => p.At < cutoff || p.At > _clock());
		foreach (var id in _points.Select(p => p.ProviderId).Distinct().ToArray())
		{
			var remove = _points.Where(p => p.ProviderId == id).OrderByDescending(p => p.At).Skip(288).ToHashSet();
			_points.RemoveAll(remove.Contains);
		}
	}
	private void SaveHistory(CancellationToken token, int generation)
	{
		AiUsagePoint[] points;
		lock (_historyGate) points = _points.ToArray();
		try
		{
			token.ThrowIfCancellationRequested();
			Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
			var temporary = _historyPath + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(points));
			lock (_gate)
			{
				if (_disposed || generation != _generation || token.IsCancellationRequested) { File.Delete(temporary); return; }
				File.Move(temporary, _historyPath, true);
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
	}
	public Task WaitForShutdownAsync()
	{
		lock (_gate) return Task.WhenAll(_tests.Append(_refreshTask));
	}
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true; _generation++; _lifetime.Cancel(); _session.Cancel(); Updated = null;
			var drain = Task.WhenAll(_tests.Append(_refreshTask));
			_ = drain.ContinueWith(_ =>
			{
				_lifetime.Dispose(); _session.Dispose();
				foreach (var gate in _providerGates.Values) gate.Dispose();
			}, TaskScheduler.Default);
		}
	}
	private sealed class Provider(string id, Func<AppSettings, bool> enabled, Func<AppSettings, CancellationToken, Task<AiUsageSnapshot>> refresh) : IAiUsageProvider
	{
		public string ProviderId => id;
		public bool IsEnabled(AppSettings settings) => enabled(settings);
		public Task<AiUsageSnapshot> RefreshAsync(AppSettings settings, CancellationToken token) => refresh(settings, token);
	}
}

