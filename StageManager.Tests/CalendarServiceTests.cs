using System.Text.Json;
using StageManager.Card3DPrototype.Calendar;

internal static class CalendarServiceTests
{
    private const string ClientId = "ec28bd67-1d44-4b94-8e1d-b377a3fb3b99";

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await ConfigurationAsync();
        await ConnectionAndSelectionAsync();
        await IntervalsAndBackoffAsync();
        await SingleflightAsync();
        await GenerationAsync();
        await LogoutAsync();
        await MonthCacheAndMidnightAsync();
        await MissingCalendarsAsync();
        await ResumeAndTimeZoneAsync();
        Ownership();
        ParsingAndTrust();
    }

    private static async Task ConfigurationAsync()
    {
        var factories = 0;
        using var service = new CalendarService(_ => { factories++; return new Fake(); });
        Check(service.Current.Status == CalendarStatus.Disabled, "Initial status");
        Check(!service.PeriodicRefreshEnabled, "Constructor leaves periodic timer stopped");
        Check(service.Configure(true, "", []), "Enable missing config");
        Check(service.PeriodicRefreshEnabled, "Enable starts periodic timer");
        await service.RefreshAsync();
        await service.ConnectAsync();
        await service.GetMonthAsync(new(2026, 10, 1));
        Check(service.Current.Status == CalendarStatus.NotConfigured && factories == 0, "Missing client ID must not instantiate/network");
        service.Configure(true, "not-a-guid", []);
        await service.RefreshAsync();
        Check(factories == 0, "Malformed client ID");
        service.Configure(false, ClientId, []);
        Check(!service.PeriodicRefreshEnabled, "Disable stops periodic timer");
        await service.RefreshAsync();
        Check(factories == 0 && service.Current.Status == CalendarStatus.Disabled, "Disabled provider");
        Check(!service.Configure(false, ClientId, []), "Unchanged configuration");
        service.Configure(true, ClientId, []);
        Check(service.PeriodicRefreshEnabled, "Reenable restarts timer");
        service.Dispose();
        Check(!service.PeriodicRefreshEnabled, "Dispose stops timer");
    }

    private static void Ownership()
    {
        Check(OutlookCalendarProvider.IsOwnedCalendar(true, "alias@outlook.com", "login@gmail.com"), "Default calendar survives personal alias mismatch");
        Check(OutlookCalendarProvider.IsOwnedCalendar(true, "", ""), "Default ownership does not require profile permission");
        Check(OutlookCalendarProvider.IsOwnedCalendar(false, "LOGIN@OUTLOOK.COM", "login@outlook.com"), "Nondefault owner match ignores case");
        Check(!OutlookCalendarProvider.IsOwnedCalendar(false, "alias@outlook.com", "login@gmail.com"), "Nondefault alias remains conservative");
        Check(!OutlookCalendarProvider.IsOwnedCalendar(false, "", ""), "Unknown nondefault owner rejected");
    }

    private static async Task MissingCalendarsAsync()
    {
        var now = At(2026, 10, 25, 12);
        var fake = new Fake { Listed = [] };
        using var service = new CalendarService(_ => fake, () => now);
        service.Configure(true, ClientId, []);
        await service.RefreshAsync();
        Check(service.Current.Status == CalendarStatus.Error && service.Current.ErrorCode == "CalendarsUnavailable" && fake.Fetches == 0,
            "Empty owned listing is not a successful empty calendar");
        await service.ConnectAsync();
        Check(service.Current.Status == CalendarStatus.Error && service.Current.ErrorCode == "CalendarsUnavailable", "Connect cannot claim empty listing success");
        fake.Listed = [new("default", "Main", "green", true)];
        now += TimeSpan.FromSeconds(10);
        await service.ResumeAsync();
        Check(service.Current.Status == CalendarStatus.Connected, "Listing failure can recover");
        fake.Error = new CalendarProviderException("CalendarsUnavailable");
        now += TimeSpan.FromSeconds(10);
        await service.RefreshAsync(true);
        Check(service.Current.Status == CalendarStatus.Stale && service.Current.ErrorCode == "CalendarsUnavailable", "Ownership revalidation failure preserves stale snapshot");

        using var invalidSelection = new CalendarService(_ => new Fake(), () => now);
        invalidSelection.Configure(true, ClientId, ["deleted-calendar"]);
        await invalidSelection.RefreshAsync();
        Check(invalidSelection.Current.Status == CalendarStatus.Error && invalidSelection.Current.ErrorCode == "CalendarsUnavailable", "Missing saved selection is not successful empty data");
    }

    private static async Task ResumeAndTimeZoneAsync()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.CreateCustomTimeZone("ZoneA", TimeSpan.Zero, "Zone A", "Zone A");
        var fake = new Fake();
        using var service = new CalendarService(_ => fake, () => now, () => zone);
        service.Configure(true, ClientId, []);
        await service.RefreshAsync();
        await service.ResumeAsync();
        Check(fake.Fetches == 1, "Wake respects ten-second floor");
        now += TimeSpan.FromSeconds(10);
        await service.RefreshAsync();
        Check(fake.Fetches == 2, "Wake leaves range due when ten-second floor initially blocks it");
        await service.GetMonthAsync(new(2026, 10, 1));
        now += TimeSpan.FromSeconds(10);
        await service.ResumeAsync();
        var count = fake.Fetches;
        await service.GetMonthAsync(new(2026, 10, 1));
        Check(fake.Fetches == count + 1, "Wake marks cached months due too");
        fake.Error = new CalendarProviderException("NetworkUnavailable");
        now += TimeSpan.FromSeconds(10);
        await service.RefreshAsync(true);
        count = fake.Fetches;
        fake.Error = null;
        now += TimeSpan.FromSeconds(10);
        await service.ResumeAsync();
        Check(fake.Fetches == count + 1 && service.Current.Status == CalendarStatus.Connected, "Network recovery bypasses local error backoff");

        // Same local date and offset, different zone ID: invalidate today AND month ranges.
        zone = TimeZoneInfo.CreateCustomTimeZone("ZoneB", TimeSpan.Zero, "Zone B", "Zone B");
        now += TimeSpan.FromSeconds(10);
        Check(service.Current.LastSuccess is null, "Same-date zone-ID change invalidates current snapshot");
        await service.ResumeAsync();
        count = fake.Fetches;
        await service.GetMonthAsync(new(2026, 10, 1));
        Check(fake.Fetches == count + 1, "Same-date zone change invalidates month cache");

        // Same ID, different current offset (DST or changed system rules).
        zone = TimeZoneInfo.CreateCustomTimeZone("ZoneB", TimeSpan.FromHours(1), "Zone B", "Zone B");
        now += TimeSpan.FromSeconds(10);
        await service.ResumeAsync();
        Check(fake.LastStart.Offset == TimeSpan.FromHours(1) && fake.LastStart.Day == 25, "Same-ID offset change refreshes local boundaries");

        fake.Error = new CalendarProviderException("Throttled", TimeSpan.FromMinutes(20));
        now += TimeSpan.FromSeconds(10);
        await service.RefreshAsync(true);
        count = fake.Fetches;
        fake.Error = null;
        zone = TimeZoneInfo.CreateCustomTimeZone("ZoneC", TimeSpan.FromHours(2), "Zone C", "Zone C");
        now += TimeSpan.FromSeconds(10);
        await service.ResumeAsync();
        await service.GetMonthAsync(new(2026, 11, 1), true);
        Check(fake.Fetches == count, "Timezone invalidation and recovery retain server Retry-After across ranges");
        now += TimeSpan.FromMinutes(20);
        await service.ResumeAsync();
        Check(fake.Fetches == count + 1 && fake.LastStart.Offset == TimeSpan.FromHours(2), "Retry-After expiry permits refreshed zone boundaries");

        fake.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        now += TimeSpan.FromSeconds(10);
        var pending = service.RefreshAsync(true);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        zone = TimeZoneInfo.CreateCustomTimeZone("ZoneD", TimeSpan.FromHours(3), "Zone D", "Zone D");
        await service.ResumeAsync();
        Check(fake.LastToken.IsCancellationRequested && service.Current.LastSuccess is null, "Time change cancels old-zone work without bypassing ten-second floor");
        fake.Block.SetResult(true);
        await pending;
        Check(service.Current.LastSuccess is null, "Late old-zone result cannot repopulate cache");
        now += TimeSpan.FromSeconds(10);
        await service.ResumeAsync();
        Check(fake.LastStart.Offset == TimeSpan.FromHours(3) && service.Current.Status == CalendarStatus.Connected, "New-zone fetch completes");
    }

    private static async Task ConnectionAndSelectionAsync()
    {
        var now = At(2026, 10, 25, 12);
        var fake = new Fake { Connected = false };
        using var service = new CalendarService(_ => fake, () => now);
        service.Configure(true, ClientId, []);
        await service.RefreshAsync();
        Check(service.Current.Status == CalendarStatus.NotConnected && fake.Connects == 0, "Silent refresh must never connect");
        await service.ConnectAsync();
        Check(fake.Connects == 1 && service.Account is not null && service.Calendars.Count == 2, "Connect populates account and calendars");
        Check(fake.Selected.SequenceEqual(["default"]), "Empty selection chooses default");
        Check(fake.Order.IndexOf("calendars") < fake.Order.IndexOf("events"), "List before events");

        var selectedFake = new Fake();
        using var selectedService = new CalendarService(_ => selectedFake, () => now);
        selectedService.Configure(true, ClientId, ["other", "unowned", "other"]);
        await selectedService.RefreshAsync();
        Check(selectedFake.Selected.SequenceEqual(["other"]), "Only valid configured selection");
        Check(!selectedService.Configure(true, ClientId, ["unowned", "other"]), "Selection normalized as set");
        Check(selectedFake.Connects == 0, "Cached account has no browser dialogs");
    }

    private static async Task IntervalsAndBackoffAsync()
    {
        var now = At(2026, 10, 25, 12);
        var fake = new Fake();
        fake.Events = [Item("one", now)];
        using var service = new CalendarService(_ => fake, () => now);
        service.Configure(true, ClientId, []);
        await service.RefreshAsync();
        var success = service.Current.LastSuccess;
        await service.RefreshAsync(true);
        Check(fake.Fetches == 1, "Manual ten-second floor");
        now += TimeSpan.FromSeconds(10);
        await service.RefreshAsync(true);
        Check(fake.Fetches == 2, "Manual refresh after ten seconds");
        now += TimeSpan.FromMinutes(4);
        await service.RefreshAsync();
        Check(fake.Fetches == 2, "Automatic five-minute floor");
        now += TimeSpan.FromMinutes(1);
        fake.Error = new CalendarProviderException("NetworkUnavailable");
        success = service.Current.LastSuccess;
        await service.RefreshAsync();
        Check(service.Current.Status == CalendarStatus.Stale && service.Current.Events.Count == 1 && service.Current.LastSuccess == success, "Keep last successful data on failure");
        var attempts = fake.Fetches;
        foreach (var minutes in new[] { 1, 2, 5, 15 })
        {
            now += TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1);
            await service.RefreshAsync(true);
            Check(fake.Fetches == attempts, "Backoff floor " + minutes);
            now += TimeSpan.FromSeconds(1);
            await service.RefreshAsync();
            Check(fake.Fetches == ++attempts, "Backoff retry " + minutes);
        }
        fake.Error = null;
        now += TimeSpan.FromMinutes(15);
        await service.RefreshAsync();
        Check(service.Current.Status == CalendarStatus.Connected && service.Current.ErrorCode == "", "Recovery clears stale error");
        fake.Error = new CalendarProviderException("Throttled", TimeSpan.FromMinutes(20));
        now += TimeSpan.FromSeconds(10);
        await service.RefreshAsync(true);
        attempts = fake.Fetches;
        now += TimeSpan.FromMinutes(19);
        await service.RefreshAsync(true);
        Check(fake.Fetches == attempts, "Retry-After dominates manual refresh");
        now += TimeSpan.FromMinutes(1);
        fake.Error = new Exception("secret-token-must-not-escape");
        await service.RefreshAsync();
        Check(service.Current.ErrorCode == "Unavailable", "Never publish raw exception messages");
    }

    private static async Task SingleflightAsync()
    {
        var fake = new Fake { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var service = new CalendarService(_ => fake, () => At(2026, 10, 25, 12));
        service.Configure(true, ClientId, []);
        using var cancel = new CancellationTokenSource();
        var first = service.RefreshAsync(token: cancel.Token);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RefreshAsync(true);
        cancel.Cancel();
        await Cancelled(first);
        Check(fake.Fetches == 1 && !fake.LastToken.IsCancellationRequested, "One cancelled waiter must not cancel shared operation");
        fake.Block.SetResult(true);
        await second;
        Check(service.Current.Status == CalendarStatus.Connected, "Shared fetch still publishes");

        fake.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var month1 = service.GetMonthAsync(new(2026, 11, 1));
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var gridStart = CalendarDates.GridStart(new(2026, 11, 1));
        Check(fake.LastStart == CalendarDates.LocalBoundary(gridStart) && fake.LastEnd == CalendarDates.LocalBoundary(gridStart.AddDays(42)), "Month fetch includes full six-week grid");
        var month2 = service.GetMonthAsync(new(2026, 11, 19), true);
        Check(fake.Fetches == 2, "Month range normalized and coalesced");
        fake.Block.SetResult(true);
        await Task.WhenAll(month1, month2);
    }

    private static async Task GenerationAsync()
    {
        var now = At(2026, 10, 25, 12);
        var old = new Fake { Events = [Item("old", now)], Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var fresh = new Fake { Events = [Item("fresh", now) with { CalendarId = "other" }] };
        var count = 0;
        using var service = new CalendarService(_ => ++count == 1 ? old : fresh, () => now);
        service.Configure(true, ClientId, []);
        var pending = service.RefreshAsync();
        await old.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Configure(true, ClientId, ["other"]);
        Check(old.LastToken.IsCancellationRequested && old.Disposals == 0, "Cancel obsolete work without disposing its live provider");
        await service.RefreshAsync();
        old.Block.SetResult(true); // Intentionally ignores cancellation, exercising the generation guard.
        await pending;
        Check(service.Current.Events.Single().Id == "fresh" && old.Disposals == 1, "Late old selection cannot publish");
        service.Configure(false, ClientId, ["other"]);
        Check(service.Current.Status == CalendarStatus.Disabled && service.Current.Events.Count == 0 && service.Account is null && service.Calendars.Count == 0, "Disable clears private in-memory state");
        Check(old.Disconnects == 0 && fresh.Disconnects == 0, "Disable retains persisted auth");

        var disposedFake = new Fake { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var disposedService = new CalendarService(_ => disposedFake, () => now);
        disposedService.Configure(true, ClientId, []);
        var late = disposedService.RefreshAsync();
        await disposedFake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disposedService.Dispose();
        disposedFake.Block.SetResult(true);
        await late;
        Check(disposedFake.Disposals == 1 && disposedService.Current.Events.Count == 0, "Dispose rejects late data and retires provider");
    }

    private static async Task LogoutAsync()
    {
        var now = At(2026, 10, 25, 12);
        var fake = new Fake { Events = [Item("one", now)] };
        using var service = new CalendarService(_ => fake, () => now);
        service.Configure(true, ClientId, []);
        await service.RefreshAsync();
        await service.GetMonthAsync(new(2026, 10, 1));
        await service.DisconnectAsync();
        Check(fake.Disconnects == 1 && service.Account is null && service.Calendars.Count == 0 && service.Current.Events.Count == 0, "Logout clears state");
        var calls = fake.Fetches;
        await service.RefreshAsync(true);
        var month = await service.GetMonthAsync(new(2026, 10, 1), true);
        Check(fake.Fetches == calls && month.Events.Count == 0, "Logout prevents silent restoration");
        await service.ConnectAsync();
        Check(service.Current.Status == CalendarStatus.Connected, "Explicit reconnect after logout");
    }

    private static async Task MonthCacheAndMidnightAsync()
    {
        var now = At(2026, 10, 25, 23);
        var fake = new Fake { Events = [Item("yesterday", now)] };
        using var service = new CalendarService(_ => fake, () => now);
        service.Configure(true, ClientId, []);
        await service.RefreshAsync();
        for (var month = 1; month <= 3; month++) await service.GetMonthAsync(new(2026, month, 1));
        var count = fake.Fetches;
        await service.GetMonthAsync(new(2026, 1, 20)); // Touch January; evict February next.
        Check(fake.Fetches == count, "Fresh month cache hit");
        await service.GetMonthAsync(new(2026, 4, 1));
        await service.GetMonthAsync(new(2026, 1, 1));
        Check(fake.Fetches == count + 1, "Three-month LRU keeps recently used month");
        await service.GetMonthAsync(new(2026, 2, 1));
        Check(fake.Fetches == count + 2, "Fourth month evicts oldest month");
        now = At(2026, 10, 26, 0);
        Check(service.Current.Events.Count == 0 && service.Current.LastSuccess is null, "Midnight never shows yesterday as today");
        fake.Events = [Item("today", now)];
        await service.ResumeAsync();
        Check(service.Current.Events.Single().Id == "today", "Resume refreshes new day");
    }

    private static void ParsingAndTrust()
    {
        Check(OutlookCalendarProvider.TrustedGraphUri("https://graph.microsoft.com/v1.0/me/calendars?$skiptoken=x"), "Trusted pagination");
        foreach (var url in new[] { "http://graph.microsoft.com/v1.0/me/calendars", "https://graph.microsoft.com.evil.test/v1.0/me/calendars", "https://user@graph.microsoft.com/v1.0/me/calendars", "https://graph.microsoft.com:444/v1.0/me/calendars", "https://evil.test/v1.0/me/calendars", "https://graph.microsoft.com/v1.0/users/someone/events" })
            Check(!OutlookCalendarProvider.TrustedGraphUri(url), "Reject arbitrary pagination " + url);
        using var allDay = JsonDocument.Parse("""
            {"id":"a","isAllDay":true,"subject":"All day","start":{"dateTime":"2026-10-24T16:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-26T16:00:00","timeZone":"UTC"},"originalStartTimeZone":"China Standard Time","originalEndTimeZone":"China Standard Time","bodyPreview":"plain preview","webLink":"https://evil.test/event"}
            """);
        var item = OutlookCalendarProvider.ParseEvent(allDay.RootElement, "default")!;
        Check(item.StartDate == new DateOnly(2026, 10, 25) && item.EndDate == new DateOnly(2026, 10, 27), "All-day original dates, exclusive end");
        Check(CalendarDates.OnDay(item, new(2026, 10, 26)) && !CalendarDates.OnDay(item, new(2026, 10, 27)), "All-day span");
        Check(item.WebLink == "" && item.Summary == "plain preview", "Only safe link and preview");
        using var timed = JsonDocument.Parse("""
            {"id":"b","start":{"dateTime":"2026-10-25T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-25T02:00:00","timeZone":"UTC"},"webLink":"https://outlook.live.com/calendar/item/123","location":{"displayName":"Room"},"organizer":{"emailAddress":{"name":"Name"}}}
            """);
        var timedItem = OutlookCalendarProvider.ParseEvent(timed.RootElement, "default")!;
        Check(timedItem.Start.Offset == TimeSpan.Zero && timedItem.Start.Hour == 1 && timedItem.Location == "Room" && timedItem.Organizer == "Name" && timedItem.WebLink.Length > 0, "Timed UTC fields");
        using var cancelled = JsonDocument.Parse("""{"id":"c","isCancelled":true}""");
        Check(OutlookCalendarProvider.ParseEvent(cancelled.RootElement, "default") is null, "Cancelled occurrences excluded");
    }

    private static DateTimeOffset At(int year, int month, int day, int hour) =>
        CalendarDates.LocalBoundary(new(year, month, day)).AddHours(hour);
    private static CalendarEvent Item(string id, DateTimeOffset start) => new(id, "default", id,
        start, start.AddMinutes(30), false, DateOnly.FromDateTime(start.LocalDateTime), DateOnly.FromDateTime(start.LocalDateTime), "", "", "", "");
    private static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException("Calendar regression: " + description); }
    private static async Task Cancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }

    private sealed class Fake : ICalendarProvider
    {
        public bool Connected = true;
        public int Connects, Disconnects, Fetches, Disposals;
        public Exception? Error;
        public TaskCompletionSource<bool>? Block;
        public TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LastToken;
        public DateTimeOffset LastStart, LastEnd;
        public string[] Selected = [];
        public List<string> Order = [];
        public IReadOnlyList<CalendarEvent> Events = Array.Empty<CalendarEvent>();
        public IReadOnlyList<CalendarInfo> Listed = [new("other", "Other", "blue", false), new("default", "Main", "green", true)];
        public Task<CalendarAccount?> GetAccountAsync(CancellationToken token) => Task.FromResult(Connected ? new CalendarAccount("id", "person@example.test") : null);
        public Task<CalendarAccount> ConnectAsync(CancellationToken token)
        { Connects++; Connected = true; Order.Add("connect"); return Task.FromResult(new CalendarAccount("id", "person@example.test")); }
        public Task DisconnectAsync(CancellationToken token) { Disconnects++; Connected = false; return Task.CompletedTask; }
        public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken token)
        { Order.Add("calendars"); return Task.FromResult(Listed); }
        public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(IReadOnlyList<string> calendarIds, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
        {
            Interlocked.Increment(ref Fetches);
            LastToken = token;
            LastStart = start;
            LastEnd = end;
            Selected = calendarIds.ToArray();
            Order.Add("events");
            Started.TrySetResult(true);
            if (Block is not null) await Block.Task;
            if (Error is not null) throw Error;
            return Events;
        }
        public void Dispose() { Disposals++; }
    }
}
