using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PCFishController;

/// <summary>一次版本检查的结果。Fields 都做成了可空/带默认值，失败时也能安全展示。</summary>
internal sealed class UpdateInfo
{
    internal bool Ok;
    internal string Message = "";
    internal string LatestTag = "";
    internal string CurrentVersion = "";
    internal string Notes = "";
    internal string ReleaseUrl = "";
    internal string AssetUrl = "";
    internal string AssetName = "";
    internal long AssetSize;

    internal bool HasUpdate;

    internal string DescribeSize() => AssetSize <= 0
        ? ""
        : AssetSize >= 1024 * 1024
            ? $"{AssetSize / 1024.0 / 1024.0:F1} MB"
            : $"{AssetSize / 1024.0:F0} KB";
}

/// <summary>
/// 从 GitHub Releases 检查新版本，并可选地下载安装。
///
/// 检查走公开 API，不需要令牌。下载得到的是一个 zip，里面是新的控制器和插件。
/// 因为程序正在运行时无法覆盖自己的 exe，更新分两步：先把新文件解到 update 目录，
/// 再交给一个等待当前进程退出的批处理去替换并重启。用户点了才做。
/// </summary>
internal static class UpdateChecker
{
    internal const string RepoOwner = "buguniaoOVO";
    internal const string RepoName = "PCFishController";

    private const string LatestReleaseApi =
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private const string LatestReleasePage =
        $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    internal static string ReleasesPage =>
        $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>当前程序集版本，三段式。</summary>
    internal static string CurrentVersion
    {
        get
        {
            try
            {
                var v = typeof(UpdateChecker).Assembly.GetName().Version;
                return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                return "0.0.0";
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // 自动使用系统代理，和游戏客户端所在网络环境保持一致。
            UseProxy = true,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PCFishController/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version ParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// 读取 releases/latest 的 302 跳转，从 Location 里的 tag 得到最新版本。
    /// 不使用 GitHub API，所以不会撞上未认证请求的每小时配额。
    /// </summary>
    private static async Task<string> ResolveLatestTagAsync()
    {
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PCFishController/{CurrentVersion}");
        using var response = await client.GetAsync(LatestReleasePage);
        var code = (int)response.StatusCode;
        if (code is not (301 or 302 or 303 or 307 or 308)) return null;
        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location)) return null;
        var tag = location.TrimEnd('/').Split('/')[^1];
        var normalized = tag.TrimStart('v', 'V');
        return Version.TryParse(normalized.Split('-')[0], out _) ? tag : null;
    }

    /// <summary>用 HEAD 请求探测压缩包大小，失败时返回 0。</summary>
    private static async Task<long> ProbeSizeAsync(string url)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"PCFishController/{CurrentVersion}");
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return 0;
            return response.Content.Headers.ContentLength ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    internal static async Task<UpdateInfo> CheckAsync()
    {
        var info = new UpdateInfo { CurrentVersion = CurrentVersion };

        // 首选：GitHub API 的 releases/latest。它取的是权威结果，不会因为 CDN 缓存
        // 落后而漏报刚发布的版本。每小时只查一次，远低于未认证的 60 次配额。
        try
        {
            var api = await CheckViaApiAsync(info);
            if (api) return info;
        }
        catch
        {
            // 落到跳转方式
        }

        // 备用：读 releases/latest 的 302 跳转。不用 API，配额用尽时也能用，
        // 代价是可能受 CDN 缓存影响，刚发布的版本会晚一点才看到。
        try
        {
            var tag = await ResolveLatestTagAsync();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                info.LatestTag = tag;
                var latest = ParseTag(tag);
                var current = ParseTag(CurrentVersion);
                info.HasUpdate = latest != null && current != null && latest > current;
                info.ReleaseUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/{tag}";
                info.AssetName = $"PCFishController-{tag}.zip";
                info.AssetUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/{info.AssetName}";
                info.AssetSize = await ProbeSizeAsync(info.AssetUrl);
                if (info.AssetSize <= 0)
                {
                    // 这个发布没有按约定命名的压缩包，让用户去发布页自己取。
                    info.AssetUrl = "";
                }
                info.Ok = true;
                info.Message = latest == null
                    ? $"最新版本标记无法识别：{tag}"
                    : info.HasUpdate
                        ? $"发现新版本 {tag}，当前 {CurrentVersion}。"
                        : $"已是最新版本（{CurrentVersion}）。";
                return info;
            }
        }
        catch
        {
            // 两种方式都失败时，下面的兜底会用 info.Message 说明情况
        }

        if (string.IsNullOrEmpty(info.Message))
            info.Message = "检查更新失败：无法连接 GitHub。";
        return info;
    }

    /// <summary>走 GitHub API 查询最新发布。成功返回 true，失败返回 false 交给备用方式。</summary>
    private static async Task<bool> CheckViaApiAsync(UpdateInfo info)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(LatestReleaseApi);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                info.Message = "仓库还没有发布任何版本。";
                info.Ok = true;
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                // 例如未认证配额用尽（403）。交给备用方式再试一次。
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            info.LatestTag = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
            info.Notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            info.ReleaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";

            // 优先选名字里带 zip 的资源；没有就退而取第一个。
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                JsonElement? fallback = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (fallback == null) fallback = asset;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    fallback = asset;
                    break;
                }
                if (fallback is { } chosen)
                {
                    info.AssetName = chosen.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    info.AssetUrl = chosen.TryGetProperty("browser_download_url", out var au)
                        ? au.GetString() ?? ""
                        : "";
                    if (chosen.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                        info.AssetSize = bytes;
                }
            }

            var latest = ParseTag(info.LatestTag);
            var current = ParseTag(CurrentVersion);
            info.HasUpdate = latest != null && current != null && latest > current;
            info.Ok = true;
            info.Message = latest == null
                ? $"最新版本标记无法识别：{info.LatestTag}"
                : info.HasUpdate
                    ? $"发现新版本 {info.LatestTag}，当前 {CurrentVersion}。"
                    : $"已是最新版本（{CurrentVersion}）。";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 zip 下载到临时文件，返回解压目录。</summary>
    private static async Task<string> DownloadAndExtractAsync(UpdateInfo info, Action<string> report)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PCFishController-update");
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        Directory.CreateDirectory(tempRoot);

        var zipPath = Path.Combine(tempRoot, string.IsNullOrWhiteSpace(info.AssetName)
            ? "update.zip"
            : info.AssetName);

        report($"正在下载 {info.AssetName}（{info.DescribeSize()}）…");
        using (var client = CreateClient())
        using (var response = await client.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(zipPath);
            await source.CopyToAsync(target);
        }

        var extractDir = Path.Combine(tempRoot, "extracted");
        report("正在解压…");
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        return extractDir;
    }

    private static string FindFile(string root, string fileName)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary>
    /// 下载并安排替换。返回是否需要退出程序（true 表示调用方应立即退出）。
    /// 真正的替换由批处理在进程退出后执行，避免占用中的 exe 无法覆盖。
    /// </summary>
    internal static async Task<(bool Ok, string Message, bool ShouldExit)> ApplyAsync(
        UpdateInfo info, Action<string> report)
    {
        try
        {
            var extractDir = await DownloadAndExtractAsync(info, report);
            var appDir = AppContext.BaseDirectory;
            var staging = Path.Combine(appDir, "update");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            // 只搬运我们认识的文件；其它内容留在临时目录里，由批处理最后清理。
            var moved = new List<string>();
            foreach (var name in new[] { "PCFishController.exe", "PCFish助手.exe", "PCFishAutoHelper.dll" })
            {
                var found = FindFile(extractDir, name);
                if (found == null) continue;
                var destination = Path.Combine(staging, name);
                File.Copy(found, destination, overwrite: true);
                moved.Add(name);
            }

            if (moved.Count == 0)
                return (false, "压缩包里没有找到可用的程序文件。", false);

            report("正在准备替换：" + string.Join("、", moved));

            var script = Path.Combine(staging, "apply-update.cmd");
            File.WriteAllText(script, BuildScript(appDir, staging), new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = staging
            };
            Process.Start(startInfo);

            return (true,
                $"更新已下载（{string.Join("、", moved)}）。程序将关闭，替换完成后自动重新启动。",
                true);
        }
        catch (Exception ex)
        {
            return (false, "更新失败：" + ex.Message, false);
        }
    }

    /// <summary>等待本进程退出 → 覆盖文件 → 重新启动 → 自删。</summary>
    private static string BuildScript(string appDir, string staging)
    {
        var exe = Path.Combine(appDir, "PCFish助手.exe");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        // 等旧进程把 exe 释放出来，最多等 60 秒。
        sb.AppendLine("set /a tries=0");
        sb.AppendLine(":wait");
        sb.AppendLine($"del /f /q \"{exe}\" >nul 2>&1");
        sb.AppendLine($"if exist \"{exe}\" (");
        sb.AppendLine("  set /a tries+=1");
        sb.AppendLine("  if %tries% lss 60 (");
        sb.AppendLine("    ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("    goto wait");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        sb.AppendLine($"for %%f in (\"{staging}\\*.exe\" \"{staging}\\*.dll\") do copy /y \"%%~ff\" \"{appDir}\\\" >nul");
        sb.AppendLine($"start \"\" \"{Path.Combine(appDir, "PCFishController.exe")}\"");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        sb.AppendLine($"rmdir /s /q \"{staging}\" >nul 2>&1");
        sb.AppendLine("endlocal");
        return sb.ToString();
    }

    /// <summary>在系统默认浏览器里打开发布页。</summary>
    internal static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，页面上已经有可复制的链接
        }
    }
}

