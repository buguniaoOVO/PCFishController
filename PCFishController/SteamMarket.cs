using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace PCFishController;

/// <summary>一条 Steam 市场报价。</summary>
internal sealed class MarketQuote
{
    /// <summary>市场里的物品名，形如 "Golden Seahorse ★★★"。</summary>
    internal string HashName = "";
    /// <summary>最低在售价，已换算成数字。</summary>
    internal decimal Lowest;
    /// <summary>原始价格文本，保留货币符号。</summary>
    internal string PriceText = "";
    /// <summary>最低出售求购价，没有就为 0。</summary>
    internal decimal HighestBuy;
    internal string BuyText = "";
    /// <summary>成交笔数，用来判断报价可不可信。</summary>
    internal int Sold;
    /// <summary>市场上流通的数量。</summary>
    internal int Listed;
}

internal sealed class MarketLookupResult
{
    internal bool Ok;
    internal string Message = "";
    /// <summary>键是「英文鱼名|星级」，值是报价。</summary>
    internal readonly Dictionary<string, MarketQuote> ByKey = new(StringComparer.OrdinalIgnoreCase);
    internal DateTime FetchedAt = DateTime.Now;
    internal int TotalItems;

    internal static string Key(string name, int stars) => $"{name}|{stars}";

    internal MarketQuote Find(string name, int stars)
        => !string.IsNullOrWhiteSpace(name) && ByKey.TryGetValue(Key(name, stars), out var quote) ? quote : null;
}

/// <summary>
/// 从 Steam 社区市场读取 PC FISH 的物品报价。
///
/// 游戏的鱼在 Steam 市场里是收藏品：物品名是「英文鱼名 + 星级」，例如
/// "Golden Seahorse ★★★"。所以仓库里的鱼可以用「英文名 + 星级」直接对上报价。
///
/// 只用公开的 search/render 接口，不需要登录、不需要 API key，也不读用户的 Steam 账号。
/// 结果整体缓存，按需刷新，避免频繁请求。
/// </summary>
internal static class SteamMarket
{
    /// <summary>PC FISH 的 Steam appid。市场和库存接口都用它。</summary>
    internal const string AppId = "4842670";

    private const string SearchUrl =
        "https://steamcommunity.com/market/search/render/?appid=" + AppId +
        "&norender=1&count={0}&start={1}&currency={2}";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private static MarketLookupResult _cache;

    internal static MarketLookupResult Cached =>
        _cache != null && DateTime.Now - _cache.FetchedAt < CacheLifetime ? _cache : null;

    /// <summary>人民币的价格代码，steam 用 currency=23 表示 CNY。</summary>
    internal const int CurrencyCny = 23;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PCFishController/" + UpdateChecker.CurrentVersion);
        return client;
    }

    /// <summary>拉取全部报价。每页 100 条，最多翻 12 页，避免意外打爆上游。</summary>
    internal static async Task<MarketLookupResult> FetchAsync(int currency, Action<string> report = null)
    {
        var result = new MarketLookupResult();
        // 直接用结果里的字典，别再建一个局部副本——之前就是漏了这一步，
        // 导致 TotalItems 有值但 ByKey 是空的，一条都匹配不上。
        var quotes = result.ByKey;
        try
        {
            using var client = CreateClient();
            var total = -1;
            var start = 0;
            // 实测 Steam 的 search/render 每次最多返回 10 条，count 传更大也没用，
            // 所以按「实际返回多少条」推进偏移，否则会跳过中间的物品。
            const int pageSize = 100;
            for (var page = 0; page < 60; page++)
            {
                var url = string.Format(CultureInfo.InvariantCulture, SearchUrl, pageSize, start, currency);
                using var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    result.Message = $"Steam 市场查询失败：HTTP {(int)response.StatusCode}。";
                    if (quotes.Count == 0) return result;
                    break;
                }
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    result.Message = "Steam 市场返回失败，稍后再试。";
                    if (quotes.Count == 0) return result;
                    break;
                }
                if (total < 0 && root.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var n))
                    total = n;

                if (!root.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array) break;
                var before = quotes.Count;
                var returned = 0;
                foreach (var item in items.EnumerateArray())
                {
                    returned++;
                    var quote = ParseQuote(item);
                    if (quote == null) continue;
                    var stars = StarsFromName(quote.HashName);
                    var plain = StripStars(quote.HashName);
                    if (plain.Length == 0 || stars <= 0) continue;
                    quotes[MarketLookupResult.Key(plain, stars)] = quote;
                }
                // 偏移按「本页返回条数」推进；返回 0 条或没带来新数据就结束，
                // 避免上游给出重复页时无限翻下去。
                start += returned;
                report?.Invoke($"已读取 {quotes.Count} 条报价…");
                if (returned == 0 || quotes.Count == before) break;
                if (total > 0 && start >= total) break;
            }

            result.Ok = quotes.Count > 0;
            result.TotalItems = quotes.Count;
            result.FetchedAt = DateTime.Now;
            result.Message = result.Ok
                ? $"已获取 {quotes.Count} 条 Steam 市场报价。"
                : "Steam 市场没有返回可识别的报价。";
            if (result.Ok) _cache = result;
            return result;
        }
        catch (Exception ex)
        {
            result.Message = "Steam 市场查询失败：" + ex.Message;
            return result;
        }
    }

    private static MarketQuote ParseQuote(JsonElement item)
    {
        try
        {
            var name = item.TryGetProperty("hash_name", out var hn) ? hn.GetString() ?? "" : "";
            if (name.Length == 0) return null;
            var quote = new MarketQuote
            {
                HashName = name,
                PriceText = item.TryGetProperty("sell_price_text", out var st) ? st.GetString() ?? "" : "",
                BuyText = item.TryGetProperty("buy_price_text", out var bt) ? bt.GetString() ?? "" : ""
            };
            quote.Lowest = item.TryGetProperty("sell_price", out var sp) && sp.ValueKind == JsonValueKind.Number
                ? sp.GetInt64() / 100m
                : ParsePrice(quote.PriceText);
            quote.HighestBuy = item.TryGetProperty("buy_price", out var bp) && bp.ValueKind == JsonValueKind.Number
                ? bp.GetInt64() / 100m
                : ParsePrice(quote.BuyText);
            quote.Sold = item.TryGetProperty("sell_listings", out var sl) && sl.TryGetInt32(out var listings)
                ? listings : 0;
            quote.Listed = quote.Sold;
            return quote;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>星级文本用实心黑星，描述里用实心白星，所以两种都认。</summary>
    private static int StarsFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var n = 0;
        foreach (var ch in name)
            if (ch is '★' or '☆') n++;
        return n;
    }

    private static string StripStars(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var end = name.Length;
        while (end > 0 && (name[end - 1] == '★' || name[end - 1] == '☆' || name[end - 1] == ' ')) end--;
        return name[..end].Trim();
    }

    private static decimal ParsePrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        var digits = new string(text.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
        digits = digits.Replace(",", "");
        return decimal.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    internal static void Invalidate() => _cache = null;
}
