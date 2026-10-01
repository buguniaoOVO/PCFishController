using System.Globalization;
using System.Text.Json.Serialization;

namespace PCFishController;

/// <summary>桥接推过来的一条报文。字段名跟 BridgeServer 的 JSON 一致。</summary>
internal sealed class BridgeMessage
{
    public string type { get; set; }
    public string ver { get; set; }
    public int port { get; set; }
    public int minInterval { get; set; }

    public string cmd { get; set; }
    public bool ok { get; set; }
    public bool isNew { get; set; }
    public string id { get; set; }
    public string msg { get; set; }
    public string errorKind { get; set; }
    public bool gameBusy { get; set; }
    public bool inputBlocked { get; set; }
    public bool networkBusy { get; set; }
    public bool functionWindowOpen { get; set; }
    public bool serverInventoryReady { get; set; }
    public int actionWaitSeconds { get; set; }

    public bool game { get; set; }
    public bool armed { get; set; }
    public bool cfg { get; set; }
    public int actions { get; set; }

    public int total { get; set; }
    public int breedable { get; set; }
    public int exhausted { get; set; }
    public int maxMerge { get; set; }
    public string summary { get; set; }

    /// <summary>繁育计数器 = 鱼缸上方计数器的数量（GameDataManager.breedCount）。</summary>
    public int charge { get; set; }

    /// <summary>鱼缸等级（截图里的「鱼缸等级 5」）。</summary>
    public int tankLevel { get; set; }

    /// <summary>鱼缸当前经验（截图里的「230/1000」分子）。</summary>
    public int tankExp { get; set; }

    /// <summary>游戏自己判定现在能不能升级（决定 LV↑ 按钮亮不亮）。</summary>
    public bool canLevelUp { get; set; }

    /// <summary>距下一颗心恢复的估计秒数。</summary>
    public int secToHeart { get; set; }

    /// <summary>繁殖成功后是否正在等主线程重画游戏界面（那排心）。</summary>
    public bool hudPending { get; set; }

    /// <summary>hud 报文里带的重画细节。</summary>
    public string detail { get; set; }

    /// <summary>窗口自检：动作是否已被锁停（窗口内容疑似被破坏时置位）。</summary>
    public bool windowLocked { get; set; }

    /// <summary>窗口自检：锁停原因。</summary>
    public string windowLockReason { get; set; }

    /// <summary>窗口自检：连续通过次数。</summary>
    public int windowChecksOk { get; set; }

    /// <summary>window 报文：功能窗口是否处于打开状态。</summary>
    public bool open { get; set; }

    /// <summary>window 报文：功能窗口内活跃节点数量。</summary>
    public int nodes { get; set; }

    /// <summary>window 报文：快照是否可用。</summary>
    public bool valid { get; set; }

    /// <summary>window 报文：容器根节点名。</summary>
    public string root { get; set; }

    public int count { get; set; }
    public List<FishDto> fish { get; set; }
    public List<string> collectionTypes { get; set; }
}

/// <summary>一条鱼。全部来自游戏自身的 FishModel，控制器只读。</summary>
internal sealed class FishDto
{
    public string id { get; set; }
    public string sp { get; set; }   // 完整鱼种，形如 FS00010_02_01_01
    public string ty { get; set; }   // 鱼型，形如 FS00010
    public int g { get; set; }       // 兼容字段：星级（旧桥接也把 FishModel.Grade 放在这里）
    public int lv { get; set; }      // 兼容字段：稀有度档位（旧桥接把 FishModel.Level 放在这里）
    public int tier { get; set; } = -1; // 0 基础，1 普通，2 高级，3 稀有，4 传说，5 神话
    public int stars { get; set; } = -1; // 1-5 星；基础鱼为 0
    public int variant { get; set; } // 同一鱼种的外观/变体序号
    public int gr { get; set; }      // 成长
    public int bc { get; set; }      // 剩余繁殖次数
    public int bm { get; set; }      // 繁殖次数上限
    public int serverBc { get; set; } = -1; // -1 未核对，-2 服务器无此鱼；其余为服务器剩余次数
    public bool pl { get; set; }     // 是否摆在展示缸
    public bool lk { get; set; }     // 是否被玩家锁定
    public string nx { get; set; }   // 下次可繁殖时间

    public int RarityTier => tier >= 0 ? tier : Math.Max(0, lv);

    [JsonIgnore]
    public int Stars
    {
        get
        {
            if (stars is >= 1 and <= 5) return stars;
            if (g is >= 1 and <= 5) return g;
            var parts = sp?.Split('_');
            return parts?.Length == 4 && int.TryParse(parts[3], out var value) && value is >= 1 and <= 5 ? value : 0;
        }
    }

    [JsonIgnore]
    public string Name => FishCatalog.Name(ty);

    public override string ToString()
        => $"{id} {Name} {FishCatalog.RarityName(RarityTier)} {Stars}星 繁育{bc}/{bm} 展示={pl} 锁定={lk}";
}

/// <summary>
/// 选鱼规则。从插件里搬过来的——决策放在游戏外，改规则不用重启游戏。
///
/// 本地状态与只读核对的服务器剩余次数共同筛选繁育候选。
/// </summary>
internal static class Selection
{
    internal static bool CanBreed(FishDto f) => f != null && !string.IsNullOrWhiteSpace(f.id)
        && f.bc > 0 && (f.serverBc == -1 || f.serverBc > 0) && !f.lk && !f.pl;

    /// <summary>次数耗尽且没锁定、没摆出来的鱼，才是合成的材料候选。</summary>
    internal static bool CanMerge(FishDto f) => f.bc <= 0 && !f.lk && !f.pl;

    /// <summary>
    /// 繁殖：挑两条还有繁殖次数、且已过冷却的鱼。
    ///
    /// **优先用高级的鱼** —— 这是用户明确要求的第一优先级：
    ///   1. 稀有度 G 降序（越高级越先用）
    ///   2. 等级 Lv 降序（同级里先用练过的）
    ///   3. 剩余繁殖次数 降序（次数多的先消耗，保留次数少的做后手）
    ///
    /// relations：亲缘图谱。服务端会拒绝直系亲子繁育（实测），有边就跳过。
    /// 半姐妹/半兄弟（只共享一个亲）实测允许，不拦。
    ///
    /// minGrade 是下限门槛（0 = 不限）。注意门槛和优先级是两件事：
    /// 门槛决定"谁有资格"，排序决定"先从谁开始"。
    /// </summary>
    internal static bool TryPickBreedPair(List<FishDto> fish, int minGrade, RelationStore relations,
        FailedPairStore failedPairs, IReadOnlySet<string> avoidedFishIds, GoalPlan goalPlan,
        out FishDto first, out FishDto second)
    {
        first = second = null;
        if (fish == null) return false;

        var candidates = fish
            .Where(f => CanBreed(f) && f.RarityTier >= minGrade && IsOffCooldown(f) &&
                        (avoidedFishIds == null || !avoidedFishIds.Contains(f.id)))
            .OrderByDescending(f => goalPlan?.Score(f) ?? 0)
            .ThenByDescending(f => f.RarityTier)
            .ThenByDescending(f => f.Stars)
            .ThenByDescending(f => f.bc)
            .ToList();

        if (candidates.Count < 2) return false;
        var preferred = failedPairs == null ? candidates :
            candidates.Where(f => !failedPairs.IsFishBlocked(f)).ToList();
        var fallback = failedPairs == null ? candidates : candidates
            .OrderBy(f => failedPairs.FailureCount(f))
            .ThenByDescending(f => goalPlan?.Score(f) ?? 0)
            .ThenByDescending(f => f.RarityTier)
            .ThenByDescending(f => f.Stars)
            .ThenByDescending(f => f.bc)
            .ToList();
        // 优先避开近期反复失败的鱼；若剩下的鱼凑不出配对，仍尝试未被记录为失败的其他配对。
        foreach (var pool in new[] { preferred, fallback })
        {
            foreach (var a in pool)
            {
                foreach (var b in pool)
                {
                    if (b.id == a.id) continue;
                    if (relations != null && relations.Related(a.id, b.id)) continue;
                    if (failedPairs != null && failedPairs.IsBlocked(a, b)) continue;
                    first = a;
                    second = b;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>可繁殖鱼里最高稀有度是多少，用来给用户看"我这池子里最好的鱼是什么档"。</summary>
    internal static int HighestBreedableGrade(List<FishDto> fish)
    {
        if (fish == null) return -1;
        var best = -1;
        foreach (var f in fish)
        {
            if (CanBreed(f) && f.RarityTier > best) best = f.RarityTier;
        }
        return best;
    }

    /// <summary>
    /// 鱼的冷却时间。实测 BreedNextDatetime 有两种格式，必须都认：
    ///   ISO:  2026-09-24T06:51:27.2261310Z  (UTC)
    ///   本地: 2026-09-24 10:55:10
    /// 解析不出来时跳过，避免把未知冷却状态的鱼提交给服务端。
    /// </summary>
    internal static bool IsOffCooldown(FishDto f)
    {
        var remaining = CooldownRemaining(f);
        return remaining.HasValue && remaining.Value <= TimeSpan.Zero;
    }

    /// <summary>空冷却时间视为就绪；无法解析时返回 null，界面显示未知且自动选鱼跳过。</summary>
    internal static TimeSpan? CooldownRemaining(FishDto f)
    {
        if (string.IsNullOrWhiteSpace(f?.nx)) return TimeSpan.Zero;
        var next = ParseNextTime(f.nx);
        return next.HasValue ? next.Value - DateTime.Now : null;
    }

    private static DateTime? ParseNextTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 服务端的无时区格式实际也是 UTC：受控繁育后出现 09:32:47，
        // 当地时间为 17:25，若按本地时间解读会立即错误复用冷却中的新鱼。
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utcPlain))
        {
            return utcPlain.ToLocalTime();
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var utc)) return utc.LocalDateTime;

        return null;
    }

    /// <summary>合成分组键：鱼型 + 稀有度。口径是实测逼出来的，别乱改。</summary>
    internal static string GroupKey(FishDto f) => $"{f.ty}|T{f.RarityTier}";

    internal static string Summarize(List<FishDto> fish)
    {
        if (fish == null) return "(无数据)";
        return $"总数={fish.Count} 可繁殖={fish.Count(CanBreed)} 次数耗尽={fish.Count(CanMerge)}";
    }

    internal static string DescribeGrades(List<FishDto> fish)
    {
        if (fish == null || fish.Count == 0) return "";
        var parts = fish.GroupBy(f => f.RarityTier)
            .OrderBy(g => g.Key)
            .Select(g => $"{FishCatalog.RarityName(g.Key)}:{g.Count()}");
        return string.Join("  ", parts);
    }
}
