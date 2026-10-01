namespace PCFishController;

internal sealed record SynthesisRow(string Type, int Star, int Count, int Depth, string Role, bool Exact);

internal sealed class SynthesisRoute
{
    internal string TargetType { get; init; } = "";
    internal int TargetStar { get; init; } = 1;
    internal bool Exact { get; init; }
    internal IReadOnlyList<SynthesisRow> Rows { get; init; } = Array.Empty<SynthesisRow>();
    internal string SourceText { get; init; } = "";
}

/// <summary>
/// Wiki Season 1 路线的本地结构化数据。
/// 普通合成的 Wiki 页面没有发布固定物种配方，因此保留为待验证节点。
/// </summary>
internal static class SynthesisPlanner
{
    internal static SynthesisRoute Build(string targetType, int targetStar)
    {
        targetType = string.IsNullOrWhiteSpace(targetType) ? "" : targetType.Trim().ToUpperInvariant();
        targetStar = Math.Clamp(targetStar, 1, 5);
        var target = FishCatalog.Find(targetType);
        if (target == null)
        {
            return new SynthesisRoute
            {
                TargetType = targetType,
                TargetStar = targetStar,
                SourceText = "请选择鱼种。"
            };
        }

        var rows = new List<SynthesisRow>();
        var recipe = WikiDatabase.FindRecipe(targetType);
        var exact = target.Season > 0 && recipe != null;
        if (exact)
        {
            AddSeasonal(rows, targetType, targetStar, 1, 0, "目标鱼", recipe);
            return new SynthesisRoute
            {
                TargetType = targetType,
                TargetStar = targetStar,
                Exact = true,
                Rows = rows,
                SourceText = "Wiki Season 1：已确认材料和星级要求；赛季鱼使用合成路线。"
            };
        }

        rows.Add(new SynthesisRow(targetType, targetStar, 1, 0, "目标鱼 · 常规合成配方待验证", false));
        return new SynthesisRoute
        {
            TargetType = targetType,
            TargetStar = targetStar,
            Exact = false,
            Rows = rows,
            SourceText = "Wiki：普通合成固定物种配方和当前概率未发布；请使用繁育路线优先积累目标相关鱼。"
        };
    }

    private static void AddSeasonal(List<SynthesisRow> rows, string type, int star, int count, int depth, string role, WikiRecipe recipe)
    {
        rows.Add(new SynthesisRow(type, star, count, depth, role, true));
        if (star > 1)
        {
            AddSeasonal(rows, type, star - 1, 1, depth + 1, "上一星级", recipe);
        }
        foreach (var ingredient in recipe.Ingredients ?? new List<WikiIngredient>())
        {
            var ingredientStar = ingredient.Role == "核心鱼" || !recipe.RepeatSupportAtTargetStar
                ? ingredient.Star
                : star;
            rows.Add(new SynthesisRow(ingredient.Type, ingredientStar, ingredient.Count, depth + 1, ingredient.Role, true));
        }
    }
}
