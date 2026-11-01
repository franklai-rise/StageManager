using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StageManager.Card3DPrototype.Calendar;

internal sealed class CodexCalendarProvider : ICalendarProvider
{
    internal sealed record CachedRange(DateOnly Start, DateOnly End, DateTimeOffset UpdatedAt, CalendarEvent[] Events, string[] CalendarIds);
    internal sealed record Cache(CalendarInfo[] Calendars, CachedRange[] Ranges);
    private readonly string _cli, _path;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Stage_Manager_Lai.Calendar.Codex.v1");
    private readonly SemaphoreSlim _sync = new(1, 1);
    private Cache _cache = new([], []);
    public DateTimeOffset? LastSuccess { get; private set; }
    public CodexCalendarProvider(string cli, string? directory = null)
    {
        _cli = cli;
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "Calendar"), "codex-calendar.bin");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= 4 * 1024 * 1024)
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                try { _cache = JsonSerializer.Deserialize<Cache>(plain) ?? new([], []); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (_cache.Ranges is null || _cache.Calendars is null || _cache.Ranges.Length > 3 || _cache.Calendars.Length > 128 ||
                    _cache.Ranges.Any(r => r is null || r.Events is null || r.CalendarIds is null || r.Start >= r.End || r.Events.Length > 10000)) _cache = new([], []);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { _cache = new([], []); }
    }
    public Task<CalendarAccount?> GetAccountAsync(CancellationToken token) => Task.FromResult<CalendarAccount?>(new("codex-outlook", "Codex · Outlook"));
    public Task<CalendarAccount> ConnectAsync(CancellationToken token) => Task.FromResult(new CalendarAccount("codex-outlook", "Codex · Outlook"));
    public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken token)
    {
        if (_cache.Calendars.Length == 0) throw new CalendarProviderException("CodexRefreshRequired");
        return Task.FromResult<IReadOnlyList<CalendarInfo>>(_cache.Calendars);
    }
    public Task DisconnectAsync(CancellationToken token)
    {
        if (File.Exists(_path)) File.Delete(_path);
        _cache = new([], []); LastSuccess = null;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(IReadOnlyList<string> calendarIds, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var first = DateOnly.FromDateTime(start.LocalDateTime);
        var last = DateOnly.FromDateTime(end.LocalDateTime);
        var cached = _cache.Ranges.Where(r => r.Start <= first && r.End >= last && calendarIds.All(id => r.CalendarIds.Contains(id, StringComparer.Ordinal))).MaxBy(r => r.UpdatedAt)
            ?? throw new CalendarProviderException("CodexRefreshRequired");
        LastSuccess = cached.UpdatedAt;
        return Task.FromResult<IReadOnlyList<CalendarEvent>>(cached.Events.Where(e => calendarIds.Contains(e.CalendarId, StringComparer.Ordinal) &&
            (e.IsAllDay ? e.StartDate < last && e.EndDate > first : e.Start < end && e.End > start)).ToArray());
    }
    internal async Task SyncAsync(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<string> ids, CancellationToken token)
    {
        await _sync.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var ct = deadline.Token;
            await using var client = await CodexCalendarClient.OpenAsync(_cli, ct).ConfigureAwait(false);
            var listed = await client.CallAsync("list_calendars", new { }, ct).ConfigureAwait(false);
            if (listed.TryGetProperty("next_link", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString()))
                throw new CalendarProviderException("InvalidResponse");
            var rows = listed.GetProperty("value").EnumerateArray().ToArray();
            var owner = rows.FirstOrDefault(e => Flag(e, "is_default_calendar"));
            var address = Text(Child(owner, "owner"), "address");
            var calendars = rows.Where(e => Flag(e, "is_default_calendar") || address.Length > 0 && Text(Child(e, "owner"), "address").Equals(address, StringComparison.OrdinalIgnoreCase))
                .Select(e => new CalendarInfo(Text(e, "id"), Text(e, "name"), "", Flag(e, "is_default_calendar"))).Take(128).ToArray();
            var selected = ids.Count == 0 ? calendars.Where(c => c.IsDefault).Take(1).ToArray() : calendars.Where(c => ids.Contains(c.Id, StringComparer.Ordinal)).ToArray();
            if (selected.Length == 0) throw new CalendarProviderException("CalendarsUnavailable");
            var events = new Dictionary<(string, string), CalendarEvent>();
            foreach (var calendar in selected)
            {
                string? cursor = null;
                var cursors = new HashSet<string>();
                for (int page = 0; ; page++)
                {
                    if (page >= 100) throw new CalendarProviderException("ResponseTooLarge");
                    var response = await client.CallAsync("search_events", new { calendar_id = calendar.Id, query = "", size = 100, cursor,
                        filters = new { start = start.ToString("O"), end = end.ToString("O") } }, ct).ConfigureAwait(false);
                    var found = response.GetProperty("results").EnumerateArray().ToArray();
                    foreach (var batch in found.Chunk(20))
                    {
                        var details = await client.CallAsync("fetch_events_batch", new { calendar_id = calendar.Id, event_ids = batch.Select(e => Text(e, "id")).ToArray(), batch_size = 20 }, ct).ConfigureAwait(false);
                        if (details.TryGetProperty("batch_failures", out var failures) && failures.ValueKind == JsonValueKind.Array && failures.GetArrayLength() != 0)
                            throw new CalendarProviderException("CodexCalendarReadFailed");
                        var payload = details.GetProperty("value");
                        var values = payload.ValueKind == JsonValueKind.Object ? payload.EnumerateObject().Select(p => p.Value).ToArray() : payload.EnumerateArray().ToArray();
                        if (values.Length != batch.Length) throw new CalendarProviderException("InvalidResponse");
                        foreach (var value in values)
                            if (ParseEvent(value, calendar.Id) is { } item) events[(calendar.Id, item.Id)] = item;
                        if (events.Count > 10000) throw new CalendarProviderException("ResponseTooLarge");
                    }
                    if (Flag(response, "is_complete")) break;
                    if (!Flag(response, "has_more")) throw new CalendarProviderException("InvalidResponse");
                    cursor = Text(response, "next_cursor");
                    if (cursor.Length == 0 || !cursors.Add(cursor)) throw new CalendarProviderException("InvalidResponse");
                }
            }
            var first = DateOnly.FromDateTime(start.LocalDateTime); var last = DateOnly.FromDateTime(end.LocalDateTime);
            var range = new CachedRange(first, last, DateTimeOffset.UtcNow, events.Values.ToArray(), selected.Select(c => c.Id).ToArray());
            var updated = new Cache(calendars, _cache.Ranges.Where(r => r.Start != first || r.End != last).Append(range).OrderByDescending(r => r.UpdatedAt).Take(3).ToArray());
            ct.ThrowIfCancellationRequested();
            Save(updated, ct); _cache = updated; LastSuccess = range.UpdatedAt;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new CalendarProviderException("Timeout"); }
        finally { _sync.Release(); }
    }
    internal static CalendarEvent? ParseEvent(JsonElement value, string calendarId)
    {
        if (Flag(value, "is_cancelled")) return null;
        var start = Time(Child(value, "start")); var end = Time(Child(value, "end"));
        var id = Text(value, "id");
        if (id.Length == 0 || end < start) throw new CalendarProviderException("InvalidResponse");
        bool all = Flag(value, "is_all_day");
        // All-day connector date fields are calendar dates, not instants to shift into the PC zone.
        var a = all ? DateOnly.Parse(Text(Child(value, "start"), "dateTime")[..10], CultureInfo.InvariantCulture) : DateOnly.FromDateTime(start.LocalDateTime);
        var b = all ? DateOnly.Parse(Text(Child(value, "end"), "dateTime")[..10], CultureInfo.InvariantCulture) : DateOnly.FromDateTime(end.LocalDateTime);
        var link = Text(value, "web_link");
        if (!CalendarPresentation.TryOutlookLink(link, out _)) link = "";
        return new(id, calendarId, Text(value, "subject"), start, end, all, a, b, Text(Child(value, "location"), "displayName"),
            Text(Child(Child(value, "organizer"), "emailAddress"), "name"), Text(value, "bodyPreview"), link);
    }
    private static DateTimeOffset Time(JsonElement value)
    {
        var time = Text(value, "dateTime"); var zone = Text(value, "timeZone");
        if (zone.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return DateTimeOffset.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var local = DateTime.SpecifyKind(DateTime.Parse(time, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        return new(TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.FindSystemTimeZoneById(zone)));
    }
    private void Save(Cache cache, CancellationToken token)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(cache);
        try
        {
            if (plain.Length > 4 * 1024 * 1024) throw new CalendarProviderException("ResponseTooLarge");
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, encrypted); token.ThrowIfCancellationRequested(); File.Move(temp, _path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static JsonElement Child(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement e, string name) => Child(e, name).ValueKind == JsonValueKind.String ? Child(e, name).GetString()! : "";
    private static bool Flag(JsonElement e, string name) => Child(e, name).ValueKind == JsonValueKind.True;
    public void Dispose() { }
}
