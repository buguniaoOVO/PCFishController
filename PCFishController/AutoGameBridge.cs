namespace PCFishController;

/// <summary>
/// 自动连接：先走 Frida 内存连接，附加不上时再退到游戏内插件桥接。
///
/// 只把当前生效的那条连接暴露给界面和动作逻辑，避免两条通道同时回包。
/// </summary>
internal sealed class AutoGameBridge : IGameBridge
{
    private readonly FridaGameClient _frida;
    private readonly BridgeClient _plugin;
    private readonly object _sync = new();
    private readonly TimeSpan _fallbackAfter = TimeSpan.FromSeconds(90);
    private volatile IGameBridge _active;
    private Func<bool> _allowConnect;
    private bool _pluginStarted;
    private bool _reportedConnected;

    internal AutoGameBridge(FridaGameClient frida, BridgeClient plugin)
    {
        _frida = frida;
        _plugin = plugin;
    }

    public bool Connected => _active?.Connected == true;
    public string ModeName => _active?.ModeName ?? _frida.ModeName;
    public string ProtocolLabel => _active?.ProtocolLabel ?? _frida.ProtocolLabel;
    public bool RequiresPlugin => _active?.RequiresPlugin ?? false;
    public bool RequiresArm => _active?.RequiresArm ?? false;
    public bool SupportsServerAudit => _active?.SupportsServerAudit ?? false;

    public event Action<bool> ConnectionChanged;
    public event Action<string> LineReceived;

    public void Start(Func<bool> allowConnect = null)
    {
        _allowConnect = allowConnect;
        _frida.ConnectionChanged += OnFridaConnection;
        _plugin.ConnectionChanged += OnPluginConnection;
        _frida.LineReceived += line => { if (ReferenceEquals(_active, _frida)) LineReceived?.Invoke(line); };
        _plugin.LineReceived += line => { if (ReferenceEquals(_active, _plugin)) LineReceived?.Invoke(line); };
        _frida.Start(allowConnect);
        var watchdog = new Thread(Watchdog) { IsBackground = true, Name = "auto-bridge-watchdog" };
        watchdog.Start();
    }

    private void Watchdog()
    {
        var disconnectedSince = DateTime.Now;
        while (true)
        {
            if (_frida.Connected)
            {
                disconnectedSince = DateTime.Now;
            }
            else if (DateTime.Now - disconnectedSince >= _fallbackAfter && GamePastStartupGrace())
            {
                StartPlugin();
                return;
            }
            Thread.Sleep(250);
        }
    }

    private static bool GamePastStartupGrace()
    {
        using var game = GameProcess.Find();
        if (game == null) return false;
        try { return DateTime.Now - game.StartTime >= TimeSpan.FromSeconds(60); }
        catch { return false; }
    }

    private void StartPlugin()
    {
        lock (_sync)
        {
            if (_pluginStarted) return;
            _pluginStarted = true;
        }
        _plugin.Start(_allowConnect);
    }

    private void OnFridaConnection(bool connected)
    {
        lock (_sync)
        {
            if (connected) _active = _frida;
            else if (ReferenceEquals(_active, _frida) && !_plugin.Connected) _active = null;
        }
        RaiseConnection();
    }

    private void OnPluginConnection(bool connected)
    {
        lock (_sync)
        {
            if (connected && !_frida.Connected) _active = _plugin;
        }
        RaiseConnection();
    }

    private void RaiseConnection()
    {
        bool now;
        lock (_sync) now = _active?.Connected == true;
        bool changed;
        lock (_sync)
        {
            changed = now != _reportedConnected;
            _reportedConnected = now;
        }
        if (changed) ConnectionChanged?.Invoke(now);
    }

    public void Send(string line) => _active?.Send(line);

    public void Dispose()
    {
        try { _frida.Dispose(); } catch { }
        try { _plugin.Dispose(); } catch { }
    }
}
