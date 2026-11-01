using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace StageManager.Card3DPrototype.Calendar;

// Deliberately independent of the application's AI/network clients and diagnostics.
internal sealed class OutlookCalendarProvider : ICalendarProvider
{
    private static readonly string[] Scopes = ["Calendars.Read"];
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private const int MaxPages = 100;
    private readonly IPublicClientApplication app;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim authGate = new(1, 1);
    private readonly object cacheGate = new();
    private readonly string cachePath;
    private readonly byte[] entropy;
    private IAccount? account;
    private bool disposed;

    public OutlookCalendarProvider(string clientId, string? dataDirectory = null)
    {
        if (!Guid.TryParse(clientId, out var id) || id == Guid.Empty)
            throw new CalendarProviderException("NotConfigured");
        cachePath = Path.Combine(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai", "Calendar"),
            $"outlook-{id:N}.bin");
        entropy = SHA256.HashData(Encoding.UTF8.GetBytes("StageManager.Calendar:" + id.ToString("N")));
        app = PublicClientApplicationBuilder.Create(id.ToString())
            .WithAuthority("https://login.microsoftonline.com/consumers")
            .WithRedirectUri("http://localhost")
            .Build();
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            lock (cacheGate)
            {
                try
                {
                    if (!File.Exists(cachePath)) return;
                    if (new FileInfo(cachePath).Length > MaxBodyBytes)
                        throw new CalendarProviderException("TokenCacheUnavailable");
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(cachePath), entropy, DataProtectionScope.CurrentUser);
                    try { args.TokenCache.DeserializeMsalV3(plain, shouldClearExistingCache: true); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                catch (Exception ex) when (ex is not CalendarProviderException)
                { throw new CalendarProviderException("TokenCacheUnavailable"); }
            }
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (cacheGate)
            {
                string? temp = null;
                byte[]? plain = null;
                try
                {
                    plain = args.TokenCache.SerializeMsalV3();
                    var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, encrypted);
                    File.Move(temp, cachePath, overwrite: true);
                }
                catch { throw new CalendarProviderException("TokenCacheUnavailable"); }
                finally
                {
                    if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                    try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
        });
    }

    public Task<CalendarAccount?> GetAccountAsync(CancellationToken token) => Safe(async () =>
    {
        await authGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            account ??= (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            token.ThrowIfCancellationRequested();
            return account is null ? null : Describe(account);
        }
        finally { authGate.Release(); }
    }, token);

    public Task<CalendarAccount> ConnectAsync(CancellationToken token) => Safe(async () =>
    {
        await authGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            var result = await app.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount).ExecuteAsync(token).ConfigureAwait(false);
            account = result.Account;
            return Describe(account);
        }
        finally { authGate.Release(); }
    }, token);

    public async Task DisconnectAsync(CancellationToken token)
    {
        await Safe(async () =>
        {
            await authGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckDisposed();
                try
                {
                    foreach (var item in await app.GetAccountsAsync().ConfigureAwait(false))
                        await app.RemoveAsync(item).ConfigureAwait(false);
                }
                finally
                {
                    account = null;
                    lock (cacheGate) { if (File.Exists(cachePath)) File.Delete(cachePath); }
                }
                return true;
            }
            finally { authGate.Release(); }
        }, token).ConfigureAwait(false);
    }

    private Task<AuthenticationResult> AcquireAsync(CancellationToken token) => Safe(async () =>
    {
        await authGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            account ??= (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (account is null) throw new CalendarProviderException("AuthRequired");
            return await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(token).ConfigureAwait(false);
        }
        finally { authGate.Release(); }
    }, token);

    public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken token) => Safe(async () =>
    {
        var auth = await AcquireAsync(token).ConfigureAwait(false);
        return await OwnedCalendarsAsync(auth, token).ConfigureAwait(false);
    }, token);

    private async Task<IReadOnlyList<CalendarInfo>> OwnedCalendarsAsync(AuthenticationResult auth, CancellationToken token)
    {
        var result = new List<CalendarInfo>();
        foreach (var item in await ReadPagesAsync(
            "https://graph.microsoft.com/v1.0/me/calendars?$select=id,name,color,isDefaultCalendar,owner&$top=100",
            auth.AccessToken, token).ConfigureAwait(false))
        {
            var owner = Text(Child(item, "owner"), "address");
            if (!IsOwnedCalendar(Flag(item, "isDefaultCalendar"), owner, auth.Account.Username)) continue;
            var id = Text(item, "id");
            if (id.Length != 0)
                result.Add(new(id, Text(item, "name"), Text(item, "color"), Flag(item, "isDefaultCalendar")));
        }
        if (result.Count == 0) throw new CalendarProviderException("CalendarsUnavailable");
        return result.AsReadOnly();
    }

    // /me's default is owned even when the Microsoft sign-in uses a Gmail address or personal alias.
    // Other calendars remain conservative; no profile lookup or additional User.Read scope is needed.
    internal static bool IsOwnedCalendar(bool isDefault, string owner, string username) => isDefault ||
        (!string.IsNullOrWhiteSpace(username) && string.Equals(owner, username, StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(IReadOnlyList<string> calendarIds,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token) => Safe(async () =>
    {
        if (end <= start || end - start > TimeSpan.FromDays(62))
            throw new CalendarProviderException("InvalidRange");
        var auth = await AcquireAsync(token).ConfigureAwait(false);
        // Revalidate ownership even when the caller supplies stale or untrusted saved IDs.
        var owned = (await OwnedCalendarsAsync(auth, token).ConfigureAwait(false)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (calendarIds.Count != 0 && !calendarIds.Any(owned.Contains))
            throw new CalendarProviderException("CalendarsUnavailable");
        var result = new List<CalendarEvent>();
        var seen = new HashSet<(string, string)>();
        foreach (var calendarId in calendarIds.Distinct(StringComparer.Ordinal).Where(owned.Contains))
        {
            var url = "https://graph.microsoft.com/v1.0/me/calendars/" + Uri.EscapeDataString(calendarId) +
                "/calendarView?startDateTime=" + Uri.EscapeDataString(start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) +
                "&endDateTime=" + Uri.EscapeDataString(end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) +
                "&$top=250&$select=id,subject,start,end,isAllDay,isCancelled,location,organizer,bodyPreview,webLink,originalStartTimeZone,originalEndTimeZone";
            foreach (var item in await ReadPagesAsync(url, auth.AccessToken, token).ConfigureAwait(false))
            {
                var parsed = ParseEvent(item, calendarId);
                if (parsed is not null && seen.Add((calendarId, parsed.Id))) result.Add(parsed);
                if (result.Count > 20000) throw new CalendarProviderException("ResponseTooLarge");
            }
        }
        return (IReadOnlyList<CalendarEvent>)result.OrderBy(e => e.Start).ThenBy(e => e.Id).ToArray();
    }, token);

    private async Task<List<JsonElement>> ReadPagesAsync(string url, string accessToken, CancellationToken token)
    {
        var items = new List<JsonElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            if (!TrustedGraphUri(url) || !visited.Add(url) || page >= MaxPages)
                throw new CalendarProviderException("InvalidResponse");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20)); // Includes streamed body, not just headers.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\"");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var retry = response.Headers.RetryAfter;
                TimeSpan? delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                throw new CalendarProviderException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "AuthRequired",
                    HttpStatusCode.Forbidden => "AccessDenied",
                    HttpStatusCode.TooManyRequests => "Throttled",
                    HttpStatusCode.ServiceUnavailable => "ServiceUnavailable",
                    _ => "HttpError"
                }, delay);
            }
            if (response.Content.Headers.ContentLength > MaxBodyBytes) throw new CalendarProviderException("ResponseTooLarge");
            using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxBodyBytes) throw new CalendarProviderException("ResponseTooLarge");
                buffer.Write(chunk, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (!json.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new CalendarProviderException("InvalidResponse");
            foreach (var value in values.EnumerateArray())
            {
                if (items.Count >= 20000) throw new CalendarProviderException("ResponseTooLarge");
                items.Add(value.Clone());
            }
            url = Text(json.RootElement, "@odata.nextLink");
            if (url.Length == 0) return items;
        }
    }

    internal static bool TrustedGraphUri(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.StartsWith("/v1.0/me/", StringComparison.Ordinal);

    internal static CalendarEvent? ParseEvent(JsonElement item, string calendarId)
    {
        if (Flag(item, "isCancelled")) return null;
        var id = Text(item, "id");
        if (id.Length == 0) throw new CalendarProviderException("InvalidResponse");
        var start = ParseTime(Child(item, "start"));
        var end = ParseTime(Child(item, "end"));
        var allDay = Flag(item, "isAllDay");
        var startDate = DateOnly.FromDateTime(start.LocalDateTime);
        var endDate = DateOnly.FromDateTime(end.LocalDateTime);
        if (allDay)
        {
            // Graph returns UTC after Prefer. Restore the original calendar dates, never the machine's dates.
            startDate = AllDayDate(start, Text(item, "originalStartTimeZone"), Child(item, "start"));
            endDate = AllDayDate(end, Text(item, "originalEndTimeZone"), Child(item, "end"));
            if (endDate <= startDate) throw new CalendarProviderException("InvalidResponse");
        }
        if (end < start) throw new CalendarProviderException("InvalidResponse");
        var link = Text(item, "webLink");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !(uri.Host.Equals("outlook.live.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("outlook.office.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("outlook.office365.com", StringComparison.OrdinalIgnoreCase))) link = "";
        var organizer = Child(Child(item, "organizer"), "emailAddress");
        return new(id, calendarId, Text(item, "subject"), start, end, allDay, startDate, endDate,
            Text(Child(item, "location"), "displayName"), Text(organizer, "name"), Text(item, "bodyPreview"), link);
    }

    private static DateOnly AllDayDate(DateTimeOffset utc, string originalZone, JsonElement value)
    {
        var zone = originalZone.Length == 0 ? Text(value, "timeZone") : originalZone;
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime); }
        catch { throw new CalendarProviderException("InvalidResponse"); }
    }

    private static DateTimeOffset ParseTime(JsonElement value)
    {
        var text = Text(value, "dateTime");
        var zone = Text(value, "timeZone");
        if (zone.Equals("UTC", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(text,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) return utc;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new CalendarProviderException("InvalidResponse");
        try { return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById(zone))); }
        catch { throw new CalendarProviderException("InvalidResponse"); }
    }

    private static JsonElement Child(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
    private static string Text(JsonElement item, string name) { var value = Child(item, name); return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; }
    private static bool Flag(JsonElement item, string name) => Child(item, name).ValueKind == JsonValueKind.True;
    private static CalendarAccount Describe(IAccount item) => new(item.HomeAccountId.Identifier, item.Username);
    private void CheckDisposed() { if (disposed) throw new CalendarProviderException("Unavailable"); }

    private static async Task<T> Safe<T>(Func<Task<T>> action, CancellationToken token)
    {
        try { return await action().ConfigureAwait(false); }
        catch (CalendarProviderException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new CalendarProviderException("Timeout"); }
        catch (MsalUiRequiredException) { throw new CalendarProviderException("AuthRequired"); }
        catch (MsalException ex) when (ex.ErrorCode == "authentication_canceled") { throw new CalendarProviderException("AuthenticationCancelled"); }
        catch (MsalException) { throw new CalendarProviderException("AuthenticationFailed"); }
        catch (HttpRequestException) { throw new CalendarProviderException("NetworkUnavailable"); }
        catch (JsonException) { throw new CalendarProviderException("InvalidResponse"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new CalendarProviderException("TokenCacheUnavailable"); }
        catch { throw new CalendarProviderException("Unavailable"); }
    }

    public void Dispose() { disposed = true; http.Dispose(); }
}
