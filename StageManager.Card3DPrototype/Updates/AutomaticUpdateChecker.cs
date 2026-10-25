using System.Text.Json;

namespace StageManager.Card3DPrototype.Updates;

// Metadata only. Installation remains exclusively in UpdateForm after an explicit click.
internal sealed class AutomaticUpdateChecker : IDisposable
{
    internal sealed record CheckState(DateTimeOffset? LastAttemptUtc = null, DateTimeOffset? LastSuccessUtc = null,
        string? NotifiedVersion = null);
    internal static string DefaultStatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "automatic-update-state.json");
    private readonly string _path;
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<CancellationToken, Task<UpdateRelease>> _check;
    private readonly Version _current;
    private CancellationTokenSource? _enabledLifetime;
    private CheckState _state;
    private UpdateRelease? _pending;
    private DateTimeOffset _notBefore;
    private int _generation;
    private int _inFlight;
    private bool _disposed;
    internal Task ActiveCheck { get; private set; } = Task.CompletedTask;
    internal bool Enabled => _enabledLifetime is not null;

    internal AutomaticUpdateChecker(string? path = null, Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<UpdateRelease>>? check = null, Version? current = null)
    {
        _path = path ?? DefaultStatePath; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _current = current ?? AppVersion.Current;
        _check = check ?? (async token => { using var service = new ReleaseUpdateService(); return await service.CheckAsync(token).ConfigureAwait(false); });
        try { _state = JsonSerializer.Deserialize<CheckState>(File.ReadAllText(_path)) ?? new(); }
        catch { _state = new(); }
    }
    internal void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
        if (_disposed || enabled == Enabled) return;
        _generation++;
        _enabledLifetime?.Cancel(); _enabledLifetime?.Dispose(); _enabledLifetime = null;
        _pending = null;
        if (enabled) { _enabledLifetime = new(); _notBefore = _clock().AddSeconds(45); }
        }
    }
    internal Task PollAsync()
    {
        lock (_sync)
        {
        if (_disposed || !Enabled || Volatile.Read(ref _inFlight) != 0) return ActiveCheck;
        var now = _clock();
        if (now < _notBefore) return Task.CompletedTask;
        if (_state.LastAttemptUtc is { } previous && previous <= now && now - previous < TimeSpan.FromHours(24)) return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return ActiveCheck;
        var generation = _generation; var token = _enabledLifetime!.Token;
        _state = _state with { LastAttemptUtc = now }; Save();
        return ActiveCheck = CheckAsync(generation, token);
        }
    }
    private async Task CheckAsync(int generation, CancellationToken token)
    {
        try
        {
            var release = await _check(token).ConfigureAwait(false);
            lock (_sync)
            {
            if (_disposed || token.IsCancellationRequested || generation != _generation) return;
            _state = _state with { LastSuccessUtc = _clock() }; Save();
            if (release.Version > _current && (!Version.TryParse(_state.NotifiedVersion, out var notified) || release.Version > notified)) _pending = release;
            }
        }
        catch (Exception) { /* Offline and malformed responses never imply "up to date". Try again tomorrow. */ }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }
    internal void TryNotify(bool allowed, Action<UpdateRelease> notify)
    {
        lock (_sync)
        {
        if (_disposed || !Enabled || !allowed || _pending is not { } release) return;
        notify(release);
        _pending = null;
        _state = _state with { NotifiedVersion = release.Version.ToString() }; Save();
        }
    }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_state));
            File.Move(_path + ".tmp", _path, true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        lock (_sync)
        {
        if (_disposed) return;
        SetEnabled(false); _disposed = true;
        }
    }
}
