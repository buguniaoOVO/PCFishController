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
    internal bool NeedRestart;
    internal readonly List<string> Steps = new();
}

/// <summary>
/// 定位游戏目录、按需复制插件、把游戏内 cfg 的「允许外部执行动作」打开。
///
/// 这一键替掉的手工步骤是：自己找游戏目录 → 放插件 → 启动游戏生成 cfg →
/// 打开 cfg 把总闸改成 true → 再重启游戏。控制器只改 cfg 里的两个值，不碰存档。
/// </summary>
internal static class GameDeploy
{
    internal const string PluginDllName = "PCFishAutoHelper.dll";
    internal const string ConfigRelativePath = @"BepInEx\config\pcfish.autohelper.cfg";
    internal const string PluginsRelativePath = @"BepInEx\plugins";

    private const string AllowActionsKey = "允许外部执行动作";
    private const string PortKey = "端口";

    /// <summary>找一个含 PCFish.exe 且装过 BepInEx 的目录。</summary>
    internal static string FindGameDir(string preferred)
    {
        foreach (var candidate in Candidates(preferred))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && IsGameDir(candidate)) return candidate;
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

    private static bool IsGameDir(string dir)
        => !string.IsNullOrWhiteSpace(dir)
           && File.Exists(Path.Combine(dir, "PCFish.exe"))
           && Directory.Exists(Path.Combine(dir, "BepInEx"));

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

    /// <summary>执行一键部署。只写 cfg 的两个值，并在需要时复制插件 DLL。</summary>
    internal static DeployResult Run(string preferredGameDir, int port)
    {
        var result = new DeployResult();
        var gameDir = FindGameDir(preferredGameDir);
        if (gameDir == null)
        {
            result.Message = "没找到游戏目录。请确认已安装 PCFish，或在游戏设置里手动指定目录。";
            return result;
        }

        result.GameDir = gameDir;
        result.Steps.Add($"游戏目录：{gameDir}");

        var configPath = Path.Combine(gameDir, ConfigRelativePath);
        result.ConfigPath = configPath;

        // 1) 插件 DLL：控制器旁边放着就顺手装上，没有就只检查。
        var source = FindPluginSource();
        var target = Path.Combine(gameDir, PluginsRelativePath, PluginDllName);
        if (source != null && !SameFile(source, target))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, overwrite: true);
                result.PluginCopied = true;
                result.NeedRestart = true;
                result.Steps.Add("已复制插件到 BepInEx\\plugins");
            }
            catch (Exception ex)
            {
                result.Steps.Add("插件复制失败（游戏可能正在运行）：" + ex.Message);
            }
        }
        else if (source != null)
        {
            result.Steps.Add("插件已是当前版本");
        }
        else if (!File.Exists(target))
        {
            result.Message = "没找到插件文件。请把 PCFishAutoHelper.dll 放到助手旁边，或手动放进 " +
                             Path.Combine(gameDir, PluginsRelativePath) + "。";
            return result;
        }
        else
        {
            result.Steps.Add("插件已安装");
        }

        // 2) 游戏内 cfg：必须先在游戏里跑过一次，插件才会生成它。
        if (!File.Exists(configPath))
        {
            result.Message = "还没生成游戏内配置。请先启动一次游戏（插件会自动生成 cfg），再点一键部署。";
            return result;
        }

        string text;
        try
        {
            text = File.ReadAllText(configPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            result.Message = "读取游戏内配置失败：" + ex.Message;
            return result;
        }

        var changed = new List<string>();

        var allowPattern = @"(?m)^(\s*" + Regex.Escape(AllowActionsKey) + @"\s*=\s*)(true|false)\s*$";
        var allowMatch = Regex.Match(text, allowPattern, RegexOptions.IgnoreCase);
        if (!allowMatch.Success)
        {
            result.Message = "游戏内配置里没有「" + AllowActionsKey + "」这一项，请确认插件版本与游戏目录正确。";
            return result;
        }
        if (allowMatch.Groups[2].Value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            text = Regex.Replace(text, allowPattern, m => m.Groups[1].Value + "true", RegexOptions.IgnoreCase);
            changed.Add("允许外部执行动作 true");
        }

        // 端口必须和控制器一致，否则连不上。
        var portPattern = @"(?m)^(\s*" + Regex.Escape(PortKey) + @"\s*=\s*)(\d+)\s*$";
        var portMatch = Regex.Match(text, portPattern);
        if (portMatch.Success)
        {
            var current = int.Parse(portMatch.Groups[2].Value);
            if (current != port)
            {
                text = Regex.Replace(text, portPattern, m => m.Groups[1].Value + port);
                changed.Add($"端口 {current} → {port}");
            }
        }

        // 3) 写回。值本来就是对的就不动文件，避免无意义的重启提示。
        if (changed.Count > 0)
        {
            try
            {
                File.WriteAllText(configPath, text, new UTF8Encoding(false));
                result.ConfigChanged = true;
                result.NeedRestart = true;
                result.Steps.Add("已写入 cfg：" + string.Join("、", changed));
            }
            catch (Exception ex)
            {
                result.Message = "写入游戏内配置失败：" + ex.Message +
                                 "（游戏正在运行时可能锁定文件，请关闭游戏后重试）";
                return result;
            }
        }
        else
        {
            result.Steps.Add("游戏内配置已是最新");
        }

        result.Ok = true;
        result.Message = result.NeedRestart
            ? "部署完成。请重启游戏让设置生效。"
            : "部署完成，当前配置已经开始生效。";
        return result;
    }
}
