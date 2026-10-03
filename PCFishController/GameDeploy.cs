using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PCFishController;

/// <summary>
/// 一键部署的结果。Steps 是给用户看的执行清单，失败时也能看出卡在哪一步。
/// </summary>
internal sealed class DeployResult
{
    internal bool Ok;
    internal string Message = "";
    internal string GameDir = "";
    internal string ConfigPath = "";
    internal bool ConfigChanged;
    internal bool PluginCopied;
    internal bool BepInExInstalled;
    internal bool GameStartedForConfig;
    internal bool NeedRestart;
    internal readonly List<string> Steps = new();
}

/// <summary>
/// 一键部署：把运行前的全部要求一次做完。
///
///  1. 检测 BepInEx 运行环境，缺了就自动从官方构建站下载安装
///  2. 复制插件到 BepInEx\plugins
///  3. 首次部署时启动一次游戏，让插件生成 cfg
///  4. 打开 cfg 里的动作总闸，并对齐端口
///  5. 关掉第 3 步启动的那个游戏进程，让用户看到干净状态
///
/// 前三步都在写游戏目录，所以必须先退出游戏。第 4 步改完 cfg 需要重启游戏才生效，
/// 函数会把这一点如实报给用户。
/// </summary>
internal static class GameDeploy
{
    internal const string PluginDllName = "PCFishAutoHelper.dll";
    internal const string ConfigRelativePath = @"BepInEx\config\pcfish.autohelper.cfg";
    internal const string PluginsRelativePath = @"BepInEx\plugins";

    private const string AllowActionsKey = "允许外部执行动作";
    private const string PortKey = "端口";

    /// <summary>找一个含 PCFish.exe 的游戏目录。装没装 BepInEx 都算数。</summary>
    internal static string FindGameDir(string preferred)
    {
        foreach (var candidate in Candidates(preferred))
        {
            if (IsGameDir(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) yield return preferred;

        // 正在运行的游戏最可信。权限不足时读不到路径，继续用后面的候选。
        var runningDir = "";
        try
        {
            var path = GameProcess.Find()?.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path)) runningDir = Path.GetDirectoryName(path);
        }
        catch
        {
        }
        if (!string.IsNullOrWhiteSpace(runningDir)) yield return runningDir;

        var libraries = new List<string>();
        foreach (var steamRoot in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                     @"D:\Steam",
                     @"D:\SteamLibrary",
                     @"E:\SteamLibrary"
                 })
        {
            if (Directory.Exists(steamRoot)) libraries.Add(steamRoot);
            var folder = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(folder)) continue;
            try
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(folder), @"""path""\s+""([^""]+)"""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch
            {
                // vdf 读不动就只用已知根目录
            }
        }

        foreach (var library in libraries)
        {
            yield return Path.Combine(library, "steamapps", "common", "PC FISH");
            yield return Path.Combine(library, "common", "PC FISH");
        }
    }

    /// <summary>游戏根目录的判定：有 PCFish.exe 就算。</summary>
    private static bool IsGameDir(string dir)
        => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "PCFish.exe"));

    /// <summary>当前游戏内是否允许动作。cfg 不存在时返回 null。</summary>
    internal static bool? ReadAllowActions(string gameDir)
    {
        var path = Path.Combine(gameDir ?? "", ConfigRelativePath);
        if (!File.Exists(path)) return null;
        try
        {
            var match = Regex.Match(File.ReadAllText(path, Encoding.UTF8),
                @"^\s*" + Regex.Escape(AllowActionsKey) + @"\s*=\s*(true|false)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success && bool.TryParse(match.Groups[1].Value, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FindPluginSource()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, PluginDllName),
            Path.Combine(baseDir, "plugin", PluginDllName),
            Path.Combine(baseDir, "plugins", PluginDllName)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool SameFile(string left, string right)
    {
        try
        {
            if (!File.Exists(left) || !File.Exists(right)) return false;
            using var a = SHA256.Create();
            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);
            return Convert.ToHexString(a.ComputeHash(leftStream)) ==
                   Convert.ToHexString(a.ComputeHash(rightStream));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>执行完整的一键部署。</summary>
    internal static async Task<DeployResult> RunAsync(string preferredGameDir, int port,
        Action<string> report)
    {
        var result = new DeployResult();
        void Step(string text)
        {
            result.Steps.Add(text);
            report?.Invoke(text);
        }

        // ---------- 0. 定位游戏目录 ----------
        var gameDir = FindGameDir(preferredGameDir);
        if (gameDir == null)
        {
            result.Message = "没找到游戏目录。请确认已安装 PCFish，或点「浏览…」手动指定。";
            return result;
        }
        result.GameDir = gameDir;
        result.ConfigPath = Path.Combine(gameDir, ConfigRelativePath);
        Step($"游戏目录：{gameDir}");

        var gameRunning = Process.GetProcessesByName(GameProcess.Name).Length > 0;

        // ---------- 1. BepInEx 运行环境 ----------
        if (BepInExInstaller.IsInstalled(gameDir))
        {
            Step("BepInEx 运行环境已就绪（" + BepInExInstaller.DescribeInstalled(gameDir) + "）");
        }
        else
        {
            if (gameRunning)
            {
                result.Message = "还没安装 BepInEx，而游戏正在运行。请先退出游戏，再点一键部署。";
                return result;
            }
            Step("未检测到 BepInEx，正在自动安装运行环境…");
            var install = await BepInExInstaller.InstallAsync(gameDir,
                text => Step("  " + text), installPlugin: false);
            if (!install.Ok)
            {
                result.Message = "运行环境安装失败：" + install.Message;
                return result;
            }
            result.BepInExInstalled = true;
            result.NeedRestart = true;
            Step("BepInEx 运行环境已安装（" + install.Version + "）");
        }

        // ---------- 2. 插件 ----------
        var source = FindPluginSource();
        var target = Path.Combine(gameDir, PluginsRelativePath, PluginDllName);
        if (source != null && !SameFile(source, target))
        {
            if (gameRunning)
            {
                result.Message = "需要更新插件，但游戏正在运行，插件文件被占用。请先退出游戏。";
                return result;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, overwrite: true);
                result.PluginCopied = true;
                result.NeedRestart = true;
                Step("已安装插件到 BepInEx\\plugins");
            }
            catch (Exception ex)
            {
                result.Message = "复制插件失败：" + ex.Message;
                return result;
            }
        }
        else if (source != null)
        {
            Step("插件已是当前版本");
        }
        else if (!File.Exists(target))
        {
            result.Message = "没找到插件文件。请把 PCFishAutoHelper.dll 放到助手旁边，" +
                             "或手动放进 " + Path.Combine(gameDir, PluginsRelativePath) + "。";
            return result;
        }
        else
        {
            Step("插件已安装");
        }

        if (GameConsole.DisableInConfig(gameDir)) Step("已设置 BepInEx 控制台默认隐藏");

        // ---------- 3. 生成 cfg：插件只在游戏加载时创建它 ----------
        if (!File.Exists(result.ConfigPath))
        {
            if (gameRunning)
            {
                result.Message = "插件首次运行才会生成配置文件。请先退出游戏，再点一次一键部署。";
                return result;
            }
            Step("首次部署，启动一次游戏以生成配置文件…");
            if (!await GenerateConfigAsync(gameDir, result.ConfigPath, text => Step("  " + text)))
            {
                result.Message = "启动游戏后仍没等到配置文件。请手动启动游戏，确认插件加载后重试。";
                return result;
            }
            result.GameStartedForConfig = true;
            Step("配置文件已生成");
        }

        // ---------- 4. 打开总闸并对齐端口 ----------
        string text;
        try
        {
            text = File.ReadAllText(result.ConfigPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            result.Message = "读取配置文件失败：" + ex.Message;
            return result;
        }

        var changed = new List<string>();
        var allowPattern = @"(?m)^(\s*" + Regex.Escape(AllowActionsKey) + @"\s*=\s*)(true|false)\s*$";
        var allowMatch = Regex.Match(text, allowPattern, RegexOptions.IgnoreCase);
        if (!allowMatch.Success)
        {
            result.Message = "配置文件里没有「" + AllowActionsKey + "」这一项，请确认插件版本正确。";
            return result;
        }
        if (allowMatch.Groups[2].Value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            text = Regex.Replace(text, allowPattern, m => m.Groups[1].Value + "true", RegexOptions.IgnoreCase);
            changed.Add("允许外部执行动作 = true");
        }

        var portPattern = @"(?m)^(\s*" + Regex.Escape(PortKey) + @"\s*=\s*)(\d+)\s*$";
        var portMatch = Regex.Match(text, portPattern);
        if (portMatch.Success && int.Parse(portMatch.Groups[2].Value) != port)
        {
            var oldPort = portMatch.Groups[2].Value;
            text = Regex.Replace(text, portPattern, m => m.Groups[1].Value + port);
            changed.Add($"端口 {oldPort} → {port}");
        }

        if (changed.Count > 0)
        {
            try
            {
                File.WriteAllText(result.ConfigPath, text, new UTF8Encoding(false));
                result.ConfigChanged = true;
                result.NeedRestart = true;
                Step("已写入配置：" + string.Join("、", changed));
            }
            catch (Exception ex)
            {
                result.Message = "写入配置失败：" + ex.Message +
                                 "（游戏正在运行时可能锁定文件，请关闭游戏后重试）";
                return result;
            }
        }
        else
        {
            Step("配置已是最新");
        }

        // ---------- 5. 关掉为了生成配置而启动的游戏 ----------
        if (result.GameStartedForConfig)
        {
            Step("关闭刚才用于生成配置的游戏进程…");
            await CloseGameAsync();
            Step("已关闭游戏");
            result.NeedRestart = true;
        }

        result.Ok = true;
        result.Message = result.NeedRestart
            ? "部署完成。现在可以启动游戏，助手会自动连接。"
            : "部署完成，当前配置已经开始生效。";
        return result;
    }

    private static async Task CloseGameAsync()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(GameProcess.Name))
            {
                try { process.CloseMainWindow(); } catch { }
            }
            for (var i = 0; i < 24; i++)
            {
                if (Process.GetProcessesByName(GameProcess.Name).Length == 0) return;
                await Task.Delay(500);
            }
            foreach (var process in Process.GetProcessesByName(GameProcess.Name))
            {
                try { process.Kill(); } catch { }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 启动游戏并等插件生成配置文件。
    ///
    /// 刚装好 BepInEx 的机器上，第一次启动要先按 GameAssembly 生成 interop 程序集，
    /// 视机器快慢需要一两分钟，部分版本还要再启动一次插件才会真正执行。所以最多启动两轮，
    /// 每轮等 5 分钟，并每隔 30 秒报告一次，让界面看得出还在等。
    /// </summary>
    private static async Task<bool> GenerateConfigAsync(string gameDir, string configPath,
        Action<string> report)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt > 1) report("还没生成配置，再启动一次游戏…");
            if (await StartGameAndWaitForConfigAsync(gameDir, configPath, report)) return true;
            if (attempt == 1)
            {
                report("本轮没等到配置，关闭游戏后重试一次…");
                await CloseGameAsync();
                await Task.Delay(3000);
            }
        }
        // 两轮都没成功，别把游戏留在运行状态。
        await CloseGameAsync();
        return false;
    }

    private static async Task<bool> StartGameAndWaitForConfigAsync(string gameDir, string configPath,
        Action<string> report)
    {
        try
        {
            var exe = Path.Combine(gameDir, "PCFish.exe");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gameDir, UseShellExecute = true });
            report("已启动游戏，等待插件生成配置…");
        }
        catch (Exception ex)
        {
            report("启动游戏失败：" + ex.Message);
            return false;
        }

        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(1000);
            if (File.Exists(configPath))
            {
                // 插件可能还在往文件里补内容，等它稳定
                await Task.Delay(1500);
                return true;
            }
            if ((i + 1) % 30 == 0) report($"仍在等待插件加载（{i + 1} 秒）…");
        }
        return false;
    }
}
