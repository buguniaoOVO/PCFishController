using PCFishController;

var result = await SteamMarket.FetchAsync(SteamMarket.CurrencyCny, text => Console.WriteLine(text));
Console.WriteLine("OK=" + result.Ok + " total=" + result.TotalItems + " quotes=" + result.ByKey.Count);
Console.WriteLine(result.Message);
var sample = result.ByKey.Values.OrderByDescending(q => q.MergeProtectionPrice).FirstOrDefault();
if (sample != null)
    Console.WriteLine(sample.HashName + " -> " + sample.MergeProtectionPrice.ToString("0.00") + " CNY, sold=" + sample.Sold);

if (result.Ok)
{
    SteamMarket.Invalidate();
    SteamMarket.LoadCache();
    var restored = SteamMarket.LastCached;
    if (restored == null || restored.ByKey.Count != result.ByKey.Count)
        throw new Exception("Persisted Steam market cache did not restore the complete quote set.");
    Console.WriteLine("PASS cache reload: " + restored.ByKey.Count + " quotes");
}
