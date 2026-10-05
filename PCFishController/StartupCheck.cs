using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PCFishController;

/// <summary>一次开机自检的结果。</summary>
internal sealed class StartupCheckResult
{
    internal bool Ok;
    /// <summary>给界面用的一句话结论。</summary>
    internal string Summary = "";
    /// <summary>逐项检测明细，写在日志里方便排查。</summary>
    internal readonly List<string> Details = new();
    /// <summary>失败项是否可以靠一键部署修好。</summary>
    internal bool FixableByDeploy;
    /// <summary>阻塞连接的原因，直接显示给用户。</summary>
    internal string BlockingReason = "";
    internal string GameDir = "";
}

/// <summary>
/// 启动自检：确认游戏的运行环境和插件与助手保持一致，齐全了才允许连接。
///
/// 只读，不写游戏目录。检查项：
///   1. 找得到游戏目录（有 PCFish.exe）
///   2. BepInEx 运行环境已安装
///   3. BepInEx\plugins 里有插件
///   4. 插件与助手自带的那个是同一份（哈希一致，避免版本错配）
///   5. 插件配置文件存在，且动作总闸已打开
///
/// 第 1~3、5 项可以直接靠一键部署修好；第 4 项也能靠重新复制插件修好。
/// 只有「游戏正在运行导致文件被占用」这类情况需要用户先退出游戏。
/// </summary>
internal static class StartupCheck
{
    private const string AllowActionsKey = "允许外部执行动作";

    internal static StartupCheckResult Run(string preferredGameDir)
    {
        var result = new StartupCheckResult();
        void Fail(string summary, string reason, bool fixable = true)
        {
            result.Ok = false;
            result.Summary = summary;
            result.BlockingReason = reason;
            result.FixableByDeploy = fixable;
        }

        // ---------- 1. 游戏目录 ----------
        var gameDir = GameDeploy.FindGameDir(preferredGameDir);
        if (gameDir == null)
        {
            result.Details.Add("游戏目录：未找到");
            Fail("没找到游戏目录", "没找到 PCFish 游戏目录，无法确认运行环境。请点「一键部署」指定。");
            return result;
        }
        result.GameDir = gameDir;
        result.Details.Add("游戏目录：" + gameDir);

        // ---------- 2. BepInEx 运行环境 ----------
        if (!BepInExInstaller.IsInstalled(gameDir))
        {
            result.Details.Add("BepInEx 运行环境：缺失");
            Fail("缺少 BepInEx 运行环境", "游戏还没有安装 BepInEx 运行环境。请点「一键部署」安装。");
            return result;
        }
        var bepInEx = BepInExInstaller.DescribeInstalled(gameDir);
        result.Details.Add("BepInEx 运行环境：" + bepInEx);

        // ---------- 3. 插件是否存在 ----------
        var installedPlugin = Path.Combine(gameDir, GameDeploy.PluginsRelativePath, GameDeploy.PluginDllName);
        if (!File.Exists(installedPlugin))
        {
            result.Details.Add("插件：未安装");
            Fail("游戏里没有助手插件", "BepInEx\\plugins 里没有 " + GameDeploy.PluginDllName +
                 "，助手无法连接。请点「一键部署」安装。");
            return result;
        }

        // ---------- 4. 插件是否与助手一致 ----------
        var localPlugin = FindLocalPlugin();
        if (localPlugin == null)
        {
            // 助手旁边没有插件副本，只能报版本号，不做强一致性判断。
            result.Details.Add("插件：已安装（助手旁边没有副本可比对）");
        }
        else if (!SameFile(localPlugin, installedPlugin))
        {
            result.Details.Add("插件：与助手版本不一致");
            result.Details.Add("  助手自带：" + DescribeVersion(localPlugin));
            result.Details.Add("  游戏内：" + DescribeVersion(installedPlugin));
            Fail("游戏里的插件与助手不一致",
                "游戏里的插件和助手自带的那份不一致，可能导致连接失败。请点「一键部署」同步。");
            return result;
        }
        else
        {
            result.Details.Add("插件：与助手一致（" + DescribeVersion(installedPlugin) + "）");
        }

        // ---------- 5. 配置文件与动作总闸 ----------
        var configPath = Path.Combine(gameDir, GameDeploy.ConfigRelativePath);
        if (!File.Exists(configPath))
        {
            result.Details.Add("配置文件：缺失");
            Fail("插件配置文件还没生成",
                "插件第一次运行游戏时才会生成配置文件。请点「一键部署」，它会启动一次游戏来生成。");
            return result;
        }

        var allow = GameDeploy.ReadAllowActions(gameDir);
        if (allow == null)
        {
            result.Details.Add("动作总闸：读不到");
            Fail("配置文件里读不到动作总闸",
                "配置文件里没有「" + AllowActionsKey + "」这一项，请确认插件版本正确，或点「一键部署」修复。");
            return result;
        }
        if (allow == false)
        {
            result.Details.Add("动作总闸：关闭");
            Fail("游戏内动作总闸还没打开",
                "游戏内还禁止外部执行动作。请点「一键部署」打开它；改完需要重启游戏才生效。");
            return result;
        }
        result.Details.Add("动作总闸：已打开");

        result.Ok = true;
        result.Summary = "运行环境与插件均已就绪，与助手一致。";
        return result;
    }

    /// <summary>助手旁边自带的插件副本，用于比对游戏里那份是否一致。</summary>
    private static string FindLocalPlugin()
    {
        var baseDir = AppContext.BaseDirectory;
        return new[]
        {
            Path.Combine(baseDir, GameDeploy.PluginDllName),
            Path.Combine(baseDir, "plugin", GameDeploy.PluginDllName),
            Path.Combine(baseDir, "plugins", GameDeploy.PluginDllName)
        }.FirstOrDefault(File.Exists);
    }

    private static bool SameFile(string left, string right)
    {
        try
        {
            if (!File.Exists(left) || !File.Exists(right)) return false;
            using var sha = SHA256.Create();
            using var a = File.OpenRead(left);
            using var b = File.OpenRead(right);
            return Convert.ToHexString(sha.ComputeHash(a)) == Convert.ToHexString(sha.ComputeHash(b));
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? Path.GetFileName(path) : version;
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }
}
