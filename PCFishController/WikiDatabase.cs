using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PCFishController;

internal sealed class WikiFishRecord
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Tier { get; set; }
    public int Season { get; set; }
}

internal sealed class WikiIngredient
{
    public string Type { get; set; } = "";
    public int Star { get; set; } = 1;
    public int Count { get; set; } = 1;
    public string Role { get; set; } = "辅助鱼";
}

internal sealed class WikiRecipe
{
    public string Target { get; set; } = "";
    public int Season { get; set; } = 1;
    public bool RepeatSupportAtTargetStar { get; set; } = true;
    public List<WikiIngredient> Ingredients { get; set; } = new();
}

internal sealed class WikiDatabaseFile
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceUrl { get; set; } = "https://pc-fish.wiki/season-1-recipes/";
    public string LastCheckedUtc { get; set; } = "";
    public bool SourceVerified { get; set; }
    public List<WikiFishRecord> Fish { get; set; } = new();
    public List<WikiRecipe> Recipes { get; set; } = new();
}

/// <summary>Wiki 数据缓存。页面刷新会验证 Wiki 页面并更新缓存时间，S2 可直接扩展 JSON 记录。</summary>
internal static class WikiDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static WikiDatabaseFile _data;

    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "PCFish Wiki数据库.json");
    internal static WikiDatabaseFile Data { get { Load(); return _data; } }

    internal static void Load()
    {
        if (_data != null) return;
        try
        {
            if (File.Exists(FilePath))
                _data = JsonSerializer.Deserialize<WikiDatabaseFile>(File.ReadAllText(FilePath, Encoding.UTF8), JsonOptions);
        }
        catch { _data = null; }
        _data ??= CreateDefault();
        _data.Fish ??= new List<WikiFishRecord>();
        _data.Recipes ??= new List<WikiRecipe>();
        FishCatalog.ApplyWikiRecords(_data.Fish);
        Save();
    }

    internal static WikiRecipe FindRecipe(string target)
    {
        Load();
        return _data.Recipes.FirstOrDefault(x => string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase));
    }

    internal static async Task<(bool Ok, string Message)> RefreshAsync()
    {
        Load();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCFish助手/0.17 Wiki refresh");
            var html = await client.GetStringAsync(_data.SourceUrl);
            var required = new[] { "Frostblue Sunfish", "Limeback Sea Turtle", "Matsuri Octopus" };
            var missing = required.Where(x => !html.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                return (false, "Wiki 页面缺少预期的 Season 1 鱼种标记：" + string.Join("、", missing));

            _data.LastCheckedUtc = DateTimeOffset.UtcNow.ToString("O");
            _data.SourceVerified = true;
            Save();
            FishCatalog.ApplyWikiRecords(_data.Fish);
            return (true, $"Wiki 已验证，缓存时间 {_data.LastCheckedUtc}");
        }
        catch (Exception ex)
        {
            return (false, "Wiki 刷新失败：" + ex.Message);
        }
    }

    private static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOptions), Encoding.UTF8); }
        catch { }
    }

    private static WikiDatabaseFile CreateDefault() => new()
    {
        Fish = new List<WikiFishRecord>
        {
            new() { Code = "FS00033", Name = "霜蓝翻车鱼", Tier = 3, Season = 1 },
            new() { Code = "FS00034", Name = "青柠背海龟", Tier = 4, Season = 1 },
            new() { Code = "FS00035", Name = "祭典章鱼", Tier = 5, Season = 1 },
        },
        Recipes = new List<WikiRecipe>
        {
            new() { Target = "FS00033", Season = 1, Ingredients = new List<WikiIngredient>
            {
                new() { Type = "FS00014", Count = 1, Role = "核心鱼" },
                new() { Type = "FS00007", Count = 3 }, new() { Type = "FS00009", Count = 3 }, new() { Type = "FS00008", Count = 3 }
            }},
            new() { Target = "FS00034", Season = 1, Ingredients = new List<WikiIngredient>
            {
                new() { Type = "FS00021", Count = 1, Role = "核心鱼" },
                new() { Type = "FS00016", Count = 3 }, new() { Type = "FS00017", Count = 3 }, new() { Type = "FS00018", Count = 3 }
            }},
            new() { Target = "FS00035", Season = 1, Ingredients = new List<WikiIngredient>
            {
                new() { Type = "FS00027", Count = 1, Role = "核心鱼" },
                new() { Type = "FS00020", Count = 3 }, new() { Type = "FS00023", Count = 3 }, new() { Type = "FS00022", Count = 3 }
            }},
        }
    };
}
