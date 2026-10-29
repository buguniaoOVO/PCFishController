namespace PCFishController;

/// <summary>
/// 控制器和游戏之间的连接。两种实现：
///   · BridgeClient   —— 游戏内 BepInEx 插件开的本地端口。
///   · FridaGameClient —— 按 PID 附加游戏进程，在内存里加载脚本；不往游戏目录放插件。
/// 两者对 MainForm 暴露同一套事件和行协议，界面和选鱼逻辑不用区分。
/// </summary>
internal interface IGameBridge : IDisposable
{
    bool Connected { get; }
    /// <summary>连接方式，用于界面显示。</summary>
    string ModeName { get; }
    /// <summary>底部状态行里的连接类型，例如“Frida 内存连接 / 插件桥接”。</summary>
    string ProtocolLabel { get; }
    /// <summary>是否需要游戏目录里的 BepInEx 插件。</summary>
    bool RequiresPlugin { get; }
    /// <summary>是否需要单独点 ARM 放行动作。</summary>
    bool RequiresArm { get; }
    /// <summary>是否提供独立的服务器繁育次数核对。</summary>
    bool SupportsServerAudit { get; }
    event Action<bool> ConnectionChanged;
    event Action<string> LineReceived;
    void Start(Func<bool> allowConnect = null);
    void Send(string line);
}
