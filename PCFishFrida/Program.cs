using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Frida;
using Frida.Events;

namespace PCFishFrida;

/// <summary>
/// 无插件连接 worker。按进程名找到 PCFish，用 Frida 附加并在进程内加载代理脚本，
/// 再把脚本的只读状态和动作回执转成本机 TCP 上的行协议，供控制器直接复用。
///
/// 监听地址固定为 127.0.0.1；不写游戏目录，也不启动游戏。
/// </summary>
internal static class Program
{
    internal const string ProtocolVersion = "0.39.0";
    internal const int DefaultPort = 27778;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static async Task<int> Main(string[] args)
    {
        var port = DefaultPort;
        var owner = 0;
        var targetPid = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed)) port = parsed;
            if (args[i] == "--owner" && int.TryParse(args[i + 1], out var parent)) owner = parent;
            if (args[i] == "--pid" && int.TryParse(args[i + 1], out var selectedPid)) targetPid = selectedPid;
        }

        var host = new FridaHost(LoadAgentSource(), targetPid);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        if (owner > 0) WatchParent(shutdown, owner);

        if (args.Length > 0 && args[0] == "wait")
        {
            // 控制器启动时游戏可能还没开：先等到能附加，再开始监听，
            // 这样控制器不会先连上一个读不到状态的 worker。
            Log("等待 PCFish 进程…");
            while (!shutdown.IsCancellationRequested)
            {
                if (await host.EnsureAttachedAsync(shutdown.Token)) break;
                try { await Task.Delay(1500, shutdown.Token); } catch (OperationCanceledException) { break; }
            }
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Log("端口 " + port + " 无法监听：" + ex.Message);
            return 2;
        }

        Log("Frida worker 已启动，监听 127.0.0.1:" + port);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(shutdown.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log("接受连接失败：" + ex.Message); continue; }

                await ServeClientAsync(client, host, shutdown.Token);
            }
        }
        finally
        {
            listener.Stop();
            await host.DisposeAsync();
        }
        return 0;
    }

    /// <summary>
    /// 控制器退出后 worker 也退出。否则崩溃/强退留下的 worker 会一直占着端口，
    /// 下一次启动就只能连到一个没有主人的旧进程。
    /// </summary>
    private static void WatchParent(CancellationTokenSource shutdown, int owner)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(owner);
            var thread = new Thread(() =>
            {
                try { process.WaitForExit(); } catch { }
                try { shutdown.Cancel(); } catch { }
            })
            { IsBackground = true, Name = "parent-watch" };
            thread.Start();
        }
        catch
        {
            // 拿不到控制器的进程句柄时留作人工启动，不影响 worker 正常监听。
        }
    }

    private static string LoadAgentSource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("PCFishFrida.pcfish-agent.js");
        if (stream == null) throw new InvalidOperationException("内置代理脚本缺失");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task ServeClientAsync(TcpClient client, FridaHost host, CancellationToken cancelled)
    {
        client.NoDelay = true;
        using var sessionCancelled = CancellationTokenSource.CreateLinkedTokenSource(cancelled);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var writeLock = new SemaphoreSlim(1, 1);

        // 先确认能附加到游戏再回 hello：游戏没开时直接断开，控制器会显示「未连接」，
        // 不会出现连上 worker 却读不到任何状态的假连接。
        if (!await host.EnsureAttachedAsync(sessionCancelled.Token)) return;

        async Task SendAsync(string line)
        {
            await writeLock.WaitAsync();
            try { await writer.WriteLineAsync(line); }
            catch { }
            finally { writeLock.Release(); }
        }

        host.ResultReceived += SendAsync;
        await SendAsync(JsonSerializer.Serialize(new
        {
            type = "hello",
            ver = ProtocolVersion,
            port = 0,
            cfg = true,
            minInterval = 60,
            breedStatusSupported = true,
            apiStatus = "Frida 内存连接：按 PID 附加游戏进程，不向游戏目录部署插件",
            mode = "frida"
        }, JsonOptions));

        var pump = PumpAsync(host, SendAsync, sessionCancelled.Token);
        try
        {
            while (!sessionCancelled.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(sessionCancelled.Token);
                if (line == null) break;
                if (line.Length == 0 || line.Length > 8192) continue;
                if (line == "PING") { await SendAsync("PONG"); continue; }
                if (line == "QUIT") break;
                await HandleCommandAsync(line, host, SendAsync);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("客户端连接结束：" + ex.Message); }
        finally
        {
            sessionCancelled.Cancel();
            host.ResultReceived -= SendAsync;
            try { await pump; } catch { }
            await host.ResetConnectionAsync();
        }
    }

    private static async Task PumpAsync(FridaHost host, Func<string, Task> send, CancellationToken cancelled)
    {
        while (!cancelled.IsCancellationRequested)
        {
            try
            {
                if (!await host.EnsureAttachedAsync(cancelled))
                {
                    await Task.Delay(1500, cancelled);
                    continue;
                }
                var tick = await host.TickAsync();
                if (tick != null) await send(tick);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("轮询失败：" + ex.Message);
                await host.ResetConnectionAsync();
                try { await Task.Delay(1500, cancelled); } catch (OperationCanceledException) { break; }
            }
            try { await Task.Delay(1000, cancelled); } catch (OperationCanceledException) { break; }
        }
    }

    private static async Task HandleCommandAsync(string line, FridaHost host, Func<string, Task> send)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var command = parts[0].ToUpperInvariant();

        if (command == "ARM")
        {
            var on = parts.Length > 1 && parts[1] == "1";
            await host.SetArmedAsync(on);
            await send("ARMED " + (on ? "1" : "0") + " cfg=1");
            return;
        }

        switch (command)
        {
            case "STATE":
            {
                var state = await host.SnapshotAsync();
                await send(state ?? ErrorJson("state", "游戏数据尚未就绪"));
                return;
            }
            case "AUTH":
            {
                var auth = await host.AuthAsync();
                await send(auth ?? "{\"type\":\"auth\",\"ok\":false,\"reason\":\"空响应\",\"headers\":{}}");
                return;
            }
            case "HUD":
                await send("{\"type\":\"hud\",\"detail\":\"Frida 模式：心位和计数器直接读取游戏内存\"}");
                return;
            case "MERGEPROBE":
                await send("{\"type\":\"mergeprobe\",\"detail\":\"Frida 模式使用隐藏合成对象，不显示功能窗口\"}");
                return;
            case "POPUPPROBE":
                await send("{\"type\":\"popupprobe\",\"detail\":\"Frida 模式不打开游戏功能窗口\"}");
                return;
            case "SERVERAUDIT":
                await send(JsonSerializer.Serialize(new
                {
                    type = "result",
                    cmd = "serveraudit",
                    ok = true,
                    isNew = false,
                    id = "",
                    msg = "{\"countMismatches\":0,\"source\":\"frida\"}",
                    errorKind = "",
                    requestId = "",
                    terminal = true
                }, JsonOptions));
                return;
            case "MERGE":
            {
                var ids = parts.Skip(1).ToArray();
                var accepted = await host.StartMergeAsync(ids);
                if (!accepted.Accepted)
                    await send(ResultJson("merge", false, "", accepted.Reason, "busy"));
                return;
            }
            case "BREED":
            {
                if (parts.Length < 4) { await send(ResultJson("breed", false, "", "繁育参数不足", "game_state")); return; }
                var accepted = await host.StartBreedAsync(parts[1], parts[2], parts[3]);
                if (!accepted.Accepted)
                    await send(ResultJson("breed", false, "", accepted.Reason, "busy", parts[1]));
                return;
            }
            case "UPGRADE":
            {
                var accepted = await host.StartUpgradeAsync();
                if (!accepted.Accepted)
                    await send(ResultJson("upgrade", false, "", accepted.Reason, "busy"));
                return;
            }
            case "TRADEEXPORT":
            {
                var ids = parts.Skip(1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var accepted = await host.StartTradeExportAsync(ids);
                if (!accepted.Accepted)
                    await send(ResultJson("tradeexport", false, "", accepted.Reason, "busy"));
                return;
            }
            case "FRAMELIMIT":
            {
                var value = parts.Length > 1 && int.TryParse(parts[1], out var requested) ? requested : 0;
                var accepted = await host.SetFrameLimitAsync(value);
                await send("FRAMELIMITED " + (accepted ? (value == 10 || value == 30 ? value : 0) : -1));
                return;
            }
            case "BREEDSTATUS":
            {
                var requestId = parts.Length > 1 ? parts[1] : "";
                var status = await host.ActionStatusAsync();
                var message = status.Pending ? "本笔仍在等待游戏回包" : "Frida worker 未找到本笔记录，保持暂停等待核对";
                await send(JsonSerializer.Serialize(new
                {
                    type = "result",
                    cmd = "breed",
                    ok = false,
                    msg = message,
                    errorKind = "uncertain",
                    requestId,
                    terminal = false
                }, JsonOptions));
                return;
            }
        }
    }

    private static string ResultJson(string cmd, bool ok, string id, string message, string errorKind, string requestId = "")
        => JsonSerializer.Serialize(new
        {
            type = "result",
            cmd,
            ok,
            isNew = ok,
            id = id ?? "",
            msg = message ?? "",
            errorKind = errorKind ?? "",
            requestId = requestId ?? "",
            terminal = true
        }, JsonOptions);

    private static string ErrorJson(string cmd, string message)
        => JsonSerializer.Serialize(new { type = cmd, ok = false, msg = message }, JsonOptions);

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static readonly object LogSync = new();
    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "PCFishFrida.log");

    private static void Log(string message)
    {
        var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        Console.Error.WriteLine(line);
        try
        {
            lock (LogSync)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    var lines = File.ReadAllLines(LogPath, Encoding.UTF8);
                    File.WriteAllLines(LogPath, lines.Skip(Math.Max(0, lines.Length - 1000)), Encoding.UTF8);
                }
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }
}

internal sealed record AcceptResult(bool Accepted, string Reason);

internal sealed record ActionStatus(bool Pending, string Kind, string State);

/// <summary>管理 Frida 会话和游戏进程的生命周期。</summary>
internal sealed class FridaHost : IAsyncDisposable
{
    private static readonly TimeSpan AttachGracePeriod = TimeSpan.FromSeconds(60);
    private readonly string _agentSource;
    private readonly int _targetPid;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FridaDeviceManager _manager;
    private FridaDevice _device;
    private FridaSession _session;
    private FridaScript _script;

    internal event Func<string, Task> ResultReceived;

    internal FridaHost(string agentSource, int targetPid)
    {
        _agentSource = agentSource;
        _targetPid = targetPid;
    }

    internal async Task<bool> EnsureAttachedAsync(CancellationToken cancelled)
    {
        if (_script != null && _session != null && !_session.IsDetached()) return true;
        System.Diagnostics.Process process = null;
        try
        {
            if (_targetPid > 0)
            {
                var selected = System.Diagnostics.Process.GetProcessById(_targetPid);
                if (string.Equals(selected.ProcessName, "PCFish", StringComparison.OrdinalIgnoreCase))
                    process = selected;
                else selected.Dispose();
            }
            else process = System.Diagnostics.Process.GetProcessesByName("PCFish").FirstOrDefault();
        }
        catch { }
        if (process == null)
        {
            await ResetConnectionAsync();
            return false;
        }

        // PC FISH may still be performing Steam sign-in during its startup scene.
        // Let the game complete that flow before injecting the read/action agent.
        try
        {
            if (DateTime.Now - process.StartTime < AttachGracePeriod)
            {
                process.Dispose();
                return false;
            }
        }
        catch { /* Continue attaching if the process start time is unavailable. */ }

        var processId = process.Id;
        process.Dispose();
        await ResetConnectionAsync();
        await _gate.WaitAsync(cancelled);
        try
        {
            _manager ??= new FridaDeviceManager();
            _device ??= await _manager.GetDeviceByType(DeviceType.Local, 5000);
            _session = await _device.Attach((uint)processId);
            _script = await _session.CreateScript(_agentSource);
            _script.OnMessage += OnScriptMessage;
            await _script.Load();
            Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 已附加 PCFish PID " + processId);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 附加失败：" + ex.Message);
            await ResetConnectionAsync();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnScriptMessage(object sender, ScriptMessageEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.Json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "send") return;
            if (!root.TryGetProperty("payload", out var payload)) return;
            if (!payload.TryGetProperty("type", out var payloadType) || payloadType.GetString() != "result") return;
            var json = payload.GetRawText();
            var handler = ResultReceived;
            if (handler != null) _ = handler(json);
        }
        catch
        {
            // 脚本消息不是回执时忽略，避免影响连接。
        }
    }

    internal async Task<string> SnapshotAsync()
    {
        var script = _script;
        if (script == null) return null;
        try { return JsonText(await script.Exports.snapshot()); }
        catch (Exception ex) { return "{\"type\":\"state\",\"ok\":false,\"msg\":\"" + JsonEscape(ex.Message) + "\"}"; }
    }

    internal async Task<string> TickAsync()
    {
        var script = _script;
        if (script == null) return null;
        return JsonText(await script.Exports.tick());
    }

    /// <summary>
    /// 取鉴权 header，供控制器在游戏进程外直接发 HTTP。
    /// accessToken/refreshToken 托管字段恒为空，真 JWT 只在 SetRequestHeader 里，
    /// 所以必须由游戏内 agent 旁路采集，这里只负责转发。
    /// </summary>
    internal async Task<string> AuthAsync()
    {
        var script = _script;
        if (script == null)
            return "{\"type\":\"auth\",\"ok\":false,\"reason\":\"游戏尚未附加\",\"headers\":{}}";
        try
        {
            var raw = JsonText(await script.Exports.auth());
            if (string.IsNullOrWhiteSpace(raw))
                return "{\"type\":\"auth\",\"ok\":false,\"reason\":\"空响应\",\"headers\":{}}";
            // 统一补一个 type，和其它消息保持同构，控制器才好分发
            return "{\"type\":\"auth\",\"payload\":" + raw + "}";
        }
        catch (Exception ex)
        {
            return "{\"type\":\"auth\",\"ok\":false,\"reason\":\"" + JsonEscape(ex.Message) + "\",\"headers\":{}}";
        }
    }

    internal async Task SetArmedAsync(bool on)
    {
        var script = _script;
        if (script == null) return;
        try { await script.Exports.setArmed(on); } catch { }
    }

    internal async Task<AcceptResult> StartMergeAsync(string[] ids)
    {
        var script = _script;
        if (script == null) return new AcceptResult(false, "游戏尚未附加");
        try { return ParseAccept(await script.Exports.startMerge(ids)); }
        catch (Exception ex) { return new AcceptResult(false, "启动合成失败：" + ex.Message); }
    }

    internal async Task<AcceptResult> StartBreedAsync(string requestId, string parentA, string parentB)
    {
        var script = _script;
        if (script == null) return new AcceptResult(false, "游戏尚未附加");
        try { return ParseAccept(await script.Exports.startBreed(requestId, parentA, parentB)); }
        catch (Exception ex) { return new AcceptResult(false, "启动繁育失败：" + ex.Message); }
    }

    internal async Task<AcceptResult> StartUpgradeAsync()
    {
        var script = _script;
        if (script == null) return new AcceptResult(false, "游戏尚未附加");
        try { return ParseAccept(await script.Exports.startUpgrade()); }
        catch (Exception ex) { return new AcceptResult(false, "启动升级失败：" + ex.Message); }
    }

    internal async Task<AcceptResult> StartTradeExportAsync(string[] ids)
    {
        var script = _script;
        if (script == null) return new AcceptResult(false, "游戏尚未附加");
        try { return ParseAccept(await script.Exports.startTradeExport(ids)); }
        catch (Exception ex) { return new AcceptResult(false, "启动导出失败：" + ex.Message); }
    }

    internal async Task<bool> SetFrameLimitAsync(int value)
    {
        var script = _script;
        if (script == null) return false;
        try
        {
            var result = await script.Exports.setFrameLimit(value);
            return result is JsonElement element &&
                   element.TryGetProperty("accepted", out var accepted) &&
                   accepted.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    internal async Task<ActionStatus> ActionStatusAsync()
    {
        var script = _script;
        if (script == null) return new ActionStatus(false, "", "");
        try
        {
            var result = await script.Exports.actionStatus();
            if (result is JsonElement element)
            {
                var pending = element.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True;
                var kind = element.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                var state = element.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
                return new ActionStatus(pending, kind, state);
            }
        }
        catch { }
        return new ActionStatus(false, "", "");
    }

    private static AcceptResult ParseAccept(object result)
    {
        if (result is JsonElement element)
        {
            var accepted = element.TryGetProperty("accepted", out var a) && a.ValueKind == JsonValueKind.True;
            var reason = element.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new AcceptResult(accepted, reason);
        }
        return new AcceptResult(false, "游戏脚本未返回结果");
    }

    private static string JsonText(object value)
        => value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value);

    private static string JsonEscape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    /// <summary>
    /// 先卸掉游戏主线程上的 Interceptor 回调，再卸载脚本、断开会话。
    /// 直接 Dispose 会在游戏 tick 里留下被中断的调用，实测会导致 PCFish 以
    /// frida-agent.dll 的堆损坏异常退出，所以这里按顺序收尾。
    /// </summary>
    internal async Task ResetConnectionAsync()
    {
        var script = _script;
        var session = _session;
        _script = null;
        _session = null;
        if (script == null && session == null) return;

        try
        {
            if (script != null)
            {
                try { script.OnMessage -= OnScriptMessage; } catch { }
                try
                {
                    var detach = script.Exports.prepareDetach();
                    await Task.WhenAny((Task)detach, Task.Delay(800));
                }
                catch { }
                try { await Task.WhenAny(script.Unload(), Task.Delay(1000)); } catch { }
                try { script.Dispose(); } catch { }
            }
            if (session != null)
            {
                try { await Task.WhenAny(session.Detach(), Task.Delay(1000)); } catch { }
                try { session.Dispose(); } catch { }
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetConnectionAsync();
        try { _device?.Dispose(); } catch { }
        try { _manager?.Dispose(); } catch { }
        _device = null;
        _manager = null;
        _gate.Dispose();
    }
}
