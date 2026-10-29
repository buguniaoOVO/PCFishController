using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PCFishController;

/// <summary>
/// 无插件连接：按 PID 找到 PCFish，由随程序分发的 PCFishFrida worker 在游戏进程内加载脚本，
/// 控制器通过 127.0.0.1 上的同一条行协议读取状态、发送动作。
///
/// worker 不写游戏目录，也不在游戏里留下插件文件；它仍在游戏进程内执行代码，
/// 所以界面上写“Frida 内存连接”，不写成“无进程内代码”。
/// </summary>
internal sealed class FridaGameClient : IGameBridge
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _workerPath;
    private readonly string _logPath;
    private readonly int _targetPid;
    private readonly object _sync = new();
    private Process _worker;
    private TcpClient _tcp;
    private NetworkStream _stream;
    private Thread _thread;
    private volatile bool _wanted;
    private volatile bool _connected;
    private Func<bool> _allowConnect;
    private bool _workerReported;

    /// <summary>
    /// 拿到鉴权后才创建。非 null 时合成改走进程外 HTTP 直发，绕开游戏 UI 状态；
    /// 为 null 时保持原来的游戏内动作路径，行为与改动前完全一致。
    /// </summary>
    private GameApiClient _api;
    /// <summary>上一次请求鉴权的时间，用于失败后节流重试。</summary>
    private DateTime _lastAuthAttempt = DateTime.MinValue;

    internal FridaGameClient(string host, int port, string workerPath, string logPath, int targetPid = 0)
    {
        _host = host;
        _port = port;
        _workerPath = workerPath;
        _logPath = logPath;
        _targetPid = targetPid;
    }

    public bool Connected => _connected;
    public string ModeName => "Frida 内存连接";
    public string ProtocolLabel => "Frida 内存连接";
    public bool RequiresPlugin => false;
    public bool RequiresArm => false;
    public bool SupportsServerAudit => false;

    public event Action<bool> ConnectionChanged;
    public event Action<string> LineReceived;

    public void Start(Func<bool> allowConnect = null)
    {
        _allowConnect = allowConnect;
        _wanted = true;
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "frida-client" };
        _thread.Start();
    }

    public void Send(string line)
    {
        // 已拿到鉴权时，合成改由进程外 HTTP 直发：不再模拟点游戏 UI，
        // 也就不再受合成面板状态影响（原来「能读到数据但动作不生效」就出在这一环）。
        if (line != null && line.StartsWith("MERGE ", StringComparison.Ordinal) && TrySendMergeOverHttp(line))
            return;

        // 刚连上时 worker 可能还没附加到游戏，AUTH 会返回未就绪。
        // 借界面的状态轮询做节流重试，成功后 _api 非 null 就不再试。
        if (line == "STATE" && _api == null &&
            (DateTime.UtcNow - _lastAuthAttempt).TotalSeconds >= 10)
        {
            _lastAuthAttempt = DateTime.UtcNow;
            Send("AUTH");
        }

        try
        {
            lock (_sync)
            {
                if (_stream == null) return;
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
        }
        catch
        {
            // 发不出去就是断了，Loop 会自己重连
        }
    }

    /// <summary>连接就绪后向 worker 要一次鉴权 header。</summary>
    private void TryRequestAuth()
    {
        Send("AUTH");
    }

    /// <summary>把 worker 的报文分发给界面；鉴权报文在内部消化，界面上不需要知道。</summary>
    private void Dispatch(string line)
    {
        if (TryCaptureAuth(line)) return;
        LineReceived?.Invoke(line);
    }

    /// <summary>
    /// 解析 AUTH 回执并构建 HTTP 客户端。返回 true 表示这条报文已被消费。
    /// 鉴权不完整时保持 _api 为 null，一切照旧走游戏内动作。
    /// </summary>
    private bool TryCaptureAuth(string line)
    {
        if (string.IsNullOrEmpty(line) || line.IndexOf("\"type\":\"auth\"", StringComparison.Ordinal) < 0)
            return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var payload = root.TryGetProperty("payload", out var inner) ? inner : root;

            if (!payload.TryGetProperty("headers", out var headersElement) ||
                headersElement.ValueKind != JsonValueKind.Object)
            {
                var reason = payload.TryGetProperty("reason", out var r) ? r.GetString() : "未知原因";
                AppendLog("鉴权信息未就绪（" + reason + "），沿用游戏内动作");
                return true;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in headersElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    headers[property.Name] = property.Value.GetString();
            }

            var api = GameApiClient.FromGameHeaders(headers);
            if (api == null)
            {
                AppendLog("鉴权 header 不完整，沿用游戏内动作");
                return true;
            }

            var previous = Interlocked.Exchange(ref _api, api);
            previous?.Dispose();
            AppendLog("已取得鉴权，" + api.HeaderNames.Count + " 项 header；合成改由 HTTP 直发");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("解析鉴权信息失败：" + ex.Message);
            return true;
        }
    }

    /// <summary>
    /// 用 HTTP 直发合成。返回 true 表示已接管这条命令（结果稍后经 LineReceived 回执）。
    /// 参数不合法或没有鉴权时返回 false，交回原有路径。
    /// </summary>
    private bool TrySendMergeOverHttp(string line)
    {
        var api = _api;
        if (api == null) return false;

        var ids = line.Substring("MERGE ".Length)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length != 10) return false;

        _ = Task.Run(async () =>
        {
            ApiResult result;
            try { result = await api.MergeAsync(ids).ConfigureAwait(false); }
            catch (Exception ex) { result = new ApiResult(false, 0, "", "请求异常：" + ex.Message); }

            if (!result.Ok)
            {
                // 鉴权失效或网络异常：丢掉缓存并回退游戏内动作，避免整轮直接失败。
                var stale = Interlocked.Exchange(ref _api, null);
                stale?.Dispose();
                AppendLog("HTTP 直发未成功（" + result.Message + "），回退游戏内合成");
                Send(line);
                return;
            }

            var message = JsonSerializer.Serialize(new
            {
                type = "result",
                cmd = "merge",
                ok = true,
                isNew = true,
                id = "",
                msg = "HTTP 直发合成已被服务器接受",
                errorKind = "",
                requestId = "",
                terminal = true
            });
            LineReceived?.Invoke(message);
        });

        return true;
    }

    private void Loop()
    {
        while (_wanted)
        {
            if (_allowConnect != null && !_allowConnect())
            {
                for (var i = 0; i < 3 && _wanted; i++) Thread.Sleep(100);
                continue;
            }
            try
            {
                EnsureWorker();
                var tcp = new TcpClient { NoDelay = true };
                tcp.Connect(_host, _port);
                lock (_sync)
                {
                    _tcp = tcp;
                    _stream = tcp.GetStream();
                }
                _connected = true;
                ConnectionChanged?.Invoke(true);
                TryRequestAuth();

                using var reader = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
                string line;
                while (_wanted && (line = reader.ReadLine()) != null)
                    Dispatch(line);
            }
            catch (Exception ex)
            {
                if ((_worker == null || _worker.HasExited) && !_workerReported)
                {
                    _workerReported = true;
                    AppendLog("Frida worker 不可用：" + ex.Message);
                }
            }
            finally
            {
                CloseSocket();
                if (_connected)
                {
                    _connected = false;
                    ConnectionChanged?.Invoke(false);
                }
            }

            if (!_wanted) break;
            for (var i = 0; i < 20 && _wanted; i++) Thread.Sleep(100);
        }
    }

    private void EnsureWorker()
    {
        if (!File.Exists(_workerPath)) throw new FileNotFoundException("缺少 PCFishFrida.exe", _workerPath);
        if (GameProcess.Find(_targetPid) is not { } game)
            throw new InvalidOperationException(_targetPid > 0 ? "所选 PCFish PID 已退出" : "游戏未运行");
        game.Dispose();

        if (_worker != null && !_worker.HasExited)
        {
            // The worker may be waiting through the game's login grace period
            // before it opens the socket. Keep one worker for this owner.
            if (IsWorkerListening()) return;
            return;
        }

        // 上一次助手异常退出时 worker 可能还在监听；直接复用，不重复启动抢同一个端口。
        // 只看进程名不够：崩溃后的进程句柄可能还没释放，但端口已经关了。
        if (IsWorkerListening())
            return;

        var start = new ProcessStartInfo(_workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(_workerPath) ?? AppContext.BaseDirectory
        };
        start.ArgumentList.Add("wait");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(_port.ToString());
        start.ArgumentList.Add("--owner");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        if (_targetPid > 0)
        {
            start.ArgumentList.Add("--pid");
            start.ArgumentList.Add(_targetPid.ToString());
        }
        _worker = Process.Start(start);
        _workerReported = false;
        _ownsWorker = true;
        AppendLog("已启动 Frida worker（PID " + _worker?.Id + "，端口 " + _port + "）");
        Thread.Sleep(1800);
    }

    private bool IsWorkerListening()
    {
        try
        {
            // 只查监听表，不发探测连接：worker 一次只服务一个客户端，
            // 探测连接会把它当前会话踢掉。
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == _port && IPAddress.IsLoopback(endpoint.Address));
        }
        catch
        {
            return false;
        }
    }


    private void AppendLog(string message)
    {
        try
        {
            File.AppendAllText(_logPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private bool _ownsWorker;

    private void CloseSocket()
    {
        lock (_sync)
        {
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
        }
    }

    public void Dispose()
    {
        _wanted = false;
        // Ask the worker to leave its client loop normally. It then runs
        // FridaHost.ResetConnectionAsync (prepareDetach -> unload -> detach)
        // instead of being killed while a Unity callback is still attached.
        try
        {
            lock (_sync)
            {
                if (_stream != null)
                {
                    var quit = Encoding.UTF8.GetBytes("QUIT\n");
                    _stream.Write(quit, 0, quit.Length);
                    _stream.Flush();
                }
            }
        }
        catch { }
        Interlocked.Exchange(ref _api, null)?.Dispose();
        CloseSocket();
        if (!_ownsWorker)
        {
            try { _worker?.Dispose(); } catch { }
            _worker = null;
            return;
        }
        try
        {
            if (_worker != null && !_worker.HasExited)
            {
                if (!_worker.WaitForExit(6000))
                {
                    // Fallback only if graceful Frida detach hangs.
                    _worker.Kill(true);
                    _worker.WaitForExit(3000);
                }
            }
        }
        catch { }
        try { _worker?.Dispose(); } catch { }
        _worker = null;
    }
}
