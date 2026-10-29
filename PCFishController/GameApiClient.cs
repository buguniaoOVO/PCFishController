using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCFishController;

/// <summary>
/// 游戏 HTTP 协议客户端。
///
/// 存在的原因：助手原来的合成/繁育是「模拟玩家点游戏 UI」——
/// ResetParent → SetParent×10 → Merge()，再靠比对鱼群猜成败。
/// 这条链依赖 UI 处于合适状态，任何一环对不上就表现为
/// 「能读到数据、动作发出去但游戏不认」。
///
/// 游戏自己的请求实测极简（2026-10-29 Frida 抓包确认）：
///   PUT https://gapi.nanoocompany.com/cs/pcfish/merge/run
///   {"parentFishId":[10 个鱼的 UUID]}
/// 无签名、无时间戳、无 nonce。所以完全可以在进程外直接发，绕开 UI 状态。
///
/// 鉴权走 header（accessToken/refreshToken 托管字段实测恒为空，真 JWT 只在 header 里）：
///   X-NanooGame-Key / X-NanooGame-Id / X-NanooGame-Device /
///   X-NanooGame-Version / Content-Type / Authorization: Bearer &lt;JWT&gt;
/// </summary>
internal sealed class GameApiClient : IDisposable
{
    internal const string BaseUrl = "https://gapi.nanoocompany.com";
    internal const string MergePath = "/cs/pcfish/merge/run";
    internal const string BreedPath = "/cs/pcfish/breed/run";

    /// <summary>单次后台请求的超时。游戏自己的 WWW_TIMEOUT 未读出，这里取保守值。</summary>
    internal const int DefaultTimeoutSeconds = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _headers;
    private bool _disposed;

    private GameApiClient(HttpClient http, Dictionary<string, string> headers)
    {
        _http = http;
        _headers = headers;
    }

    /// <summary>是否已具备可用的 Authorization。</summary>
    internal bool HasAuthorization =>
        _headers.TryGetValue("Authorization", out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>当前携带的 header 名（用于日志，不含值）。</summary>
    internal IReadOnlyCollection<string> HeaderNames => _headers.Keys;

    /// <summary>
    /// 由 agent 抓到的 header 构造客户端。
    /// 缺失 X-NanooGame-Key / X-NanooGame-Id / Authorization 时返回 null，
    /// 调用方应当回退到原来的 UI 驱动路径。
    /// </summary>
    internal static GameApiClient FromGameHeaders(
        IReadOnlyDictionary<string, string> headers,
        HttpMessageHandler handler = null,
        TimeSpan? timeout = null)
    {
        if (headers == null) return null;

        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            // 这些 header 由 HttpClient 自己管，手工设置会抛异常
            if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            kept[pair.Key] = pair.Value;
        }

        if (!kept.ContainsKey("X-NanooGame-Key")) return null;
        if (!kept.ContainsKey("X-NanooGame-Id")) return null;
        if (!kept.ContainsKey("Authorization")) return null;
        if (!kept.ContainsKey("Content-Type"))
            kept["Content-Type"] = "application/json; charset=UTF-8";

        var http = handler != null ? new HttpClient(handler, false) : new HttpClient();
        http.Timeout = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        return new GameApiClient(http, kept);
    }

    /// <summary>合成：消耗 10 条材料鱼产出一条新鱼。</summary>
    internal Task<ApiResult> MergeAsync(IReadOnlyList<string> parentFishIds, CancellationToken token = default)
        => PutAsync(MergePath, new { parentFishId = parentFishIds }, token);

    /// <summary>繁育：两条亲鱼。</summary>
    internal Task<ApiResult> BreedAsync(string parentA, string parentB, CancellationToken token = default)
        => PutAsync(BreedPath, new { parentFishId = new[] { parentA, parentB } }, token);

    private async Task<ApiResult> PutAsync(string path, object payload, CancellationToken token)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GameApiClient));

        var url = BaseUrl + path;
        using var request = new HttpRequestMessage(HttpMethod.Put, url);

        // 只在请求级设置，避免污染 HttpClient 共享状态
        foreach (var pair in _headers)
        {
            if (pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var ok = response.IsSuccessStatusCode && !LooksLikeBusinessFailure(body);
            return new ApiResult(ok, (int)response.StatusCode, body,
                ok ? "服务器已接受" : DescribeFailure((int)response.StatusCode, body));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new ApiResult(false, 0, "", "请求超时（" + _http.Timeout.TotalSeconds + " 秒）");
        }
        catch (Exception ex)
        {
            return new ApiResult(false, 0, "", "请求失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 游戏服务端在业务失败时仍可能返回 200，靠 body 里的 code 区分。
    /// 这里做保守判断：只认「明确成功」的业务码，其余交给调用方核对鱼群。
    /// </summary>
    private static bool LooksLikeBusinessFailure(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            foreach (var name in new[] { "status", "result", "code", "messageCode" })
            {
                if (!root.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number) return value.GetInt32() >= 400;
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString() ?? "";
                    if (text.Equals("fail", StringComparison.OrdinalIgnoreCase)) return true;
                    if (text.Equals("error", StringComparison.OrdinalIgnoreCase)) return true;
                    if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应不在这里判失败
        }
        return false;
    }

    private static string DescribeFailure(int statusCode, string body)
    {
        if (statusCode == 401 || statusCode == 403)
            return "鉴权失败（" + statusCode + "），token 可能已过期";
        if (statusCode == 0) return "无响应";
        var preview = body == null ? "" : body.Length > 120 ? body.Substring(0, 120) + "…" : body;
        return "服务器返回 " + statusCode + (preview.Length > 0 ? "：" + preview : "");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

/// <summary>一次游戏接口调用的结果。</summary>
internal sealed record ApiResult(bool Ok, int StatusCode, string Body, string Message);
