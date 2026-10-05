using System.Net.Sockets;
using System.Text;

namespace PCFishController;

/// <summary>
/// 连到游戏内桥接的 TCP 客户端。自己管重连，断了不弹错，隔 2 秒再试。
/// </summary>
internal sealed class BridgeClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly object _sync = new();

    private TcpClient _tcp;
    private NetworkStream _stream;
    private Thread _thread;
    private volatile bool _wanted;
    private volatile bool _connected;
    /// <summary>为 null 表示随时可连；返回 false 时跳过本轮连接尝试。</summary>
    private Func<bool> _allowConnect;

    internal bool Connected => _connected;

    internal event Action<bool> ConnectionChanged;
    internal event Action<string> LineReceived;

    internal BridgeClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>
    /// 启动重连线程。allowConnect 返回 false 时表示「暂时不允许连接」——线程照常起来，
    /// 但不建连接。开机自检通过后返回 true，下一次轮询就会立刻连上。
    /// </summary>
    internal void Start(Func<bool> allowConnect = null)
    {
        _allowConnect = allowConnect;
        _wanted = true;
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "bridge-client" };
        _thread.Start();
    }

    internal void Send(string line)
    {
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
                var tcp = new TcpClient { NoDelay = true };
                tcp.Connect(_host, _port);
                lock (_sync)
                {
                    _tcp = tcp;
                    _stream = tcp.GetStream();
                }
                _connected = true;
                ConnectionChanged?.Invoke(true);

                using var reader = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
                string line;
                while (_wanted && (line = reader.ReadLine()) != null)
                {
                    LineReceived?.Invoke(line);
                }
            }
            catch
            {
                // 游戏没开 / 插件没装，都属于正常情况
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
        CloseSocket();
    }
}
