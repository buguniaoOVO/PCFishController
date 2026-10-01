using System.Text.Json;

namespace PCFishController;

/// <summary>
/// 亲缘关系图谱：记录"谁是谁的孩子"，避免撮合直系亲子。
///
/// 依据（2026-09-26 实测）：服务端会拒绝直系亲子繁育——
///   ✗ f7e96ae0（孩子）× 7aa0d568（母亲）→「服务端拒绝繁育」
///   ✓ f7e96ae0 × 无亲缘的 fc083c8f → 成功
///   ✓ 半姐妹（共享单亲）配对 → 成功
/// 每次繁育成功，把新生儿与双亲各记一条边；选鱼时查表跳过。
/// 数据持久化在 exe 旁的「PCFish亲缘关系.json」，控制器重启不丢。
/// </summary>
internal sealed class RelationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<string>> _map = new();
    private readonly string _path;

    internal RelationStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>记录一对亲子边（双向存储，查表 O(1)）。</summary>
    internal void AddChild(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child) || parent == child) return;
        lock (_sync)
        {
            if (!_map.TryGetValue(parent, out var set)) _map[parent] = set = new HashSet<string>();
            set.Add(child);
            if (!_map.TryGetValue(child, out var set2)) _map[child] = set2 = new HashSet<string>();
            set2.Add(parent);
        }
    }

    /// <summary>两个体是否直系亲子。</summary>
    internal bool Related(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        lock (_sync)
        {
            return _map.TryGetValue(a, out var set) && set.Contains(b);
        }
    }

    /// <summary>清掉已不在鱼群里的个体（被合成消耗/移除的鱼），防止文件无限膨胀。</summary>
    internal void Prune(HashSet<string> livingIds)
    {
        lock (_sync)
        {
            var dead = _map.Keys.Where(id => !livingIds.Contains(id)).ToList();
            foreach (var id in dead)
            {
                foreach (var set in _map.Values) set.Remove(id);
                _map.Remove(id);
            }
        }
    }

    internal void Save()
    {
        try
        {
            lock (_sync)
            {
                var opt = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_path, JsonSerializer.Serialize(_map, opt));
            }
        }
        catch
        {
            // 存不上也不影响繁育，最多重启后重新积累
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(json);
            if (data == null) return;
            lock (_sync)
            {
                foreach (var kv in data) _map[kv.Key] = kv.Value;
            }
        }
        catch
        {
        }
    }
}
