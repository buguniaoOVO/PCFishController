using System.Text.Json;

namespace PCFishController;

/// <summary>短时间内跳过被拒绝的配对，避免在鱼的剩余次数变化后又尝试同一对。</summary>
internal sealed class FailedPairStore
{
    private readonly Dictionary<string, long> _until = new();
    private readonly string _path;

    internal FailedPairStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
                if (saved != null)
                    foreach (var (key, expiry) in saved)
                    {
                        var normalized = NormalizeOldKey(key);
                        if (normalized != null)
                            _until[normalized] = Math.Max(_until.GetValueOrDefault(normalized), expiry);
                    }
            }
        }
        catch { /* 损坏的缓存不妨碍助手启动 */ }
        RemoveExpired();
    }

    private static string NormalizeOldKey(string key)
    {
        var halves = key.Split('|');
        if (halves.Length != 2) return null;
        var left = halves[0].Split(':')[0];
        var right = halves[1].Split(':')[0];
        return string.CompareOrdinal(left, right) <= 0 ? $"{left}|{right}" : $"{right}|{left}";
    }

    private static string Key(FishDto a, FishDto b)
        => NormalizeOldKey($"{a.id}|{b.id}");

    internal bool IsBlocked(FishDto a, FishDto b)
        => _until.TryGetValue(Key(a, b), out var expiry) && expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal bool IsFishBlocked(FishDto fish)
        => FailureCount(fish) >= 2;

    internal int FailureCount(FishDto fish)
    {
        var token = fish.id;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _until.Count(p => p.Value > now &&
            (p.Key.StartsWith(token + "|", StringComparison.Ordinal) ||
             p.Key.EndsWith("|" + token, StringComparison.Ordinal)));
    }

    internal void Block(FishDto a, FishDto b, TimeSpan duration)
    {
        if (a == null || b == null) return;
        _until[Key(a, b)] = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeSeconds();
        RemoveExpired();
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_until, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* 缓存写入失败只影响下次启动后的选鱼 */ }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var key in _until.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _until.Remove(key);
    }
}
