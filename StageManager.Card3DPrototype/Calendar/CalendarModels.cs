namespace StageManager.Card3DPrototype.Calendar;

internal enum CalendarStatus { Disabled, NotConfigured, NotConnected, Loading, Connected, Stale, AuthRequired, Error }
internal sealed record CalendarAccount(string Id, string DisplayName);
internal sealed record CalendarInfo(string Id, string Name, string Color, bool IsDefault);
internal sealed record CalendarEvent(string Id, string CalendarId, string Subject,
    DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, DateOnly StartDate, DateOnly EndDate,
    string Location, string Organizer, string Summary, string WebLink);
internal sealed record CalendarSnapshot(CalendarStatus Status, IReadOnlyList<CalendarEvent> Events,
    DateTimeOffset? LastSuccess, string ErrorCode = "")
{
    public static CalendarSnapshot Empty(CalendarStatus status) => new(status, Array.Empty<CalendarEvent>(), null);
}
internal interface ICalendarProvider : IDisposable
{
    Task<CalendarAccount?> GetAccountAsync(CancellationToken token);
    Task<CalendarAccount> ConnectAsync(CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
    Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken token);
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(IReadOnlyList<string> calendarIds,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token);
}
internal sealed class CalendarProviderException(string code, TimeSpan? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
internal static class CalendarDates
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);
    public static DateOnly GridStart(DateOnly date)
    {
        var first = MonthStart(date);
        return first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
    }
    public static DateTimeOffset LocalBoundary(DateOnly date)
    {
        var time = date.ToDateTime(TimeOnly.MinValue);
        while (TimeZoneInfo.Local.IsInvalidTime(time)) time = time.AddMinutes(1);
        return new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time));
    }
    public static bool OnDay(CalendarEvent item, DateOnly date) => item.IsAllDay
        ? item.StartDate <= date && date < item.EndDate
        : item.Start < LocalBoundary(date.AddDays(1)) && item.End > LocalBoundary(date);
    public static IEnumerable<CalendarEvent> ForDay(IEnumerable<CalendarEvent> events, DateOnly date) =>
        events.Where(e => OnDay(e, date)).OrderByDescending(e => e.IsAllDay).ThenBy(e => e.Start).ThenBy(e => e.Id);
}
