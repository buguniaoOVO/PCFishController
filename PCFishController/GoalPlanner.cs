namespace PCFishController;

internal sealed record RouteIngredient(string Type, int Star, int Count);

internal sealed class GoalPlan
{
    internal string TargetType { get; init; } = "";
    internal int TargetStar { get; init; } = 1;
    internal bool Enabled { get; init; }
    internal IReadOnlySet<string> RouteTypes { get; init; } = new HashSet<string>();
    internal IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    internal bool HasExactRecipe { get; init; }

    internal int Score(FishDto fish)
    {
        if (!Enabled || fish == null) return 0;
        var score = RouteTypes.Contains(fish.ty ?? "") ? 10000 : 0;
        if (string.Equals(fish.ty, TargetType, StringComparison.OrdinalIgnoreCase)) score += 5000;
        var targetTier = FishCatalog.Find(TargetType)?.Tier ?? fish.RarityTier;
        score -= Math.Abs(targetTier - fish.RarityTier) * 100;
        score += Math.Min(50, fish.Stars) * 5;
        return score;
    }
}

/// <summary>
/// 目标路线只负责规划和排序。常规鱼的结果是概率事件，游戏资源没有给出确定的父母表；
/// 三种赛季鱼在资源里有确定配方，所以单独展示为精确路线。
/// </summary>
internal static class GoalPlanner
{
    private static readonly Dictionary<string, string[]> SeasonalIngredients = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FS00033"] = new[] { "FS00014", "FS00007", "FS00009", "FS00008" },
        ["FS00034"] = new[] { "FS00021", "FS00016", "FS00017", "FS00018" },
        ["FS00035"] = new[] { "FS00027", "FS00020", "FS00023", "FS00022" },
    };

    internal static GoalPlan Build(string targetType, int targetStar,
        IReadOnlyCollection<string> collectedTypes, IReadOnlyList<FishDto> fish)
    {
        targetType = string.IsNullOrWhiteSpace(targetType) ? "" : targetType.Trim().ToUpperInvariant();
        targetStar = Math.Clamp(targetStar, 1, 5);
        var enabled = FishCatalog.Find(targetType) != null;
        var route = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        if (!enabled)
        {
            lines.Add("尚未设置最终目标。请在本页选择鱼种和目标星级。");
            return new GoalPlan { TargetType = targetType, TargetStar = targetStar, Enabled = false, RouteTypes = route, Lines = lines };
        }

        route.Add(targetType);
        var target = FishCatalog.Find(targetType);
        lines.Add($"最终目标：{target.Name} {StarText(targetStar)}（{FishCatalog.RarityName(target.Tier)}）");
        SeasonalIngredients.TryGetValue(targetType, out var ingredients);
        var exact = target.Season == 1 && ingredients != null;
        if (exact)
        {
            lines.Add("赛季配方：每个星级都需要以下 4 种材料各 3 条；再加上一条同鱼种的上一星级鱼（1 星不需要）。");
            for (var star = 1; star <= targetStar; star++)
            {
                var parts = ingredients.Select(type => $"{FishCatalog.Name(type)} {StarText(star)} ×3").ToList();
                if (star > 1) parts.Insert(0, $"{target.Name} {StarText(star - 1)} ×1");
                lines.Add($"第 {star} 星：" + string.Join(" + ", parts));
                foreach (var type in ingredients) route.Add(type);
            }
        }
        else
        {
            lines.Add("常规鱼种：按目标鱼种和目标稀有度优先选择亲鱼，结果仍由游戏概率决定。");
            for (var tier = Math.Max(1, target.Tier - 1); tier <= target.Tier; tier++)
                lines.Add($"阶段 {FishCatalog.RarityName(tier)}：先积累该档位可繁育鱼，再逐步冲击 {target.Name}。");
        }

        var ownedTypes = new HashSet<string>(collectedTypes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var ownedFish = fish ?? new List<FishDto>();
        lines.Add($"图鉴进度：已发现 {ownedTypes.Count(type => FishCatalog.Find(type) != null)} / {FishCatalog.Species.Count} 种；当前仓库 {ownedFish.Count(f => string.Equals(f.ty, targetType, StringComparison.OrdinalIgnoreCase))} 条目标鱼。");
        lines.Add(exact ? "自动繁育会优先使用路线上的鱼；没有路线材料时按稀有度和星级递进。" : "自动繁育会优先使用目标相关鱼；没有目标相关鱼时按稀有度和星级递进。");
        return new GoalPlan { TargetType = targetType, TargetStar = targetStar, Enabled = true, RouteTypes = route, Lines = lines, HasExactRecipe = exact };
    }

    private static string StarText(int star)
        => star is >= 1 and <= 5 ? new string('★', star) + new string('☆', 5 - star) : "—";
}
