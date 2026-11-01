namespace StageManager.Card3DPrototype.Calendar;

internal sealed class CalendarService : IDisposable
{
    private sealed class ProviderState(ICalendarProvider provider)
    {
        public readonly ICalendarProvider Provider = provider;
        public readonly SemaphoreSlim Setup = new(1, 1);
        public int Users;
        public bool Retired, Initialized;
        public CalendarAccount? Account;
        public IReadOnlyList<CalendarInfo> Calendars = Array.Empty<CalendarInfo>();
    }

    private sealed class RangeState(DateOnly start, DateOnly end)
    {
        public readonly DateOnly Start = start, End = end;
        public DateOnly CacheKey = start;
        public TimeZoneInfo Zone = TimeZoneInfo.Local;
        public bool Due;
        public CalendarSnapshot Snapshot = CalendarSnapshot.Empty(CalendarStatus.NotConnected);
        public DateTimeOffset? Attempt;
        public long Access;
        public Task<CalendarSnapshot>? Flight;
    }

    private readonly object gate = new();
    private readonly Func<string, ICalendarProvider> factory;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<TimeZoneInfo> timeZone;
    private string zoneId;
    private TimeSpan zoneOffset;
    private readonly System.Threading.Timer timer;
    private readonly Dictionary<DateOnly, RangeState> months = new();
    private CancellationTokenSource generationCancel = new();
    private ProviderState? provider;
    private RangeState day;
    private long generation, access;
    private bool enabled, disposed, signedOut, connecting;
    private string clientId = "";
    private string[] ids = [];
    private CalendarAccount? account;
    private IReadOnlyList<CalendarInfo> calendars = Array.Empty<CalendarInfo>();
    private DateTimeOffset retryAt = DateTimeOffset.MinValue;
    private DateTimeOffset serverRetryAt = DateTimeOffset.MinValue;
    private int failures;
    private DateTimeOffset? codexManualAttempt;

    public CalendarService(Func<string, ICalendarProvider>? factory = null, Func<DateTimeOffset>? clock = null)
        : this(factory, clock, () => TimeZoneInfo.Local) { }

    // Injectable zone keeps time-change tests isolated from the machine's settings.
    internal CalendarService(Func<string, ICalendarProvider>? factory, Func<DateTimeOffset>? clock, Func<TimeZoneInfo> timeZone)
    {
        this.factory = factory ?? (id => id.StartsWith("codex:", StringComparison.Ordinal) ? new CodexCalendarProvider(id[6..]) : new OutlookCalendarProvider(id));
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.timeZone = timeZone;
        var zone = timeZone();
        zoneId = zone.Id;
        zoneOffset = zone.GetUtcOffset(this.clock());
        var today = Today();
        day = new(today, today.AddDays(1)) { Zone = zone, Snapshot = CalendarSnapshot.Empty(CalendarStatus.Disabled) };
        timer = new System.Threading.Timer(_ => { _ = TickAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal bool PeriodicRefreshEnabled { get; private set; }

    // Events may originate on a worker thread; the UI subscriber owns dispatching.
    public event EventHandler? Updated;
    public CalendarSnapshot Current { get { lock (gate) { EnsureDay(); return day.Snapshot; } } }
    public CalendarAccount? Account { get { lock (gate) return account; } }
    public IReadOnlyList<CalendarInfo> Calendars { get { lock (gate) return calendars; } }

    public bool Configure(bool enabled, string clientId, IReadOnlyList<string> ids)
    {
        clientId = (clientId ?? "").Trim();
        var selection = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        lock (gate)
        {
            ThrowIfDisposed();
            if (this.enabled == enabled && this.clientId == clientId && this.ids.SequenceEqual(selection)) return false;
            var changedClient = this.clientId != clientId;
            this.enabled = enabled;
            var periodic = enabled && !clientId.StartsWith("codex:", StringComparison.Ordinal);
            timer.Change(periodic ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan,
                periodic ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan);
            PeriodicRefreshEnabled = periodic;
            this.clientId = clientId;
            this.ids = selection;
            if (changedClient) signedOut = false;
            Invalidate();
            RetireProvider();
            account = null;
            calendars = Array.Empty<CalendarInfo>();
            ClearRanges(BaseStatus());
        }
        Notify();
        return true;
    }

    public async Task RefreshAsync(bool manual = false, CancellationToken token = default)
    {
        Task<CalendarSnapshot> work;
        lock (gate) { ThrowIfDisposed(); EnsureDay(); work = GetRange(day, manual); }
        await work.WaitAsync(token).ConfigureAwait(false);
    }

    public Task<CalendarSnapshot> GetMonthAsync(DateOnly month, bool manual = false, CancellationToken token = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureDay();
            month = CalendarDates.MonthStart(month);
            if (!months.TryGetValue(month, out var range))
            {
                if (months.Count >= 3)
                {
                    var oldest = months.MinBy(pair => pair.Value.Access).Key;
                    months.Remove(oldest); // Its flight may finish, but cannot publish into this cache.
                }
                var gridStart = CalendarDates.GridStart(month);
                range = new(gridStart, gridStart.AddDays(42)) { Zone = timeZone(), CacheKey = month, Snapshot = CalendarSnapshot.Empty(BaseStatus()) };
                months.Add(month, range);
            }
            range.Access = ++access;
            return GetRange(range, manual).WaitAsync(token);
        }
    }

    private Task<CalendarSnapshot> GetRange(RangeState range, bool manual)
    {
        if (!enabled || !Configured() || signedOut || connecting)
        {
            if (!connecting) range.Snapshot = CalendarSnapshot.Empty(BaseStatus());
            return Task.FromResult(range.Snapshot);
        }
        if (range.Flight is not null) return range.Flight;
        var now = clock();
        if (manual && clientId.StartsWith("codex:", StringComparison.Ordinal))
        {
            if (codexManualAttempt is { } last && now - last < TimeSpan.FromSeconds(10)) return Task.FromResult(range.Snapshot);
            codexManualAttempt = now;
            range.Attempt = null;
            retryAt = serverRetryAt;
        }
        if (now < retryAt || (range.Attempt is { } attempt && now - attempt <
            (manual || range.Due ? TimeSpan.FromSeconds(10) : range.Snapshot.ErrorCode.Length != 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(5))))
            return Task.FromResult(range.Snapshot);
        ProviderState state;
        try { state = GetProvider(); }
        catch (Exception ex)
        {
            range.Snapshot = Failure(range.Snapshot, ex);
            Backoff(ex);
            return Task.FromResult(range.Snapshot);
        }
        state.Users++;
        range.Attempt = now;
        range.Due = false;
        range.Snapshot = range.Snapshot with { Status = range.Snapshot.LastSuccess is null ? CalendarStatus.Loading : CalendarStatus.Stale };
        range.Flight = FetchAsync(range, state, generation, manual, generationCancel.Token);
        return range.Flight;
    }

    private async Task<CalendarSnapshot> FetchAsync(RangeState range, ProviderState state, long version, bool manual, CancellationToken token)
    {
        // Ensure singleflight is registered before synchronous fake/provider completions.
        await Task.Yield();
        Notify();
        try
        {
            if (manual && state.Provider is CodexCalendarProvider codex)
            {
                await state.Setup.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await codex.SyncAsync(Boundary(range.Start, range.Zone), Boundary(range.End, range.Zone), ids, token).ConfigureAwait(false);
                    state.Initialized = false;
                }
                finally { state.Setup.Release(); }
            }
            await InitializeAsync(state, token).ConfigureAwait(false);
            string[] selected;
            lock (gate)
            {
                if (!Valid(version, range)) return CalendarSnapshot.Empty(BaseStatus());
                account = state.Account;
                calendars = state.Calendars;
                selected = Selected(state.Calendars);
                if (state.Account is not null && selected.Length == 0)
                    throw new CalendarProviderException("CalendarsUnavailable");
            }
            IReadOnlyList<CalendarEvent> events = Array.Empty<CalendarEvent>();
            if (state.Account is not null && selected.Length != 0)
                events = await state.Provider.GetEventsAsync(selected, Boundary(range.Start, range.Zone),
                    Boundary(range.End, range.Zone), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!Valid(version, range)) return CalendarSnapshot.Empty(BaseStatus());
                range.Snapshot = state.Account is null ? CalendarSnapshot.Empty(CalendarStatus.NotConnected) :
                    new(CalendarStatus.Connected, Array.AsReadOnly(events.Where(e => selected.Contains(e.CalendarId, StringComparer.Ordinal) &&
                        (e.IsAllDay ? e.StartDate < range.End && e.EndDate > range.Start :
                            e.Start < Boundary(range.End, range.Zone) && e.End > Boundary(range.Start, range.Zone)))
                        .OrderByDescending(e => e.IsAllDay).ThenBy(e => e.Start).ThenBy(e => e.Id).ToArray()),
                        state.Provider is CodexCalendarProvider cached ? cached.LastSuccess : clock());
                if (state.Provider is CodexCalendarProvider && range.Start <= day.Start && range.End >= day.End)
                {
                    day.Snapshot = range.Snapshot with { Events = range.Snapshot.Events.Where(e => CalendarDates.OnDay(e, day.Start)).ToArray() };
                    day.Attempt = clock();
                }
                failures = 0;
                retryAt = serverRetryAt;
                return range.Snapshot;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { lock (gate) return CalendarSnapshot.Empty(BaseStatus()); }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (!Valid(version, range)) return CalendarSnapshot.Empty(BaseStatus());
                range.Snapshot = Failure(range.Snapshot, ex);
                Backoff(ex);
                return range.Snapshot;
            }
        }
        finally
        {
            bool notify;
            lock (gate) { notify = Valid(version, range); range.Flight = null; Release(state); }
            if (notify) Notify();
        }
    }

    private async Task InitializeAsync(ProviderState state, CancellationToken token)
    {
        await state.Setup.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (state.Initialized) return;
            var found = await state.Provider.GetAccountAsync(token).ConfigureAwait(false);
            var listed = found is null ? Array.Empty<CalendarInfo>() : await state.Provider.GetCalendarsAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (found is not null && listed.Count == 0) throw new CalendarProviderException("CalendarsUnavailable");
            state.Account = found;
            state.Calendars = Array.AsReadOnly(listed.ToArray());
            state.Initialized = true;
        }
        finally { state.Setup.Release(); }
    }

    public async Task ConnectAsync(CancellationToken token = default)
    {
        ProviderState state;
        long version;
        CancellationToken operationToken;
        lock (gate)
        {
            ThrowIfDisposed();
            if (!enabled || !Configured() || connecting) return;
            try { state = GetProvider(); }
            catch (Exception ex) { day.Snapshot = Failure(day.Snapshot, ex); return; }
            Invalidate();
            ClearRanges(CalendarStatus.Loading);
            account = null;
            calendars = Array.Empty<CalendarInfo>();
            signedOut = false;
            connecting = true;
            state.Users++;
            version = generation;
            operationToken = generationCancel.Token;
        }
        Notify();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken);
        var connected = false;
        try
        {
            await state.Setup.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                state.Initialized = false;
                var found = await state.Provider.ConnectAsync(linked.Token).ConfigureAwait(false);
                var listed = await state.Provider.GetCalendarsAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (listed.Count == 0) throw new CalendarProviderException("CalendarsUnavailable");
                lock (gate)
                {
                    if (version != generation || disposed) return;
                    state.Account = account = found;
                    state.Calendars = calendars = Array.AsReadOnly(listed.ToArray());
                    state.Initialized = true;
                    day.Snapshot = CalendarSnapshot.Empty(CalendarStatus.Connected);
                    connected = true;
                }
            }
            finally { state.Setup.Release(); }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            lock (gate) { if (version == generation && !disposed) day.Snapshot = CalendarSnapshot.Empty(CalendarStatus.NotConnected); }
            if (token.IsCancellationRequested) throw;
        }
        catch (Exception ex)
        {
            lock (gate) { if (version == generation && !disposed) { day.Snapshot = Failure(day.Snapshot, ex); Backoff(ex); } }
        }
        finally
        {
            lock (gate) { if (version == generation) connecting = false; Release(state); }
            Notify();
        }
        if (connected)
        {
            Task<CalendarSnapshot>? refresh = null;
            lock (gate) { if (version == generation && !disposed) refresh = GetRange(day, true); }
            if (refresh is not null) await refresh.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(CancellationToken token = default)
    {
        ProviderState? state;
        long version;
        lock (gate)
        {
            ThrowIfDisposed();
            Invalidate();
            signedOut = true;
            account = null;
            calendars = Array.Empty<CalendarInfo>();
            ClearRanges(BaseStatus());
            state = provider;
            // Logout must also remove a previously cached account when disabled or not yet loaded.
            if (state is null && Configured())
            {
                try { state = GetProvider(); }
                catch (Exception ex) { day.Snapshot = Failure(day.Snapshot, ex); }
            }
            if (state is not null) state.Users++;
            version = generation;
        }
        Notify();
        if (state is null) return;
        try
        {
            await state.Setup.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await state.Provider.DisconnectAsync(token).ConfigureAwait(false);
                state.Initialized = false;
                state.Account = null;
                state.Calendars = Array.Empty<CalendarInfo>();
            }
            finally { state.Setup.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (gate) { if (version == generation && !disposed) day.Snapshot = Failure(day.Snapshot, ex); }
        }
        finally { lock (gate) Release(state); Notify(); }
    }

    public Task ResumeAsync()
    {
        // Host may call this on wake, network recovery and SystemEvents.TimeChanged.
        TimeZoneInfo.ClearCachedData();
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureDay();
            day.Due = true;
            foreach (var range in months.Values) range.Due = true;
            // A network recovery may bypass local error backoff, but never server Retry-After.
            retryAt = serverRetryAt;
        }
        return RefreshAsync();
    }

    private string[] Selected(IReadOnlyList<CalendarInfo> listed) => ids.Length == 0
        ? listed.OrderByDescending(c => c.IsDefault).Take(1).Select(c => c.Id).ToArray()
        : ids.Where(id => listed.Any(c => c.Id == id)).ToArray();
    private bool Configured() => clientId.StartsWith("codex:", StringComparison.Ordinal) || Guid.TryParse(clientId, out var id) && id != Guid.Empty;
    public bool UsesCodex { get { lock (gate) return clientId.StartsWith("codex:", StringComparison.Ordinal); } }
    private CalendarStatus BaseStatus() => !enabled ? CalendarStatus.Disabled : !Configured() ? CalendarStatus.NotConfigured : CalendarStatus.NotConnected;
    private ProviderState GetProvider() => provider ??= new(factory(clientId));
    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock(), timeZone()).DateTime);
    private static DateTimeOffset Boundary(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new(local, zone.GetUtcOffset(local));
    }
    private void EnsureDay()
    {
        var zone = timeZone();
        var offset = zone.GetUtcOffset(clock());
        if (!disposed && (zoneId != zone.Id || zoneOffset != offset))
        {
            zoneId = zone.Id;
            zoneOffset = offset;
            var attempt = day.Attempt;
            var oldMonths = months.ToArray();
            Invalidate(preserveThrottles: true);
            ClearRanges(BaseStatus());
            day.Attempt = attempt;
            day.Due = true;
            foreach (var (key, old) in oldMonths)
                months[key] = new(old.Start, old.End) { CacheKey = key, Zone = zone, Attempt = old.Attempt,
                    Access = old.Access, Due = true, Snapshot = CalendarSnapshot.Empty(BaseStatus()) };
        }
        var today = Today();
        if (day.Start != today) day = new(today, today.AddDays(1)) { Zone = zone, Snapshot = CalendarSnapshot.Empty(BaseStatus()) };
    }
    private void ClearRanges(CalendarStatus status)
    {
        months.Clear();
        var today = Today();
        day = new(today, today.AddDays(1)) { Zone = timeZone(), Snapshot = CalendarSnapshot.Empty(status) };
    }
    private bool Valid(long version, RangeState range) => !disposed && generation == version &&
        (ReferenceEquals(day, range) || months.TryGetValue(range.CacheKey, out var cached) && ReferenceEquals(cached, range));
    private void Invalidate(bool preserveThrottles = false)
    {
        codexManualAttempt = null;
        generation++;
        connecting = false;
        var previous = generationCancel;
        generationCancel = new();
        previous.Cancel();
        previous.Dispose();
        if (!preserveThrottles)
        {
            retryAt = serverRetryAt = DateTimeOffset.MinValue;
            failures = 0;
        }
    }
    private void RetireProvider()
    {
        if (provider is null) return;
        provider.Retired = true;
        if (provider.Users == 0) provider.Provider.Dispose();
        provider = null;
    }
    private static void Release(ProviderState state)
    {
        state.Users--;
        if (state.Retired && state.Users == 0) state.Provider.Dispose();
    }
    private static CalendarSnapshot Failure(CalendarSnapshot previous, Exception ex)
    {
        var code = ex is CalendarProviderException error ? SafeCode(error.Code) : "Unavailable";
        var status = code == "NotConfigured" ? CalendarStatus.NotConfigured :
            previous.LastSuccess is not null ? CalendarStatus.Stale :
            code == "AuthRequired" ? CalendarStatus.AuthRequired : CalendarStatus.Error;
        return previous with { Status = status, ErrorCode = code };
    }
    private static string SafeCode(string code) => code switch
    {
        "NotConfigured" or "AuthRequired" or "AuthenticationCancelled" or "AuthenticationFailed" or
        "TokenCacheUnavailable" or "AccessDenied" or "Throttled" or "ServiceUnavailable" or "HttpError" or
        "NetworkUnavailable" or "Timeout" or "InvalidRange" or "InvalidResponse" or "ResponseTooLarge" or "CalendarsUnavailable" or
        "CodexRefreshRequired" or "CodexUnavailable" or "CodexCalendarUnavailable" or "CodexCalendarReadFailed" or "CodexApprovalRequired" or "CodexProtocolUnavailable" => code,
        _ => "Unavailable"
    };
    private void Backoff(Exception ex)
    {
        failures = Math.Min(failures + 1, 4);
        var wait = TimeSpan.FromMinutes(failures switch { 1 => 1, 2 => 2, 3 => 5, _ => 15 });
        if (ex is CalendarProviderException { RetryAfter: { } requested } && requested > wait) wait = requested;
        var now = clock();
        if (ex is CalendarProviderException { RetryAfter: { } retry } && retry > TimeSpan.Zero)
        {
            var until = retry > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + retry;
            if (until > serverRetryAt) serverRetryAt = until;
        }
        retryAt = wait > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + wait;
        if (serverRetryAt > retryAt) retryAt = serverRetryAt;
    }
    private async Task TickAsync()
    {
        try { await RefreshAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }
    private void Notify()
    {
        EventHandler? handlers;
        lock (gate) { if (disposed) return; handlers = Updated; }
        if (handlers is null) return;
        // A UI subscriber must not turn a successful fetch into a provider failure.
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch { }
    }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(disposed, this); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
            PeriodicRefreshEnabled = false;
            Invalidate();
            generationCancel.Dispose();
            RetireProvider();
            account = null;
            calendars = Array.Empty<CalendarInfo>();
            ClearRanges(CalendarStatus.Disabled);
            Updated = null;
        }
    }
}
