using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StageManager.Card3DPrototype.Calendar;

// Local checklist only. Never sends writes to Graph or stores event text/URLs/account names.
internal sealed class CalendarCompletionStore
{
    private readonly string _path;
    private readonly Dictionary<string, DateTimeOffset> _completed = new(StringComparer.Ordinal);
    public CalendarCompletionStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stage_Manager_Lai", "Calendar", "completion.json");
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 1024 * 1024) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_path));
            if (saved is null) return;
            foreach (var (key, date) in saved.Take(10000))
                if (key.Length == 64 && key.All(Uri.IsHexDigit) && date >= DateTimeOffset.UtcNow.AddDays(-90))
                    _completed[key] = date;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
    }
    public static string Key(CalendarEvent item)
    {
        static string Part(string value) => value.Length + ":" + value;
        var identity = Part(item.CalendarId) + Part(item.Id) +
            Part(item.IsAllDay ? item.StartDate.ToString("yyyy-MM-dd") : item.Start.ToUniversalTime().ToString("O"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    public IReadOnlySet<string> Keys => _completed.Keys.ToHashSet(StringComparer.Ordinal);
    public bool IsCompleted(CalendarEvent item) => _completed.ContainsKey(Key(item));
    public bool Set(CalendarEvent item, bool completed)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _completed.Where(p => p.Value < now.AddDays(-90)).Select(p => p.Key).ToArray()) _completed.Remove(key);
        if (completed) _completed[Key(item)] = now; else _completed.Remove(Key(item));
        while (_completed.Count > 10000) _completed.Remove(_completed.MinBy(p => p.Value).Key);
        return Save();
    }
    public bool Clear() { _completed.Clear(); return Save(); }
    private bool Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temp, JsonSerializer.Serialize(_completed));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
