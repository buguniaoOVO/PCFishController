using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace PCFishController;

/// <summary>安装 BepInEx 的结果，Steps 是给用户看的执行清单。</summary>
internal sealed class BepInExInstallResult
{
    internal bool Ok;
    internal string Message = "";
    internal bool AlreadyInstalled;
    internal bool NeedRestart;
    internal string Version = "";
    internal readonly List<string> Steps = new();
}

/// <summary>
/// 一键安装 BepInEx 6。
///
/// 从官方构建站 builds.bepinex.dev 抓最新版本，按游戏的运行时分发选择合适的包
/// （IL2CPP 游戏用 BepInEx-Unity.IL2CPP-win-x64），解压到游戏根目录。
/// 安装前会把会被覆盖的既有文件备份到 BepInEx-backup 目录，覆盖已装版本时也用它。
///
/// 写文件前必须关闭游戏：winhttp.dll 在游戏运行时被占用，无法覆盖。
/// </summary>
internal static class BepInExInstaller
{
    private const string BuildPage = "https://builds.bepinex.dev/projects/bepinex_be";
    private const string SiteRoot = "https://builds.bepinex.dev";

    private const string WinHttpDll = "winhttp.dll";
    private const string DoorstopConfig = "doorstop_config.ini";
    private const string DoorstopVersion = ".doorstop_version";
    private const string CoreMarker = @"BepInEx\core\BepInEx.Core.dll";

    /// <summary>判断游戏是 IL2CPP 还是 Mono。PC FISH 是 IL2CPP。</summary>
    private static string DetectRuntime(string gameDir)
        => File.Exists(Path.Combine(gameDir, "GameAssembly.dll")) ? "IL2CPP" : "Mono";

    /// <summary>游戏根目录是否已经装好 BepInEx（三个关键文件都在）。</summary>
    internal static bool IsInstalled(string gameDir)
        => File.Exists(Path.Combine(gameDir, WinHttpDll))
           && File.Exists(Path.Combine(gameDir, DoorstopConfig))
           && File.Exists(Path.Combine(gameDir, CoreMarker));

    /// <summary>给界面用的已安装版本描述，读不到版本号时给个通用说明。</summary>
    internal static string DescribeInstalled(string gameDir)
    {
        var version = ReadInstalledVersion(gameDir);
        if (version.Length > 0) return version;
        try
        {
            var core = Path.Combine(gameDir, CoreMarker);
            if (File.Exists(core))
            {
                var info = FileVersionInfo.GetVersionInfo(core);
                if (!string.IsNullOrWhiteSpace(info.FileVersion)) return "BepInEx " + info.FileVersion;
            }
        }
        catch
        {
        }
        return "已安装";
    }

    /// <summary>已装的话读出版本号，读不到就返回空。</summary>
    private static string ReadInstalledVersion(string gameDir)
    {
        try
        {
            var versionFile = Path.Combine(gameDir, DoorstopVersion);
            if (File.Exists(versionFile))
            {
                var text = File.ReadAllText(versionFile, Encoding.UTF8).Trim();
                if (text.Length > 0) return "Doorstop " + text;
            }
        }
        catch
        {
            // 读不到版本不影响安装流程
        }
        return "";
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCFishController/" + UpdateChecker.CurrentVersion);
        return client;
    }

    /// <summary>
    /// 在构建站首页里找最新的 win-x64 包地址。
    /// 页面按构建号从新到旧排列，所以第一个匹配就是最新版。
    /// </summary>
    private static (string Url, string FileName, string Build) ParseLatest(string html, string runtime)
    {
        var pattern = "href=\"(/projects/bepinex_be/(\\d+)/BepInEx-Unity\\." + runtime +
                      "-win-x64-([^\"%]+)(%2B[^\"]+)?\\.zip)\"";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return (null, null, null);
        var url = SiteRoot + match.Groups[1].Value;
        var build = match.Groups[2].Value;
        var name = $"BepInEx-Unity.{runtime}-win-x64-{match.Groups[3].Value}.zip";
        return (url, name, build);
    }

    /// <summary>安装或升级 BepInEx。会先下载并校验，再备份既有文件，最后写入。</summary>
    internal static async Task<BepInExInstallResult> InstallAsync(string gameDir, Action<string> report)
    {
        var result = new BepInExInstallResult();
        void Step(string text)
        {
            result.Steps.Add(text);
            report?.Invoke(text);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            {
                result.Message = "游戏目录不存在，请先设置正确的目录。";
                return result;
            }
            if (!File.Exists(Path.Combine(gameDir, "PCFish.exe")))
            {
                result.Message = "这个目录里没有 PCFish.exe，请确认选的是游戏根目录。";
                return result;
            }

            var runtime = DetectRuntime(gameDir);
            Step($"游戏运行时：{runtime}");

            if (IsInstalled(gameDir))
            {
                result.AlreadyInstalled = true;
                result.Version = ReadInstalledVersion(gameDir);
                Step("检测到 BepInEx 已安装" + (result.Version.Length > 0 ? $"（{result.Version}）" : ""));
            }

            // 游戏运行时 winhttp.dll 被占用，覆盖会失败。
            if (Process.GetProcessesByName("PCFish").Length > 0)
            {
                result.Message = "游戏正在运行，BepInEx 的文件被占用。请先退出游戏再安装。";
                return result;
            }

            Step("正在查询 BepInEx 最新版本…");
            string html;
            using (var client = CreateClient())
            {
                html = await client.GetStringAsync(BuildPage);
            }

            var (url, fileName, build) = ParseLatest(html, runtime);
            if (url == null)
            {
                result.Message = $"在构建站上没有找到 {runtime} 的 Windows x64 包，请到 https://builds.bepinex.dev 手动下载。";
                return result;
            }
            Step($"目标版本：构建 {build}（{fileName}）");

            // 下载到临时目录
            var tempRoot = Path.Combine(Path.GetTempPath(), "pcfish-bepinex");
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, fileName);

            Step($"正在下载 {fileName}…");
            using (var client = CreateClient())
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(zipPath);
                await source.CopyToAsync(target);
            }
            var sizeMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
            Step($"下载完成（{sizeMb:F1} MB）");

            // 解压，确认包结构
            var extractDir = Path.Combine(tempRoot, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            if (!File.Exists(Path.Combine(extractDir, WinHttpDll)) ||
                !File.Exists(Path.Combine(extractDir, DoorstopConfig)))
            {
                result.Message = "压缩包内容和预期不符，缺少 winhttp.dll 或 doorstop_config.ini。已停止，未改动游戏目录。";
                return result;
            }

            // 备份将要被覆盖的文件
            var backupDir = Path.Combine(gameDir, "BepInEx-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var backed = new List<string>();
            foreach (var name in new[] { WinHttpDll, DoorstopConfig, DoorstopVersion, "changelog.txt" })
            {
                var existing = Path.Combine(gameDir, name);
                if (!File.Exists(existing)) continue;
                Directory.CreateDirectory(backupDir);
                File.Copy(existing, Path.Combine(backupDir, name), overwrite: true);
                backed.Add(name);
            }
            if (File.Exists(Path.Combine(gameDir, CoreMarker)))
            {
                Directory.CreateDirectory(backupDir);
                var coreBackup = Path.Combine(backupDir, "BepInEx-core");
                Directory.CreateDirectory(coreBackup);
                foreach (var file in Directory.GetFiles(Path.Combine(gameDir, "BepInEx", "core")))
                    File.Copy(file, Path.Combine(coreBackup, Path.GetFileName(file)), overwrite: true);
                backed.Add(@"BepInEx\core");
            }
            if (backed.Count > 0) Step("已备份原有文件：" + string.Join("、", backed));

            // 复制进去。目录结构保持原样。
            var copied = 0;
            foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extractDir, file);
                var destination = Path.Combine(gameDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied++;
            }
            Step($"已写入 {copied} 个文件到游戏目录");

            // 安装插件
            var pluginSource = Path.Combine(AppContext.BaseDirectory, GameDeploy.PluginDllName);
            var pluginTarget = Path.Combine(gameDir, GameDeploy.PluginsRelativePath, GameDeploy.PluginDllName);
            if (File.Exists(pluginSource))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pluginTarget)!);
                File.Copy(pluginSource, pluginTarget, overwrite: true);
                Step("已安装 PCFish 插件到 BepInEx\\plugins");
            }

            Step("正在清理临时文件…");
            try { Directory.Delete(tempRoot, recursive: true); } catch { }

            result.Ok = true;
            result.NeedRestart = true;
            result.Version = "build " + build;
            result.Message = result.AlreadyInstalled
                ? "BepInEx 已更新到 " + result.Version + "。请启动游戏。"
                : "BepInEx 6 安装完成。请启动游戏，首次运行会在 config 目录生成配置文件。";
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            result.Message = "写入游戏目录被拒绝。请关闭游戏再试，或以管理员身份运行助手。";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = "安装失败：" + ex.Message;
            return result;
        }
    }
}

