using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StageManager.Card3DPrototype.Updates;

internal sealed record ReleaseAsset(string Name, Uri Url, long Size);
internal sealed record UpdateRelease(Version Version, string Tag, string Notes, DateTimeOffset Published,
    Uri Page, IReadOnlyList<ReleaseAsset> Assets);

internal sealed class ReleaseUpdateService : IDisposable
{
    private readonly HttpClient _http;
    public ReleaseUpdateService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Stage-Manager-Lai/" + AppVersion.Current);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public async Task<UpdateRelease> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var bytes = await GetBytesAsync(new Uri($"https://api.github.com/repos/{AppVersion.Repository}/releases/latest"), 2 * 1024 * 1024, timeout.Token);
        return Parse(bytes);
    }
    internal static UpdateRelease Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes); var r = doc.RootElement;
        if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Not a stable release.");
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version)) throw new InvalidDataException("Invalid release version.");
        var page = new Uri(r.GetProperty("html_url").GetString()!);
        if (page.AbsoluteUri != $"{AppVersion.ReleasesUrl}/tag/{tag}") throw new InvalidDataException("Unexpected release origin.");
        var assets = r.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(a.GetProperty("name").GetString()!,
            new Uri(a.GetProperty("browser_download_url").GetString()!), a.GetProperty("size").GetInt64())).ToArray();
        foreach (var asset in assets)
            if (asset.Size <= 0 || asset.Size > 600L * 1024 * 1024 || asset.Name != Path.GetFileName(asset.Name) ||
                asset.Url.AbsoluteUri != $"{AppVersion.ReleasesUrl}/download/{tag}/{Uri.EscapeDataString(asset.Name)}")
                throw new InvalidDataException("Unexpected release asset.");
        return new(version, tag, r.GetProperty("body").GetString() ?? "", r.GetProperty("published_at").GetDateTimeOffset(), page, assets);
    }
    public async Task<UpdateTransaction> DownloadAsync(UpdateRelease release, IProgress<int> progress, CancellationToken token)
    {
        if (release.Version <= AppVersion.Current) throw new InvalidOperationException("Not a newer version.");
        var name = $"Stage_Manager_Lai_{release.Tag}.exe";
        ReleaseAsset Asset(string n) => release.Assets.SingleOrDefault(a => a.Name == n) ?? throw new InvalidDataException("Release lacks " + n);
        var exe = Asset(name); var sums = Asset("SHA256SUMS.txt"); var manifest = Asset("build-info.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var sumBytes = await GetBytesAsync(sums.Url, 64 * 1024, ct);
        var infoBytes = await GetBytesAsync(manifest.Url, 64 * 1024, ct);
        var hash = VerifyManifest(release.Tag, name, sumBytes, infoBytes);
        var transaction = await Task.Run(() => UpdateTransaction.Create(Environment.ProcessPath!, hash, release.Version.ToString()), ct);
        try
        {
            using var response = await SendAsync(exe.Url, ct);
            if (response.Content.Headers.ContentLength is { } length && length != exe.Size) throw new InvalidDataException("Asset size mismatch.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(transaction.Payload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long count = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct); if (read == 0) break;
                    count += read; if (count > exe.Size) throw new InvalidDataException("Oversized asset.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); progress.Report((int)(count * 100 / exe.Size));
                }
                if (count != exe.Size) throw new InvalidDataException("Truncated asset.");
            }
            await Task.Run(() => transaction.ValidatePayload(), ct);
            ct.ThrowIfCancellationRequested();
            transaction.Save(); return transaction;
        }
        catch
        {
            if (File.Exists(transaction.Payload)) File.Delete(transaction.Payload);
            throw;
        }
    }
    internal static string VerifyManifest(string tag, string name, byte[] sums, byte[] manifest)
    {
        using var doc = JsonDocument.Parse(manifest); var r = doc.RootElement;
        var hash = r.GetProperty("sha256").GetString()?.ToLowerInvariant() ?? "";
        if (!Regex.IsMatch(hash, "^[0-9a-f]{64}$") || r.GetProperty("tag").GetString() != tag || r.GetProperty("file").GetString() != name)
            throw new InvalidDataException("Manifest mismatch.");
        var matches = System.Text.Encoding.UTF8.GetString(sums).TrimStart('\uFEFF').Split('\n')
            .Where(line => Regex.IsMatch(line.Trim(), "^" + hash + @"\s+\*?" + Regex.Escape(name) + "$", RegexOptions.IgnoreCase)).Count();
        if (matches != 1) throw new InvalidDataException("Checksum manifest mismatch.");
        return hash;
    }
    private async Task<byte[]> GetBytesAsync(Uri uri, int max, CancellationToken token)
    {
        using var response = await SendAsync(uri, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        { if (output.Length + read > max) throw new InvalidDataException("Response too large."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken token)
    {
        for (var i = 0; i < 5; i++)
        {
            if (!AllowedHost(uri)) throw new InvalidDataException("Untrusted update host.");
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            { uri = next.IsAbsoluteUri ? next : new Uri(uri, next); response.Dispose(); continue; }
            if (!response.IsSuccessStatusCode)
            { var status = response.StatusCode; response.Dispose(); throw new HttpRequestException("Update service HTTP " + (int)status, null, status); }
            return response;
        }
        throw new InvalidDataException("Too many redirects.");
    }
    internal static bool AllowedHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    public void Dispose() => _http.Dispose();
}
