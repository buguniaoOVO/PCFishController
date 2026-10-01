using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Il2CppInterop.Runtime;

namespace PCFishAutoHelper;

/// <summary>只读获取服务器鱼群，校验繁育资格；不调用 ManageRestore 或写游戏模型。</summary>
internal static class ServerInventoryCache
{
    private static Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private static bool _refreshing;
    private static NANOOGame.Network.NetworkManager _reader;
    private static Il2CppSystem.Action<string> _response;
    private static Il2CppSystem.Action _failure;
    internal static bool Ready { get; private set; }

    internal static int CountFor(string id, int tier)
        => tier == 0 || !Ready ? -1 : _counts.TryGetValue(id ?? "", out var count) ? count : -2;

    internal static void NoteSuccess(string[] parents, string child, int childCount)
    {
        if (!Ready) return;
        foreach (var id in parents)
            if (_counts.TryGetValue(id, out var count)) _counts[id] = Math.Max(0, count - 1);
        if (!string.IsNullOrEmpty(child)) _counts[child] = childCount;
    }

    internal static void Invalidate() => Ready = false;

    private static string Key(string name)
        => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static void Collect(JsonElement node, Dictionary<string, int> fish, int depth, string inheritedId = null)
    {
        if (depth > 10) return;
        if (node.ValueKind == JsonValueKind.Object)
        {
            string id = inheritedId; int? count = null;
            foreach (var property in node.EnumerateObject())
            {
                var key = Key(property.Name);
                if (key is "id" or "fishid" or "code" or "uuid" or "uid" or "uniqueid")
                    if (Guid.TryParse(property.Value.ToString(), out _)) id = property.Value.ToString();
                if (key is "breedcount" or "bc")
                    if (int.TryParse(property.Value.ToString(), out var number)) count = number;
            }
            if (Guid.TryParse(id, out _) && count.HasValue) fish[id] = count.Value;
            foreach (var property in node.EnumerateObject())
                Collect(property.Value, fish, depth + 1, Guid.TryParse(property.Name, out _) ? property.Name : id);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) Collect(item, fish, depth + 1, inheritedId);
        else if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text) && (text[0] == '{' || text[0] == '['))
                try { using var nested = JsonDocument.Parse(text); Collect(nested.RootElement, fish, depth + 1, inheritedId); }
                catch (JsonException) { }
        }
    }

    private static void ClearRequest()
    { _reader = null; _response = null; _failure = null; _refreshing = false; }

    private static IEnumerable<string> Schema(JsonElement node, string path = "root", int depth = 0)
    {
        if (depth > 5) yield break;
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject().Take(24))
            {
                var childPath = path + "." + (Guid.TryParse(property.Name, out _) ? "{fishId}" : property.Name);
                yield return childPath + ":" + property.Value.ValueKind;
                foreach (var entry in Schema(property.Value, childPath, depth + 1)) yield return entry;
            }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray().Take(1))
                foreach (var entry in Schema(item, path + "[]", depth + 1)) yield return entry;
        else if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text) && (text[0] == '{' || text[0] == '['))
            {
                JsonDocument nested = null;
                try { nested = JsonDocument.Parse(text); } catch (JsonException) { }
                if (nested != null)
                {
                    using (nested)
                        foreach (var entry in Schema(nested.RootElement, path + "(json)", depth + 1))
                            yield return entry;
                }
            }
        }
    }

    internal static bool Refresh(Action<bool, string> completed, out string reason)
    {
        reason = "";
        if (_refreshing) { reason = "服务器库存核对仍在进行"; return false; }
        if (GameBridge.HasPendingBreed || !GameBridge.TryGetDataManager(out var dm))
        { reason = "繁育尚未完成或游戏数据未就绪"; return false; }
        if (GameBridge.ReadInteractionState().NetworkBusy ||
            string.IsNullOrWhiteSpace(NN.PF.Core.CommonData.accessToken))
        { reason = "游戏登录或网络请求尚未完成，稍后只读核对"; return false; }
        try
        {
            if (!NN.PF.Core.SingletonManager<NN.PF.Core.Network.NetworkManager>.HasInstance)
            { reason = "游戏网络尚未就绪"; return false; }
            var native = NN.PF.Core.SingletonManager<NN.PF.Core.Network.NetworkManager>.Instance;
            _reader = native.GetNetworkManager();
            _reader.AccessToken = NN.PF.Core.CommonData.accessToken;
            _refreshing = true;
            _response = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(
                new Action<string>(body => GameBridge.RunOnMainThread(() =>
                {
                    try
                    {
                        using var document = JsonDocument.Parse(body);
                        if (document.RootElement.TryGetProperty("status", out var status) &&
                            !string.Equals(status.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                        {
                            var messageCode = document.RootElement.TryGetProperty("messageCode", out var code)
                                ? code.ToString() : "";
                            throw new InvalidOperationException("服务器只读核对失败：status=" +
                                status.ToString() + " messageCode=" + messageCode);
                        }
                        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        Collect(document.RootElement, counts, 0);
                        if (counts.Count == 0)
                            throw new InvalidOperationException("服务器返回中没有可识别的鱼种次数字段；结构=" +
                                string.Join(",", Schema(document.RootElement).Take(45)));
                        _counts = counts;
                        Ready = true;
                        var local = GameBridge.Snapshot(dm);
                        var localIds = new HashSet<string>(local.Select(f => f.Id));
                        var differing = local.Where(f => f.Level > 0 && counts.TryGetValue(f.Id, out var n) &&
                            n != f.BreedCount).ToList();
                        var report = JsonSerializer.Serialize(new
                        {
                            serverFish = counts.Count, localFish = local.Count,
                            countMismatches = differing.Count,
                            localOnly = local.Count(f => f.Level > 0 && !counts.ContainsKey(f.Id)),
                            serverOnly = counts.Keys.Count(id => !localIds.Contains(id)),
                            examples = differing.Take(20).Select(f => new
                            { id = f.Id, species = f.Species, local = f.BreedCount, server = counts[f.Id] })
                        });
                        Journal.Write("服务器库存只读核对：" + report);
                        completed?.Invoke(true, report);
                    }
                    catch (Exception ex)
                    {
                        Ready = false;
                        Journal.Error("服务器库存只读核对失败", ex);
                        completed?.Invoke(false, ex.Message);
                    }
                    finally { ClearRequest(); }
                })));
            _failure = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                new Action(() => GameBridge.RunOnMainThread(() =>
                {
                    Ready = false;
                    completed?.Invoke(false, "服务器库存 GET 请求失败，未修改游戏存档");
                    ClearRequest();
                })));
            _reader.RequestGet(NN.PF.Core.Network.NetworkManager.URL_MANAGE_RESTORE, _response, _failure);
            return true;
        }
        catch (Exception ex)
        {
            Ready = false; ClearRequest(); reason = ex.Message;
            Journal.Error("发送服务器库存只读请求失败", ex);
            return false;
        }
    }
}
