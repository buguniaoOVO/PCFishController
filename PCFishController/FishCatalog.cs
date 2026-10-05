namespace PCFishController;

/// <summary>
/// 游戏资源中的鱼种目录。名称来自 PCFish 自带的简体中文本地化表，
/// 稀有度和分组来自 fish_type_v1.3；控制器不依赖外部网页才能显示名称。
/// </summary>
internal sealed record FishSpecies(string Code, string Name, int Tier, int Group, int Season)
{
    public string DisplayName => FishCatalog.DisplayName(Code);
}

internal static class FishCatalog
{
    private static readonly FishSpecies[] All =
    {
        new("FS00000", "金鱼", 0, 0, 0),
        new("FS00001", "阳光豆娘鱼", 1, 2, 0),
        new("FS00002", "紫罗兰拟雀鲷", 1, 1, 0),
        new("FS00003", "蓝宝石雀鲷", 1, 3, 0),
        new("FS00004", "烈焰蝶尾金鱼", 1, 1, 0),
        new("FS00005", "霜鳞太阳鱼", 1, 3, 0),
        new("FS00006", "樱桃鲃", 1, 2, 0),
        new("FS00007", "月角豆娘鱼", 1, 1, 0),
        new("FS00008", "苔背小精灵", 1, 3, 0),
        new("FS00009", "霓虹清洁隆头鱼", 1, 2, 0),
        new("FS00010", "三色锦鲤", 2, 2, 0),
        new("FS00011", "火鳍兵鲷", 2, 1, 0),
        new("FS00012", "睡衣雀鲷", 2, 3, 0),
        new("FS00013", "泻湖雀鲷", 2, 2, 0),
        new("FS00014", "铜带蝴蝶鱼", 2, 1, 0),
        new("FS00015", "绯红雀鲷", 2, 3, 0),
        new("FS00016", "星鳍邦盖拟雀鲷", 2, 1, 0),
        new("FS00017", "黄金蝴蝶鱼", 2, 2, 0),
        new("FS00018", "霓虹条纹灯鱼", 2, 3, 0),
        new("FS00019", "棱镜虹鱼", 3, 3, 0),
        new("FS00020", "奶油泡芙河豚", 3, 1, 0),
        new("FS00021", "红脸火焰虾虎鱼", 3, 2, 0),
        new("FS00022", "金尾帝王鱼", 3, 3, 0),
        new("FS00023", "天翼飞鱼", 3, 2, 0),
        new("FS00024", "宝石花鮨", 3, 1, 0),
        new("FS00025", "珊瑚小丑鱼", 4, 2, 0),
        new("FS00026", "黄金海马", 4, 3, 0),
        new("FS00027", "黄金长鳍蝴蝶鱼", 4, 1, 0),
        new("FS00028", "小蓝鲸", 5, 3, 0),
        new("FS00029", "绯红皇冠蓑鲉", 5, 2, 0),
        new("FS00030", "紫水晶斗鱼", 5, 1, 0),
        new("FS00031", "蓝电金枪鱼", 3, 3, 0),
        new("FS00032", "旭日兰寿", 4, 2, 0),
        new("FS00033", "霜蓝翻车鱼", 3, 0, 1),
        new("FS00034", "青柠背海龟", 4, 0, 1),
        new("FS00035", "祭典章鱼", 5, 0, 1),
    };

    private static readonly List<FishSpecies> SpeciesList = All.ToList();
    private static readonly Dictionary<string, FishSpecies> ByCode =
        SpeciesList.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> EnglishNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FS00000"] = "Goldfish", ["FS00001"] = "Sunshine Tang", ["FS00002"] = "Violet Dottyback",
        ["FS00003"] = "Sapphire Chromis", ["FS00004"] = "Flame Fantail", ["FS00005"] = "Frostblue Sunfish",
        ["FS00006"] = "Cherry Barb", ["FS00007"] = "Moonhorn Tang", ["FS00008"] = "Mossback Otocinclus",
        ["FS00009"] = "Neon Cleaner Wrasse", ["FS00010"] = "Calico Koi", ["FS00011"] = "Emberfin Snapper",
        ["FS00012"] = "Pajama Cardinalfish", ["FS00013"] = "Lagoon Chromis", ["FS00014"] = "Copperband Butterflyfish",
        ["FS00015"] = "Crimson Cardinalfish", ["FS00016"] = "Banggai Starfin", ["FS00017"] = "Golden Butterflyfish",
        ["FS00018"] = "Neon Streak Tetra", ["FS00019"] = "Prism Rainbowfish", ["FS00020"] = "Cream Puff Puffer",
        ["FS00021"] = "Blushing Firefish", ["FS00022"] = "Royal Goldtail", ["FS00023"] = "Skywing Flying Fish",
        ["FS00024"] = "Jewel Mandarinfish", ["FS00025"] = "Coral Clownfish", ["FS00026"] = "Golden Seahorse",
        ["FS00027"] = "Golden Moorish Idol", ["FS00028"] = "Little Blue Whale", ["FS00029"] = "Crimson Crown Lionfish",
        ["FS00030"] = "Amethyst Betta", ["FS00031"] = "Bluebolt Tuna", ["FS00032"] = "Sunburst Ranchu",
        ["FS00033"] = "Frostblue Sunfish", ["FS00034"] = "Limeback Sea Turtle", ["FS00035"] = "Matsuri Octopus"
    };

    internal static IReadOnlyList<FishSpecies> Species => SpeciesList;

    internal static void ApplyWikiRecords(IEnumerable<WikiFishRecord> records)
    {
        if (records == null) return;
        foreach (var record in records)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Code)) continue;
            if (ByCode.TryGetValue(record.Code, out var current))
            {
                var merged = current with
                {
                    Name = string.IsNullOrWhiteSpace(record.Name) ? current.Name : record.Name,
                    Tier = record.Tier > 0 ? record.Tier : current.Tier,
                    Season = record.Season > 0 ? record.Season : current.Season
                };
                ByCode[record.Code] = merged;
                var index = SpeciesList.FindIndex(x => string.Equals(x.Code, record.Code, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) SpeciesList[index] = merged;
            }
            else
            {
                var added = new FishSpecies(record.Code, record.Name ?? record.Code, record.Tier, 0, record.Season);
                ByCode[record.Code] = added;
                SpeciesList.Add(added);
            }
        }
    }

    internal static FishSpecies Find(string code)
        => code != null && ByCode.TryGetValue(code, out var value) ? value : null;

    internal static string Name(string code)
    {
        var species = Find(code);
        if (species == null) return string.IsNullOrWhiteSpace(code) ? UiLanguage.T("未知鱼") : code;
        return UiLanguage.IsEnglish && EnglishNames.TryGetValue(code, out var english) ? english : species.Name;
    }

    internal static string DisplayName(string code)
    {
        var species = Find(code);
        if (species == null) return Name(code);
        var name = Name(code);
        return species.Season > 0 ? $"S{species.Season} · {name}" : name;
    }

    /// <summary>
    /// 固定返回英文名，和 Steam 市场里的物品名对齐。
    /// 市场物品名是「英文名 + 星级」，所以查价必须用英文，不能跟着界面语言走。
    /// </summary>
    internal static string EnglishName(string code)
        => code != null && EnglishNames.TryGetValue(code, out var english) ? english : null;

    internal static string RarityName(int tier) => UiLanguage.T(tier switch
    {
        0 => "基础",
        1 => "普通",
        2 => "高级",
        3 => "稀有",
        4 => "传说",
        5 => "神话",
        _ => $"T{tier}"
    });

    internal static string Describe(string code, int tier = -1)
    {
        var species = Find(code);
        var actualTier = tier >= 0 ? tier : species?.Tier ?? -1;
        return $"{Name(code)} · {RarityName(actualTier)}";
    }
}
