using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PCFishAutoHelper;

/// <summary>仅本机可连接的后台桥接。Socket 线程只处理文本；游戏对象只在 Pump 中访问。</summary>
internal static class BridgeServer
{
    // 动作总开关。
    //
    // 【为什么现在可以打开】
    //   之前关着是为了做连接核查。现在核查完毕，结论是：
    //     · 只加载桥接（不做动作）时窗口完全正常 —— 注入本身没问题；
    //     · 出问题的形态（图标还在、内容全空、按键无反应）是「绕过游戏自己的
    //       窗口生命周期入口去改 UI 对象」留下的残状态。
    //   所以这里走的路和那些出问题的版本不同：动作只经 NetworkManager.FishBreed 与
    //   GameDataManager 的数据入口，以及游戏公开的繁育收尾、刷新和关闭方法。
    //   WindowGuard 读取收尾后的窗口状态，异常时锁停后续动作。
    //
    //   真正决定"跑不跑"的是三道用户可见的闸：游戏内 cfg「允许外部执行动作」、
    //   控制器的 ARM、以及桥接侧强制的最小动作间隔。这里只是把能力打开。
    private static readonly bool WriteActionsEnabled = true;
    private sealed class Client
    {
        internal TcpClient Tcp;
        internal readonly ConcurrentQueue<string> Out = new();
        internal int Alive = 1;
    }

    private sealed class Request
    {
        internal Client Client;
        internal string Command;
        internal string[] Args;
        internal string RequestId = "";
    }

    private static readonly ConcurrentQueue<Request> Inbox = new();
    private static readonly List<Client> Clients = new();
    private static TcpListener _listener;
    private static volatile bool _started;
    private static volatile bool _startFailed;
    private static volatile bool _allowed;
    private static volatile bool _armed;
    private static int _port = 27777;
    private static int _minInterval = 60;
    private static DateTime _lastActionAt = DateTime.MinValue;
    private static DateTime _lastTickAt = DateTime.MinValue;
    private static Request _pendingBreed;
    private static DateTime _pendingBreedAt = DateTime.MinValue;
    private static bool _pendingBreedTimeoutReported;
    private static int _actions;
    private static readonly Dictionary<string, string> BreedResults = new();
    private static readonly Queue<string> BreedResultIds = new();
    private static DateTime _lastBusyLogAt = DateTime.MinValue;

    // ---------- 窗口内容自检 ----------
    //
    // 用户报的故障：点开功能窗口后「只有图标还在，内容全消失，按键无反应，
    // 但底部鱼缸动画正常」。这是窗口内容被改坏的形态。
    // 动作由游戏自己的代码完成，桥接只调用公开的繁育收尾、刷新和关闭方法。
    //
    // 自检是「最后一道保险」：万一某条路径仍有 UI 副作用，就立刻锁停，
    // 不让它累积成「窗口废掉」的状态。
    private static volatile bool _windowLocked;
    private static string _windowLockReason = "";
    private static DateTime _windowLockAt = DateTime.MinValue;
    private static volatile bool _windowCheckEnabled = true;
    private static int _windowChecksOk;

    /// <summary>动作前的窗口内容快照，动作后拿去比对。</summary>
    private static WindowGuard.Signature _preActionWindow;

    internal static void Configure(bool allowed, int minIntervalSeconds, int port)
    {
        _allowed = allowed;
        _minInterval = Math.Max(60, minIntervalSeconds);
        _port = port;
    }

    internal static void Pump()
    {
        if (!_started && !_startFailed) Start();
        for (var i = 0; i < 16 && Inbox.TryDequeue(out var request); i++)
        {
            try { Handle(request); }
            catch (Exception ex)
            {
                Journal.Error("桥接命令执行失败：" + request.Command, ex);
                var pending = request.Command == "BREED" && GameBridge.HasPendingBreed;
                if (!pending && _pendingBreed == request) _pendingBreed = null;
                Reply(request, request.Command.ToLowerInvariant(), false, false, "",
                    "游戏入口执行失败：" + ex.GetBaseException().Message,
                    pending ? "uncertain" : "compatibility", terminal: !pending);
            }
        }
        if (_pendingBreed != null && !_pendingBreedTimeoutReported &&
            (DateTime.UtcNow - _pendingBreedAt).TotalSeconds > (_pendingBreed.Command == "MERGE" ? 45 : 25))
        {
            _pendingBreedTimeoutReported = true;
            if (_pendingBreed.Command == "MERGE")
            {
                // 合成是本地逐步点击，超时说明步骤没走完：释放锁并如实回执。
                var stuck = _pendingBreed;
                _pendingBreed = null;
                Reply(stuck, "merge", false, false, "",
                    "合成步骤超时，已释放动作锁", "game_state");
            }
            else
            {
                _armed = false;
                // 保留本笔请求占位，迟到回包收尾前不能开始下一笔。
                Reply(_pendingBreed, "breed", false, false, "",
                    "服务端回包较慢，正在等待本笔结果并自动核对", "uncertain", terminal: false);
            }
        }
        if ((DateTime.UtcNow - _lastTickAt).TotalSeconds >= 1)
        {
            _lastTickAt = DateTime.UtcNow;
            Broadcast(BuildTick());
        }
    }

    private static void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _started = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "PCFishSafeBridge.Accept" }.Start();
            Journal.Write($"安全桥接监听 127.0.0.1:{_port}，动作放行={_allowed}");
        }
        catch (Exception ex)
        {
            _startFailed = true;
            Journal.Error("安全桥接启动失败", ex);
        }
    }

    private static void AcceptLoop()
    {
        while (_started)
        {
            TcpClient tcp = null;
            try
            {
                tcp = _listener.AcceptTcpClient();
                tcp.NoDelay = true;
                var client = new Client { Tcp = tcp };
                lock (Clients) Clients.Add(client);
                client.Out.Enqueue(Hello());
                new Thread(() => WriterLoop(client)) { IsBackground = true, Name = "PCFishSafeBridge.Writer" }.Start();
                new Thread(() => ReaderLoop(client)) { IsBackground = true, Name = "PCFishSafeBridge.Reader" }.Start();
            }
            catch (Exception ex)
            {
                Journal.Error("桥接接入失败", ex);
                try { tcp?.Close(); } catch { }
            }
        }
    }

    private static void ReaderLoop(Client client)
    {
        try
        {
            using var reader = new StreamReader(client.Tcp.GetStream(), new UTF8Encoding(false));
            string line;
            while (client.Alive != 0 && (line = reader.ReadLine()) != null)
            {
                if (line.Length > 512) break;
                // 本机有服务发现器定期用 HTTP 探测开放端口；它们不是桥接命令。
                if (line.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase)) break;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var command = parts[0].ToUpperInvariant();
                if (command == "PING") { client.Out.Enqueue("PONG"); continue; }
                if (command == "QUIT") break;
                if (command == "ARM")
                {
                    _armed = parts.Length > 1 && parts[1] == "1";
                    client.Out.Enqueue("ARMED " + (_armed ? "1" : "0") + " cfg=" + (_allowed ? "1" : "0"));
                    continue;
                }
                Inbox.Enqueue(new Request
                {
                    Client = client,
                    Command = command,
                    Args = parts.Length > 1 ? parts[1..] : Array.Empty<string>()
                });
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Journal.Error("桥接读取失败", ex); }
        finally { Drop(client); }
    }

    private static void WriterLoop(Client client)
    {
        try
        {
            var stream = client.Tcp.GetStream();
            while (client.Alive != 0)
            {
                if (!client.Out.TryDequeue(out var line)) { Thread.Sleep(25); continue; }
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch { }
        finally { Drop(client); }
    }

    private static void Drop(Client client)
    {
        if (Interlocked.Exchange(ref client.Alive, 0) == 0) return;
        lock (Clients)
        {
            Clients.Remove(client);
            if (Clients.Count == 0) _armed = false;
        }
        try { client.Tcp.Close(); } catch { }
    }

    private static void Handle(Request request)
    {
        switch (request.Command)
        {
            case "STATE": request.Client.Out.Enqueue(BuildState()); return;
            case "MERGEPROBE": request.Client.Out.Enqueue(BuildText("mergeprobe", MergeBridge.Describe())); return;
            case "MERGE": Merge(request); return;
            case "BREEDSTATUS":
                request.RequestId = request.Args.Length > 0 ? request.Args[0] : "";
                if (BreedResults.TryGetValue(request.RequestId, out var completed))
                    request.Client.Out.Enqueue(completed);
                else if (_pendingBreed != null && _pendingBreed.RequestId == request.RequestId)
                    Reply(request, "breed", false, false, "", "本笔仍在等待游戏回包", "uncertain", terminal: false);
                else
                    Reply(request, "breed", false, false, "", "桥接未找到本笔记录，保持暂停等待核对", "uncertain", terminal: false);
                return;
            case "SERVERAUDIT":
                if (!ServerInventoryCache.Refresh((ok, report) =>
                    Reply(request, "serveraudit", ok, false, "", report),
                    out var auditReason))
                    Reply(request, "serveraudit", false, false, "", auditReason, "busy");
                return;
            case "HUD": request.Client.Out.Enqueue("{\"type\":\"hud\",\"detail\":\"后台模式保持游戏窗口原样\"}"); return;
            case "WINDOW": request.Client.Out.Enqueue(BuildWindowReport()); return;
            case "UISTATE": request.Client.Out.Enqueue(BuildUiState()); return;
            case "UIDETAIL": request.Client.Out.Enqueue(BuildText("uidetail", UiState.DumpDetail())); return;
            case "WINDOWS": request.Client.Out.Enqueue(BuildText("windows", UiState.DumpWindows())); return;
            case "HIDEPOPUP":
                // 读/写游戏自己的「隐藏繁育弹窗」开关（纯静态 bool，不碰界面对象）。
                // 不带参数 = 只看当前值；带 0/1 = 设置。
                if (request.Args.Length == 0)
                {
                    var cur = GameBridge.GetHideBreedingPopup(out var err);
                    Reply(request, "hidepopup", err == "", false, "",
                        err == "" ? $"隐藏繁育弹窗当前={cur}" : "读取失败：" + err);
                }
                else
                {
                    var on = request.Args[0] == "1";
                    var ok = GameBridge.SetHideBreedingPopup(on, out var detail);
                    Reply(request, "hidepopup", ok, false, "", detail);
                }
                return;
            case "WINDOWRESET":
                _windowLocked = false;
                _windowLockReason = "";
                _windowChecksOk = 0;
                Journal.Write("窗口自检的锁停已被手动解除");
                request.Client.Out.Enqueue("{\"type\":\"result\",\"cmd\":\"windowreset\",\"ok\":true,\"isNew\":false,\"id\":\"\",\"msg\":\"锁停已解除\"}");
                return;
            case "BREED": StartBreed(request); return;
            case "UPGRADE": Upgrade(request); return;
            default:
                Reply(request, request.Command.ToLowerInvariant(), false, false, "",
                    "支持的命令：PING / ARM / STATE / HUD / WINDOW / WINDOWS / UISTATE / UIDETAIL / " +
                    "HIDEPOPUP / BREED / UPGRADE / WINDOWRESET");
                return;
        }
    }

    private static bool ActionAllowed(Request request, string command)
    {
        // 编译期硬开关。置 false 可一次性关掉全部写入动作（比如排查期）。
        if (!WriteActionsEnabled)
        {
            Reply(request, command, false, false, "", "桥接已编译为只读，动作被拒绝");
            return false;
        }
        // 窗口自检一旦锁停，先拒绝一切写入动作。
        // 这条排在所有闸门最前面：界面已经不对了，就不该再往游戏里灌命令。
        if (_windowLocked)
        {
            Reply(request, command, false, false, "",
                "窗口内容自检已锁停动作：" + _windowLockReason + "（界面正常后可发 WINDOWRESET 解除）");
            return false;
        }

        // 动手前先看界面是不是已经坏了。
        // 判据是「预测浮窗被异常拉大」—— 实测那个空白大面板就是这个指纹。
        // 界面本来就不对的时候，最该做的是停手，而不是继续往游戏里灌命令。
        if (_windowCheckEnabled && UiState.HasHugeForecast())
        {
            _windowLocked = true;
            _windowLockReason = "预测浮窗已被异常放大，界面本身处于异常状态";
            _windowLockAt = DateTime.UtcNow;
            Journal.Write("!! 动作前检查发现界面已异常（预测浮窗被放大），已锁停动作");
            Reply(request, command, false, false, "", "【已锁停动作】" + _windowLockReason);
            return false;
        }
        if (!_allowed || !_armed)
        {
            Reply(request, command, false, false, "", "游戏配置或 ARM 未放行动作");
            return false;
        }
        var busy = GameBridge.ActionBusyReason();
        if (!string.IsNullOrEmpty(busy))
        {
            Reply(request, command, false, false, "", busy, "busy");
            return false;
        }
        if (command == "breed" && !ServerInventoryCache.Ready)
        {
            Reply(request, command, false, false, "", "请先只读核对服务器库存后再繁育", "inventory");
            return false;
        }
        var elapsed = (DateTime.UtcNow - _lastActionAt).TotalSeconds;
        if (_lastActionAt != DateTime.MinValue && elapsed < _minInterval)
        {
            Reply(request, command, false, false, "", $"动作间隔还需 {_minInterval - elapsed:F0} 秒");
            return false;
        }
        return true;
    }

    private static void StartBreed(Request request)
    {
        // 三个参数时第一个是请求编号；旧客户端的双亲格式仍可读取。
        if (request.Args.Length == 3)
        {
            request.RequestId = request.Args[0];
            request.Args = request.Args[1..];
        }
        if (request.RequestId.Length > 0 && BreedResults.TryGetValue(request.RequestId, out var completed))
        {
            request.Client.Out.Enqueue(completed);
            return;
        }
        if (_pendingBreed != null)
        {
            var same = request.RequestId.Length > 0 && request.RequestId == _pendingBreed.RequestId;
            Reply(request, "breed", false, false, "", "上一笔繁育仍在等待结果",
                same ? "uncertain" : "busy", terminal: !same);
            return;
        }
        if (!ActionAllowed(request, "breed")) return;

        // 动手前拍一张功能窗口的内容快照，回包后复核。
        // 这是「不碰 UI」原则的保险丝：真出副作用就锁停，不让它累积成窗口废掉。
        _preActionWindow = _windowCheckEnabled ? WindowGuard.Snapshot() : default;

        _pendingBreed = request;
        if (!GameBridge.StartBreed(request.Args, (ok, isNew, id, message, errorKind) =>
        {
            if (_pendingBreed != request) return;
            _pendingBreed = null;
            _pendingBreedTimeoutReported = false;
            if (!ok && !string.IsNullOrEmpty(errorKind)) _armed = false;
            var warned = ReviewWindow("breed", _preActionWindow);
            Reply(request, "breed", ok, isNew, id, warned == null ? message : message + "；" + warned, errorKind);
        }, out var reason))
        {
            _pendingBreed = null;
            Reply(request, "breed", false, false, "", reason, "not_sent");
            return;
        }
        _pendingBreedTimeoutReported = false;
        _pendingBreedAt = DateTime.UtcNow;
        _lastActionAt = DateTime.UtcNow;
        _actions++;
        Journal.Write($"后台繁育已发送：{string.Join(",", request.Args)}");
    }

    /// <summary>
    /// 后台自动合成：按真实点击顺序驱动合成窗口。
    ///   打开功能窗口 → 切到合成标签 → 逐条放鱼 → 点合成 → 等动画 → 关闭结果弹窗。
    /// 每一步都在游戏主线程里执行，步骤之间留出间隔，避免一次灌进太多操作。
    /// </summary>
    private static void Merge(Request request)
    {
        if (_pendingBreed != null)
        {
            Reply(request, "merge", false, false, "", "上一笔动作仍在等待结果");
            return;
        }
        if (!ActionAllowed(request, "merge")) return;
        if (request.Args == null || request.Args.Length != MergeBridge.RequiredCount)
        {
            Reply(request, "merge", false, false, "", $"合成需要正好 {MergeBridge.RequiredCount} 条鱼", "not_sent");
            return;
        }
        GameBridge.TryGetDataManager(out var dmBefore);
        var before = dmBefore == null ? null : GameBridge.Snapshot(dmBefore);
        if (before == null)
        {
            Reply(request, "merge", false, false, "", "鱼群读取失败", "not_sent");
            return;
        }
        _preActionWindow = _windowCheckEnabled ? WindowGuard.Snapshot() : default;
        _pendingBreed = request;   // 合成与本笔动作共用同一把“进行中”锁
        _pendingBreedAt = DateTime.UtcNow;
        _pendingBreedTimeoutReported = false;
        _lastActionAt = DateTime.UtcNow;
        _actions++;
        Journal.Write($"后台合成开始：{string.Join(",", request.Args)}");

        MergeRun state = null;
        state = new MergeRun((ok, message) =>
        {
            _pendingBreed = null;
            _pendingBreedTimeoutReported = false;
            // 游戏自己弹了网络错误提示时，这是服务端拒绝或丢包，不是界面被改坏。
            // 报成 busy 让控制器稍后自动重试，而不是要求用户手动 ARM。
            var kind = ok ? "" : (state != null && state.GameErrorSeen ? "busy" : "game_state");
            Reply(request, "merge", ok, false, "", message, kind);
        }, before);

        // 串行执行：每一步完成后才进入下一步，保证关闭弹窗一定在关闭窗口之前。
        // 打开窗口 -> 清空 -> 逐条点“+”放鱼 -> 点合成 -> 等动画 -> 关结果弹窗 -> 关窗口。
        Step(0.4, OpenWindow);

        void Step(double delay, Action next)
            => GameBridge.RunOnMainThreadAfter(delay, SafeStep(next));

        void OpenWindow()
        {
            state.Opened = MergeBridge.OpenMergeWindow(out var openDetail);
            state.Log(openDetail);
            if (!state.Opened) { state.Finish(false, openDetail); return; }
            Step(0.8, ResetSlots);
        }

        void ResetSlots()
        {
            var found = MergeBridge.Find();
            if (found == null) { state.Finish(false, "找不到合成面板"); return; }
            state.Ui = found;
            MergeBridge.ClearParents(found, out var clearDetail);
            state.Log(clearDetail);
            Step(0.4, () => FillOne(0));
        }

        // 每 150 毫秒放一条鱼，模拟人手逐次点“+”。放完 10 条就点合成。
        void FillOne(int index)
        {
            if (state.Ui == null) { state.Finish(false, "合成面板丢失"); return; }
            if (index >= request.Args.Length)
            {
                state.Placed = MergeBridge.FilledCount(state.Ui);
                state.Log($"已放鱼 {state.Placed}/{request.Args.Length}");
                if (state.Placed != MergeBridge.RequiredCount) { state.Finish(false, "放鱼数量不足"); return; }
                Step(0.5, ClickMerge);
                return;
            }
            var placed = MergeBridge.AddParent(state.Ui, request.Args[index], out var addDetail);
            if (!placed)
            {
                state.Log(addDetail.Length > 0 ? addDetail : $"第 {index + 1} 条未被接受");
                state.Placed = MergeBridge.FilledCount(state.Ui);
                state.Log($"已放鱼 {state.Placed}/{request.Args.Length}");
                state.Finish(false, "有鱼未被合成窗口接受");
                return;
            }
            Step(0.15, () => FillOne(index + 1));
        }

        void ClickMerge()
        {
            if (state.Ui == null) { state.Finish(false, "合成面板丢失"); return; }
            var clicked = MergeBridge.ClickMerge(state.Ui, out var clickDetail);
            state.Log(clickDetail);
            if (!clicked) { state.Finish(false, clickDetail); return; }
            Step(0.8, () => WatchResult(0));
        }

        // 每 350~400 毫秒看一次：结果弹窗一出现就关掉；弹窗还没出现就先等动画播完。
        void WatchResult(int attempt)
        {
            if (state.Ui == null) { state.Finish(false, "合成面板丢失"); return; }
            try
            {
                // 游戏如果弹了通用提示（例如「发生网络错误」），先点掉它，它挡住了合成结果。
                if (!MergeBridge.ResultOpen(state.Ui))
                {
                    var dismiss = MergeBridge.CloseConfirm();
                    if (dismiss != null)
                    {
                        state.Log(dismiss);
                        Journal.Write("后台合成：" + dismiss);
                        state.GameErrorSeen = true;
                    }
                }
                // 结果弹窗优先：只要它显示着就关掉，不等动画标志。
                // 动画标志偶尔会滞后，先看弹窗才能保证它一定被关掉。
                if (MergeBridge.ResultOpen(state.Ui))
                {
                    var closeDetail = MergeBridge.CloseResult(state.Ui);
                    state.Log(closeDetail);
                    Journal.Write("后台合成结果弹窗：" + closeDetail);
                    if (MergeBridge.ResultOpen(state.Ui))
                    {
                        if (attempt >= 40) { state.Log("结果弹窗反复未关闭，交给收尾再关一次"); Step(0.2, CloseUp); return; }
                        state.Log("结果弹窗仍在，稍后再关一次");
                        Step(0.5, () => WatchResult(attempt + 1));
                        return;
                    }
                    Step(0.6, CloseUp);
                    return;
                }
                // 弹窗还没出来：说明合成动画还在播，等它结束（结束时游戏自己的回调会放出弹窗）。
                if (attempt >= 40)
                {
                    if (state.GameErrorSeen)
                        state.Log("游戏提示网络错误，本笔合成未生效");
                    else
                        state.Log(MergeBridge.FxPlaying(state.Ui) ? "等待结果弹窗超时（动画仍在）" : "等待结果弹窗超时");
                    Step(0.2, CloseUp);
                    return;
                }
                Step(0.35, () => WatchResult(attempt + 1));
            }
            catch (Exception ex)
            {
                Journal.Error("等待合成结果弹窗失败", ex);
                state.Log("等待结果弹窗出错：" + ex.GetBaseException().Message);
                Step(0.2, CloseUp);
            }
        }

        void CloseUp()
        {
            // 收尾保险：关窗口之前再确认一次结果弹窗已经关掉，免得它留在屏幕上。
            try
            {
                if (state.Ui != null && MergeBridge.ResultOpen(state.Ui))
                {
                    var finalClose = MergeBridge.CloseResult(state.Ui);
                    state.Log("收尾再关一次结果弹窗：" + finalClose);
                    Journal.Write("后台合成收尾补关结果弹窗：" + finalClose);
                }
            }
            catch (Exception ex) { Journal.Error("收尾补关结果弹窗失败", ex); }
            try { MergeBridge.CloseWindow(); state.Log("已关闭功能窗口"); }
            catch (Exception ex) { Journal.Error("关闭功能窗口失败", ex); }
            Step(0.4, Verify);
        }

        void Verify()
        {
            var changed = false;
            var detail = "鱼群变化未核实";
            try
            {
                GameBridge.TryGetDataManager(out var dmAfter);
                var after = dmAfter == null ? null : GameBridge.Snapshot(dmAfter);
                var beforeIds = new HashSet<string>(before.ConvertAll(f => f.Id));
                var consumed = after != null && Array.TrueForAll(request.Args, id => !after.Exists(f => f.Id == id));
                var gained = after != null && after.Exists(f => !beforeIds.Contains(f.Id));
                changed = consumed || gained;
                detail = $"鱼 {before.Count}→{after?.Count ?? -1}";
                Journal.Write($"后台合成对账：提交 {request.Args.Length} 条 本笔核实={changed} {detail}");
            }
            catch (Exception ex) { Journal.Error("合成对账失败", ex); }
            var warned = ReviewWindow("merge", _preActionWindow);
            state.Finish(changed,
                (changed ? "合成已确认" : "合成回包后鱼群未核实") + "；" + detail +
                "；" + string.Join("；", state.Steps) + (warned == null ? "" : "；" + warned));
        }
    }

    /// <summary>包装单个合成步骤：出错时用回执收尾，避免把上一笔锁留在原地。</summary>
    private static Action SafeStep(Action step)
        => () => { try { step(); } catch (Exception ex) { Journal.Error("合成步骤失败", ex); } };


    private static void Upgrade(Request request)
    {
        if (_pendingBreed != null)
        {
            Reply(request, "upgrade", false, false, "", "繁育尚未完成");
            return;
        }
        if (!ActionAllowed(request, "upgrade")) return;
        _preActionWindow = _windowCheckEnabled ? WindowGuard.Snapshot() : default;
        var ok = GameBridge.TryUpgrade(out var detail);
        if (ok) { _lastActionAt = DateTime.UtcNow; _actions++; }
        var warned = ReviewWindow("upgrade", _preActionWindow);
        Reply(request, "upgrade", ok, false, "", warned == null ? detail : detail + "；" + warned);
    }

    /// <summary>
    /// 动作后的窗口内容复核。异常时锁停所有动作。
    /// 返回 null 表示正常；返回非 null 是给用户的说明文字。
    /// </summary>
    private static string ReviewWindow(string command, WindowGuard.Signature before)
    {
        if (!_windowCheckEnabled || !before.Valid) return null;

        var after = WindowGuard.Snapshot();
        var problem = WindowGuard.Compare(before, after);
        if (problem == null)
        {
            _windowChecksOk++;
            return null;
        }

        _windowLocked = true;
        _windowLockReason = problem;
        _windowLockAt = DateTime.UtcNow;
        Journal.Write($"!! 窗口内容自检在 {command} 之后发现异常并锁停动作：{problem}");
        Journal.Write($"   快照：动作前 [{before.Describe()}] → 动作后 [{after.Describe()}]");
        return "【已锁停动作】" + problem;
    }

    private static string Hello()
        => "{\"type\":\"hello\",\"ver\":" + Q(Plugin.PluginVersion)
           + ",\"port\":" + _port + ",\"cfg\":" + B(_allowed)
             + ",\"minInterval\":" + _minInterval + ",\"breedStatusSupported\":true,\"apiStatus\":" + Q(NativeBreedingApi.Detail) + "}";

    private static string BuildTick()
    {
        if (!GameBridge.TryGetDataManager(out var dm))
            return "{\"type\":\"tick\",\"game\":false,\"armed\":" + B(_armed)
                   + ",\"cfg\":" + B(_allowed) + "}";
        var fish = GameBridge.Snapshot(dm);
        if (fish == null) return "{\"type\":\"tick\",\"game\":false,\"armed\":" + B(_armed)
            + ",\"cfg\":" + B(_allowed) + "}";
        var breedable = fish.FindAll(f => f.CanBreed).Count;
        var exhausted = fish.FindAll(f => f.BreedCount <= 0).Count;
        var interaction = GameBridge.ReadInteractionState();
        return "{\"type\":\"tick\",\"game\":true,\"armed\":" + B(_armed)
               + ",\"cfg\":" + B(_allowed) + ",\"actions\":" + _actions
               + ",\"total\":" + fish.Count + ",\"breedable\":" + breedable
               + ",\"exhausted\":" + exhausted + ",\"maxMerge\":0"
               + ",\"charge\":" + GameBridge.GetBreedCharge(dm)
               + ",\"tankLevel\":" + GameBridge.GetTankLevel(dm)
               + ",\"tankExp\":" + GameBridge.GetTankExp(dm)
               + ",\"canLevelUp\":" + B(GameBridge.CanLevelUp(dm))
               + ",\"secToHeart\":" + GameBridge.EstimateSecondsToNextHeart(dm)
               + ",\"gameBusy\":" + B(interaction.Busy)
               + ",\"inputBlocked\":" + B(interaction.InputBlocked)
               + ",\"networkBusy\":" + B(interaction.NetworkBusy)
               + ",\"functionWindowOpen\":" + B(interaction.WindowOpen)
                + ",\"serverInventoryReady\":" + B(ServerInventoryCache.Ready)
                + ",\"breedPending\":" + B(_pendingBreed != null)
                + ",\"pendingRequestId\":" + Q(_pendingBreed?.RequestId ?? "")
               + ",\"actionWaitSeconds\":" + Math.Max(0,
                   (int)Math.Ceiling(_minInterval - (DateTime.UtcNow - _lastActionAt).TotalSeconds))
               + ",\"windowLocked\":" + B(_windowLocked)
               + ",\"windowLockReason\":" + Q(_windowLockReason)
               + ",\"windowChecksOk\":" + _windowChecksOk
               + ",\"summary\":" + Q($"等级={GameBridge.GetTankLevel(dm)} 经验={GameBridge.GetTankExp(dm)}")
               + "}";
    }

    /// <summary>只读诊断：把功能窗口的内容快照报给控制器，用于人工核对界面是否正常。</summary>
    private static string BuildWindowReport()
    {
        var sig = WindowGuard.Snapshot();
        var sb = new StringBuilder(220);
        sb.Append("{\"type\":\"window\",\"valid\":").Append(B(sig.Valid))
          .Append(",\"open\":").Append(B(sig.WindowOpen))
          .Append(",\"nodes\":").Append(sig.ActiveNodes)
          .Append(",\"root\":").Append(Q(sig.RootName))
          .Append(",\"windowLocked\":").Append(B(_windowLocked))
          .Append(",\"windowLockReason\":").Append(Q(_windowLockReason))
          .Append(",\"checksOk\":").Append(_windowChecksOk)
          .Append(",\"detail\":").Append(Q(
              sig.Valid
                  ? $"{sig.RootName} 开着={sig.WindowOpen}，窗口内活跃节点 {sig.ActiveNodes} 个" +
                    (_windowLocked ? $"，动作已锁停：{_windowLockReason}" : "，动作未被锁停")
                  : "未找到功能窗口容器（可能游戏还在读档，或功能窗口从未打开过）"))
          .Append('}');
        return sb.ToString();
    }

    /// <summary>只读诊断：界面对象状态。用来判断「窗口空白」到底是谁、是否在动作后出现。</summary>
    private static string BuildUiState()
        => "{\"type\":\"uistate\",\"locked\":" + B(_windowLocked)
           + ",\"checksOk\":" + _windowChecksOk
           + ",\"detail\":" + Q(GameBridge.ReadInteractionState().Describe() + "；" + UiState.Summarize()) + "}";

    /// <summary>只读诊断：把一段多行文本塞进 JSON 回执。</summary>
    private static string BuildText(string type, string body)
        => "{\"type\":" + Q(type) + ",\"detail\":" + Q(body) + "}";

    private static string BuildState()
    {
        if (!GameBridge.TryGetDataManager(out var dm))
            return "{\"type\":\"state\",\"ok\":false,\"msg\":\"游戏数据尚未就绪\"}";
        var fish = GameBridge.Snapshot(dm);
        if (fish == null) return "{\"type\":\"state\",\"ok\":false,\"msg\":\"鱼群读取失败\"}";
        var collection = GameBridge.SnapshotCollectionTypes(dm);
        var sb = new StringBuilder(fish.Count * 160 + 150);
        sb.Append("{\"type\":\"state\",\"ok\":true,\"count\":").Append(fish.Count)
          .Append(",\"charge\":").Append(GameBridge.GetBreedCharge(dm))
          .Append(",\"tankLevel\":").Append(GameBridge.GetTankLevel(dm))
          .Append(",\"tankExp\":").Append(GameBridge.GetTankExp(dm))
          .Append(",\"canLevelUp\":").Append(B(GameBridge.CanLevelUp(dm)))
          .Append(",\"collectionTypes\":[");
        for (var i = 0; i < collection.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Q(collection[i]));
        }
        sb.Append("],\"fish\":[");
        for (var i = 0; i < fish.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var f = fish[i];
            sb.Append("{\"id\":").Append(Q(f.Id))
              .Append(",\"sp\":").Append(Q(f.Species))
              .Append(",\"ty\":").Append(Q(f.Type))
              .Append(",\"tier\":").Append(f.Level)
              .Append(",\"stars\":").Append(f.Stars)
              .Append(",\"variant\":").Append(f.Variant)
              .Append(",\"g\":").Append(f.Grade)
              .Append(",\"lv\":").Append(f.Level)
              .Append(",\"gr\":").Append(f.Growth)
              .Append(",\"bc\":").Append(f.BreedCount)
              .Append(",\"bm\":").Append(f.BreedMaxCount)
              .Append(",\"serverBc\":").Append(f.ServerBreedCount)
              .Append(",\"pl\":").Append(B(f.IsPlaced))
              .Append(",\"lk\":").Append(B(f.IsLocked))
              .Append(",\"nx\":").Append(Q(f.BreedNextDatetime)).Append('}');
        }
        return sb.Append("]}").ToString();
    }

    private static void Reply(Request request, string command, bool ok, bool isNew, string id, string message,
        string errorKind = "", bool terminal = true)
    {
        // busy 是「现在还不能做，等会儿再来」的常规回复：游戏刚启动、登录还没走完时，
        // 控制器每几秒重试一次。每次都写会把日志刷成几百行，把真正有用的信息淹掉。
        // 只对 busy 节流，其它结果照常记录。
        if (errorKind == "busy")
        {
            if ((DateTime.UtcNow - _lastBusyLogAt).TotalSeconds >= 30)
            {
                _lastBusyLogAt = DateTime.UtcNow;
                Journal.Write($"{command} 结果：ok={ok} {message}（同类提示 30 秒内只记一条）");
            }
        }
        else
        {
            Journal.Write($"{command} 结果：ok={ok} {message}");
        }
        var json = "{\"type\":\"result\",\"cmd\":" + Q(command)
            + ",\"ok\":" + B(ok) + ",\"isNew\":" + B(isNew)
              + ",\"id\":" + Q(id) + ",\"msg\":" + Q(message) + ",\"errorKind\":" + Q(errorKind)
              + ",\"requestId\":" + Q(request.RequestId) + ",\"terminal\":" + B(terminal) + "}";
        if (command == "breed" && terminal && request.RequestId.Length > 0)
        {
            if (!BreedResults.ContainsKey(request.RequestId)) BreedResultIds.Enqueue(request.RequestId);
            BreedResults[request.RequestId] = json;
            while (BreedResultIds.Count > 16) BreedResults.Remove(BreedResultIds.Dequeue());
        }
        request.Client?.Out.Enqueue(json);
    }

    private static void Broadcast(string message)
    {
        lock (Clients)
            foreach (var client in Clients)
                if (client.Alive != 0) client.Out.Enqueue(message);
    }

    private static string B(bool value) => value ? "true" : "false";

    private static string Q(string value)
    {
        if (value == null) return "\"\"";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
 }
