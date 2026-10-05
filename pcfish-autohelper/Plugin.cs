using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace PCFishAutoHelper;

/// <summary>
/// BepInEx 插件入口。
///
/// 进游戏后在本机打开只监听 127.0.0.1 的数据桥接。
/// 繁育经游戏网络入口与原生完成回调执行，失败保留网络错误证据。
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "pcfish.autohelper";
    public const string PluginName = "PC FISH Auto Helper (Bridge)";
public const string PluginVersion = "0.36.0";

    internal static ManualLogSource Logger;
    internal static Plugin Instance;

    internal static ConfigEntry<int> CfgPort;
    internal static ConfigEntry<bool> CfgAllowActions;
    internal static ConfigEntry<int> CfgMinActionInterval;

    public override void Load()
    {
        Logger = Log;
        Instance = this;

        CfgPort = Config.Bind("桥接", "端口", 27777,
            "本机监听端口。只绑 127.0.0.1，不对局域网开放。改这里后控制器那边也要改。");

        CfgAllowActions = Config.Bind("安全", "允许外部执行动作", false,
            "总闸。false 时桥接接受连接、会上报游戏状态，但拒绝 BREED/UPGRADE 命令。" +
            "需要真实执行动作时改 true 并重启游戏。这是防止本机其它程序偷偷驱动游戏的最后一道锁。");

        CfgMinActionInterval = Config.Bind("安全", "最小动作间隔秒", 60,
            "桥接侧强制的最小间隔，写小于 60 也会被强制抬到 60。每次繁育或升级至少间隔 1 分钟。");

        Journal.Init(Path.Combine(Paths.BepInExRootPath, "PCFishAutoHelper.log"));
        Journal.Write($"=== {PluginName} v{PluginVersion} 启动 ===");
        Journal.Write($"游戏目录: {Paths.GameRootPath}");

        var diagnosticsReady = false;
        try
        {
            new Harmony(PluginGuid + ".network-diagnostics").PatchAll(typeof(Plugin).Assembly);
            diagnosticsReady = true;
            Journal.Write("繁育网络旁路诊断已启用");
        }
        catch (Exception ex) { Journal.Error("网络诊断加载失败，动作保持关闭", ex); }
        var apiReady = NativeBreedingApi.Validate(out var apiDetail);
        Journal.Write(apiDetail);
        BridgeServer.Configure(CfgAllowActions.Value && diagnosticsReady && apiReady, CfgMinActionInterval.Value, CfgPort.Value);

        AddComponent<AutoHelperBehaviour>();

        Log.LogInfo($"{PluginName} v{PluginVersion} 已加载：桥接端口={CfgPort.Value}、" +
                    $"放行动作={CfgAllowActions.Value && diagnosticsReady && apiReady}。繁育使用原生完整收尾。");
    }
}

/// <summary>
/// 独立日志文件，避免和 BepInEx 主日志混在一起。
/// 会被主线程和 socket 线程同时调用，所以必须加锁。
/// </summary>
internal static class Journal
{
    private static readonly object Sync = new();
    private static string _path;

    internal static void Init(string path)
    {
        _path = path;
    }

    internal static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Plugin.Logger?.LogInfo(message);
        if (_path == null) return;
        try
        {
            lock (Sync)
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去不能影响游戏
        }
    }

    internal static void Error(string message, Exception ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex != null) sb.Append(" -> ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        Plugin.Logger?.LogError(sb.ToString());
        if (_path == null) return;
        try
        {
            lock (Sync)
            {
                File.AppendAllText(_path,
                    $"[{DateTime.Now:HH:mm:ss}] !! {sb}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
