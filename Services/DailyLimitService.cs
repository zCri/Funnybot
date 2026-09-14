using System.Text.Json;
using System.Text.Json.Serialization;

namespace Funnybot.Services;

public sealed record AddedTrack(
    ulong UserId,
    string Username,
    string TrackId,
    string TrackName,
    string Artists,
    string SpotifyUrl,
    DateTime AddedAtUtc);

public sealed class DailyLimitService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, Dictionary<string, AddedTrack>> _data = [];
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DailyLimitService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public static string TodayKey() => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return;
            _data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AddedTrack>>>(json, JsonOpts)
                ?? [];
        }
        catch
        {
            try
            {
                if (File.Exists(_filePath))
                    File.Copy(_filePath, _filePath + ".bak", overwrite: true);
            }
            catch { }
            _data = [];
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOpts));
    }

    public bool HasAddedToday(ulong userId, out AddedTrack? existing)
    {
        var key = TodayKey();
        if (_data.TryGetValue(key, out var day) && day.TryGetValue(userId.ToString(), out var rec))
        {
            existing = rec;
            return true;
        }
        existing = null;
        return false;
    }

    public IReadOnlyList<AddedTrack> GetToday()
    {
        var key = TodayKey();
        if (!_data.TryGetValue(key, out var day)) return Array.Empty<AddedTrack>();
        return day.Values.OrderBy(x => x.AddedAtUtc).ToList();
    }

    public async Task<(bool Ok, AddedTrack? Existing)> TryAddTodayAsync(AddedTrack track, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var key = TodayKey();
            if (!_data.TryGetValue(key, out var day))
            {
                day = [];
                _data[key] = day;
            }
            if (day.TryGetValue(track.UserId.ToString(), out var existing))
                return (false, existing);

            day[track.UserId.ToString()] = track;
            Save();
            return (true, null);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> RemoveTodayAsync(ulong userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var key = TodayKey();
            if (_data.TryGetValue(key, out var day) && day.Remove(userId.ToString()))
            {
                Save();
                return true;
            }
            return false;
        }
        finally { _lock.Release(); }
    }
}
