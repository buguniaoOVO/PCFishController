using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NN.PF.Core;
using NN.PF.Core.Managers;
using NN.PF.Core.Network;

namespace PCFishAutoHelper;

internal sealed class FishInfo
{
    internal string Id;
    internal string Species;
    internal string Type;
    internal int Grade;
    internal int Level;
    internal int Stars;
    internal int Variant;
    internal int Growth;
    internal int BreedCount;
    internal int BreedMaxCount;
    internal int ServerBreedCount = -1;
    internal bool IsPlaced;
    internal bool IsLocked;
    internal string BreedNextDatetime;

    internal bool CanBreed => !string.IsNullOrWhiteSpace(Id) && BreedCount > 0 &&
        (ServerBreedCount == -1 || ServerBreedCount > 0) && !IsPlaced && !IsLocked;
}

/// <summary>
/// 游戏数据和网络入口。Unity 主线程执行原生回包、动画结束和结果确认，
/// 网络诊断只记录 HTTP 状态及服务端错误字段。
/// </summary>
internal static class GameBridge
{
    private static readonly ConcurrentQueue<Action> MainThreadWork = new();
    private static readonly ConcurrentQueue<(DateTime Due, Action Work)> DelayedMainThreadWork = new();
    private static Il2CppSystem.Action<bool, bool, string> _pendingCallback;
    private static BreedEvidence _evidence;

    private sealed class BreedEvidence
    {
        internal bool ResponseSeen;
        internal bool TransportFailure;
        internal long HttpStatus;
        internal string HttpResult = "";
        internal string HttpError = "";
        internal string ServerStatus = "";
        internal string GameMessage = "";
        internal string FailureKind => TransportFailure ? "transport" : ResponseSeen ? "server" : "unknown";
        internal string Describe() =>
            $"HTTP={HttpStatus} 传输={HttpResult} 错误={HttpError} 服务端={ServerStatus} 游戏提示={GameMessage}";
    }

    internal readonly struct InteractionState
    {
        internal readonly bool InputBlocked, NetworkBusy, WindowOpen, Processing;
        internal bool Busy => InputBlocked || NetworkBusy || WindowOpen || Processing;
        internal InteractionState(bool input, bool network, bool window, bool processing)
        { InputBlocked = input; NetworkBusy = network; WindowOpen = window; Processing = processing; }
        internal string Describe() =>
            $"输入锁={InputBlocked} 网络请求中={NetworkBusy} 功能窗口={WindowOpen} 界面处理中={Processing}";
    }

    internal static InteractionState ReadInteractionState()
    {
        var input = false; var network = false; var window = false; var processing = false;
        try
        {
            if (SingletonManager<AudioManager>.HasInstance)
                input = SingletonManager<AudioManager>.Instance.isBlockUi;
            if (UIManager.HasInstance)
            {
                var ui = UIManager.Instance;
                network = ui.viewNetwork != null && ui.viewNetwork.activeInHierarchy;
                window = ui.viewWindow != null && ui.viewWindow.activeInHierarchy;
                processing = ui.isProcessing;
            }
        }
        catch (Exception ex) { Journal.Error("读取游戏交互状态失败", ex); input = true; }
        return new InteractionState(input, network, window, processing);
    }

    internal static string ActionBusyReason()
    {
        var state = ReadInteractionState();
        if (state.NetworkBusy) return "游戏网络请求尚未完成，等待回包";
        if (state.InputBlocked) return "游戏输入仍被原生流程锁定，等待收尾";
        if (state.Processing) return "游戏界面正在处理操作，稍后再试";
        if (state.WindowOpen) return "游戏功能窗口正在使用，关闭后再进行自动操作";
        return "";
    }

    private static string Brief(string value)
        => string.IsNullOrEmpty(value) ? "" : value.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(240, value.Length)];

    internal static void ObserveServerResponse(string response)
    {
        if (_evidence == null) return;
        _evidence.ResponseSeen = true;
        try
        {
            using var doc = JsonDocument.Parse(response);
            var parts = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Name is "result" or "code" or "status" or "error" or "errorCode" or "errorMessage" or "message")
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        parts.Add(property.Name + "=" + Brief(property.Value.ToString()));
            _evidence.ServerStatus = string.Join(" ", parts);
            if (_evidence.ServerStatus.Contains("No BreedCount", StringComparison.OrdinalIgnoreCase))
                ServerInventoryCache.Invalidate();
        }
        catch { _evidence.ServerStatus = "回包不是可解析的 JSON"; }
        Journal.Write("繁育服务端回包诊断：" + _evidence.Describe());
    }

    internal static void ObserveTransport(long status, string result, string error)
    {
        if (_evidence == null) return;
        _evidence.HttpStatus = status;
        _evidence.HttpResult = Brief(result);
        _evidence.HttpError = Brief(error);
    }

    internal static void ObserveTransportFailure()
    {
        if (_evidence == null) return;
        _evidence.TransportFailure = true;
        Journal.Write("繁育传输失败诊断：" + _evidence.Describe());
    }

    internal static void ObserveGameMessage(string message)
    {
        if (_evidence != null) _evidence.GameMessage = Brief(message);
    }

    internal static bool HasPendingBreed => _evidence != null;

    internal static void RunOnMainThread(Action action)
    {
        if (action != null) MainThreadWork.Enqueue(action);
    }

    internal static void DrainMainThreadWork()
    {
        for (var i = 0; i < 32 && MainThreadWork.TryDequeue(out var action); i++)
        {
            try { action(); }
            catch (Exception ex) { Journal.Error("主线程回调失败", ex); }
        }
        for (var i = 0; i < 16 && DelayedMainThreadWork.TryPeek(out var delayed) &&
             delayed.Due <= DateTime.UtcNow; i++)
        {
            if (!DelayedMainThreadWork.TryDequeue(out delayed)) break;
            try { delayed.Work(); }
            catch (Exception ex) { Journal.Error("延迟收尾检查失败", ex); }
        }
    }

    internal static bool TryGetDataManager(out GameDataManager manager)
    {
        manager = null;
        try
        {
            if (!SingletonManager<GameDataManager>.HasInstance) return false;
            manager = SingletonManager<GameDataManager>.Instance;
            return manager != null;
        }
        catch (Exception ex) { Journal.Error("读取游戏数据管理器失败", ex); return false; }
    }

    private static bool TryGetNetworkManager(out NetworkManager manager)
    {
        manager = null;
        try
        {
            if (!SingletonManager<NetworkManager>.HasInstance) return false;
            manager = SingletonManager<NetworkManager>.Instance;
            return manager != null;
        }
        catch (Exception ex) { Journal.Error("读取网络管理器失败", ex); return false; }
    }

    internal static List<FishInfo> Snapshot(GameDataManager manager)
    {
        try
        {
            var result = new List<FishInfo>();
            var fish = manager.fishList;
            if (fish == null) return result;
            for (var i = 0; i < fish.Count; i++)
            {
                var model = fish[i];
                if (model == null) continue;
                result.Add(new FishInfo
                {
                    Id = model.Id,
                    Species = model.Fish,
                    Type = model.Type,
                    Grade = model.Grade,
                    Level = model.Level,
                    Stars = model.Grade,
                    Variant = ParseVariant(model.Fish),
                    Growth = model.Growth,
                    BreedCount = model.BreedCount,
                    BreedMaxCount = model.BreedMaxCount,
                    ServerBreedCount = ServerInventoryCache.CountFor(model.Id, model.Level),
                    IsPlaced = model.IsPlaced,
                    IsLocked = model.IsLocked,
                    BreedNextDatetime = model.BreedNextDatetime
                });
            }
            return result;
        }
        catch (Exception ex) { Journal.Error("读取鱼群失败", ex); return null; }
    }

    /// <summary>只读读取游戏自己的图鉴鱼种列表。控制器据此显示已发现鱼种。</summary>
    internal static List<string> SnapshotCollectionTypes(GameDataManager manager)
    {
        try
        {
            var result = new List<string>();
            var source = manager.fishTypeCollection;
            if (source == null) return result;
            for (var i = 0; i < source.Count; i++)
            {
                var value = source[i];
                if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value)) result.Add(value);
            }
            return result;
        }
        catch (Exception ex) { Journal.Error("读取鱼种图鉴失败", ex); return new List<string>(); }
    }

    private static int ParseVariant(string species)
    {
        try
        {
            var parts = species?.Split('_');
            return parts?.Length == 4 && int.TryParse(parts[3], out var value) ? value : 0;
        }
        catch { return 0; }
    }

    internal static int GetBreedCharge(GameDataManager manager)
    {
        try { return manager.breedCount; }
        catch (Exception ex) { Journal.Error("读取心数失败", ex); return -1; }
    }

    internal static int GetTankLevel(GameDataManager manager)
    {
        try { return manager.tankLevel; }
        catch (Exception ex) { Journal.Error("读取鱼缸等级失败", ex); return -1; }
    }

    internal static int GetTankExp(GameDataManager manager)
    {
        try { return manager.tankExp; }
        catch (Exception ex) { Journal.Error("读取鱼缸经验失败", ex); return -1; }
    }

    internal static bool CanLevelUp(GameDataManager manager)
    {
        try { return manager.IsCheckTankLevelUp(); }
        catch (Exception ex) { Journal.Error("检查升级条件失败", ex); return false; }
    }

    internal static int EstimateSecondsToNextHeart(GameDataManager manager)
    {
        try
        {
            if (manager.breedCount >= 5) return 0;
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - manager.breedTimestamp;
            if (elapsed < 0 || elapsed > 86400) return -1;
            return 360 - (int)(elapsed % 360);
        }
        catch { return -1; }
    }

    private static bool OffCooldown(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var when))
            return when <= DateTimeOffset.UtcNow;
        return false;
    }

    /// <summary>只读：找到场景里的繁育面板实例。找不到就返回 null。</summary>
    internal static NN.PF.UI.Breed.UIBreed FindBreedUi()
    {
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType<NN.PF.UI.Breed.UIBreed>(true);
            if (arr == null || arr.Length == 0) return null;
            for (var i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null) return arr[i];
            }
        }
        catch (Exception ex) { Journal.Error("查找繁育面板失败", ex); }
        return null;
    }

    private static NN.PF.UI.Breed.UIBreedTankPopup FindBreedTankPopup()
    {
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType<NN.PF.UI.Breed.UIBreedTankPopup>(true);
            if (arr == null || arr.Length == 0) return null;
            for (var i = 0; i < arr.Length; i++)
                if (arr[i] != null) return arr[i];
        }
        catch (Exception ex) { Journal.Error("查找繁育计数器面板失败", ex); }
        return null;
    }

    /// <summary>
    /// 完成游戏的成功回包链路。FinishFx 释放 AudioManager.isBlockUi、
    /// 重置亲鱼并显示结果；Confirm 触发结果确认。不能用 UIBreed.Close 替代。
    /// </summary>
    private static string FinishBreedUi(NN.PF.UI.Breed.UIBreed ui)
    {
        var parts = new List<string>();
        ui.uiFishResultFx.FinishFx();
        parts.Add("原生动画结束回调已执行");
        ui.uiFishResult.Confirm();
        parts.Add("原生繁育结果已确认");
        try { ui.ShowBreedTimer(); parts.Add("繁育窗口计时器已刷新"); }
        catch (Exception ex) { Journal.Error("刷新繁育窗口计时器失败", ex); parts.Add("窗口计时器刷新失败"); }

        try
        {
            var popup = FindBreedTankPopup();
            if (popup == null) parts.Add("鱼缸计数器面板未找到");
            else { popup.CheckBreedCount(); parts.Add("鱼缸计数器已刷新"); }
        }
        catch (Exception ex) { Journal.Error("刷新鱼缸繁育计数器失败", ex); parts.Add("鱼缸计数器刷新失败"); }

        return string.Join("；", parts);
    }

    /// <summary>
    /// 通过游戏 NetworkManager 请求繁育，并在成功后依次执行原生回包、
    /// FinishFx、Confirm。仅收到回包还不足以完成繁育：输入锁在 FinishFx 中释放。
    /// 网络失败保留真实游戏提示，回包超时期间由桥接阻止下一笔动作。
    /// </summary>
    internal static bool StartBreed(IList<string> ids, Action<bool, bool, string, string, string> onResult,
        out string reason)
    {
        reason = "";
        if (ids == null || ids.Count != 2 || ids[0] == ids[1])
        {
            reason = "需要两条不同的亲鱼";
            return false;
        }
        if (!TryGetDataManager(out var dm) || !TryGetNetworkManager(out var net))
        {
            reason = "游戏数据或网络尚未就绪";
            return false;
        }

        // 拿不到游戏自己的回调就不发请求：这是防止把界面搞坏的关键一步。
        var ui = FindBreedUi();
        if (ui == null || ui.uiFishResultFx == null || ui.uiFishResult == null)
        {
            reason = "游戏繁育面板对象尚未创建；请手动打开一次繁育界面让游戏建好它，再试";
            return false;
        }

        var beforeCharge = GetBreedCharge(dm);
        if (beforeCharge <= 0)
        {
            reason = "心数不足";
            return false;
        }
        var fish = Snapshot(dm);
        if (fish == null)
        {
            reason = "鱼群读取失败";
            return false;
        }
        foreach (var id in ids)
        {
            var candidate = fish.Find(f => f.Id == id);
            if (candidate == null || !candidate.CanBreed || !OffCooldown(candidate.BreedNextDatetime))
            {
                reason = "亲鱼状态或冷却时间已变化";
                return false;
            }
        }

        var pair = new[] { ids[0], ids[1] };
        try
        {
            var evidence = new BreedEvidence();
            _evidence = evidence;
            var managed = new Action<bool, bool, string>((ok, isNew, newId) =>
                RunOnMainThread(() =>
                {
                    var deferred = false;
                    try
                    {
                        if (!ok)
                        {
                            onResult?.Invoke(false, isNew, newId,
                                "繁育未完成：" + evidence.Describe() + "；保留游戏错误提示，停止本轮",
                                evidence.FailureKind);
                            return;
                        }
                        // 回调计算经验时使用亲鱼数组，必须绑定本笔实际双亲。
                        ui.parentFishList = new Il2CppStringArray(pair);
                        ui._Breed_b__20_2(true, isNew, newId);
                        var uiCleanup = FinishBreedUi(ui);
                        if (!TryGetDataManager(out var latest))
                            throw new InvalidOperationException("回包后本地数据管理器不可用");
                        var afterCharge = GetBreedCharge(latest);
                        var afterFish = Snapshot(latest);
                        var child = afterFish?.Find(f => f.Id == newId);
                        ServerInventoryCache.NoteSuccess(pair, newId, child?.BreedCount ?? 0);
                        var changed = afterFish != null && !string.IsNullOrEmpty(newId) &&
                            afterFish.Exists(f => f.Id == newId) &&
                            Array.TrueForAll(pair, id =>
                            {
                                var oldFish = fish.Find(f => f.Id == id);
                                var newFish = afterFish.Find(f => f.Id == id);
                                return oldFish != null && newFish != null &&
                                    (oldFish.Level == 0 || newFish.BreedCount < oldFish.BreedCount);
                            });
                        void CheckSettled(int attempt)
                        {
                            var interaction = ReadInteractionState();
                            // Confirm 会经 PlayUI 暂时锁输入，等待游戏自己的防连点时段结束。
                            if (interaction.InputBlocked && !interaction.NetworkBusy &&
                                !interaction.WindowOpen && attempt < 8)
                            {
                                DelayedMainThreadWork.Enqueue((DateTime.UtcNow.AddMilliseconds(250),
                                    () => CheckSettled(attempt + 1)));
                                return;
                            }
                            try
                            {
                                var resultClosed = !ui.uiFishResultFx.gameObject.activeSelf &&
                                                   !ui.uiFishResult.gameObject.activeSelf;
                                var settled = resultClosed &&
                                    (!interaction.InputBlocked || interaction.NetworkBusy || interaction.WindowOpen);
                                Journal.Write($"后台繁育原生流程对账：计数器 {beforeCharge}→{afterCharge} " +
                                    $"鱼 {fish.Count}→{afterFish?.Count ?? -1} 本笔核实={changed} 收尾完成={settled}；" +
                                    uiCleanup + "；" + interaction.Describe());
                                onResult?.Invoke(changed && settled, isNew, newId,
                                    changed && settled ? "服务端已确认，原生收尾完成；" + uiCleanup
                                        : "回包成功，但本笔鱼群或原生收尾未核实；停止本轮",
                                    changed && settled ? "" : "game_state");
                            }
                            catch (Exception ex)
                            {
                                Journal.Error("繁育延迟对账失败", ex);
                                onResult?.Invoke(false, isNew, newId, "繁育收尾核实失败：" + ex.Message, "game_state");
                            }
                            finally { _pendingCallback = null; _evidence = null; }
                        }
                        deferred = true;
                        DelayedMainThreadWork.Enqueue((DateTime.UtcNow.AddMilliseconds(250), () => CheckSettled(0)));
                    }
                    catch (Exception ex)
                    {
                        Journal.Error("繁育原生收尾或对账失败", ex);
                        onResult?.Invoke(false, isNew, newId, "繁育收尾失败：" + ex.Message + "；停止本轮", "game_state");
                    }
                    finally { if (!deferred) { _pendingCallback = null; _evidence = null; } }
                }));
            _pendingCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool, bool, string>>(managed);
            net.FishBreed(new Il2CppStringArray(pair), _pendingCallback);
            reason = "已发送繁育请求，等待服务端回包";
            return true;
        }
        catch (Exception ex)
        {
            _pendingCallback = null;
            _evidence = null;
            Journal.Error("发送繁育请求失败", ex);
            reason = ex.Message;
            return false;
        }
    }

    internal static bool TryUpgrade(out string detail)
    {
        detail = "";
        if (!TryGetDataManager(out var dm)) { detail = "游戏数据尚未就绪"; return false; }
        if (!CanLevelUp(dm)) { detail = "游戏判定当前不可升级"; return false; }
        var before = GetTankLevel(dm);
        try
        {
            dm.UpgradeTankLevel();
            var after = GetTankLevel(dm);
            detail = $"鱼缸等级 {before}→{after}";
            return after > before;
        }
        catch (Exception ex) { Journal.Error("游戏升级入口失败", ex); detail = ex.Message; return false; }
    }

    // ---------- 游戏自带的「隐藏繁育弹窗」开关 ----------
    //
    // 【为什么需要它】
    //   后台繁育成功后，我们把结果交回游戏自己的收尾入口 —— 这是对的，扣心/加经验都靠它。
    //   但游戏那条流程也会**弹出繁育结果**。玩家当时不在看，结果就留在那里：
    //   等玩家打开功能窗口，才补看一遍「发现新鱼」的动画和结果。
    //
    // 【用游戏自己的办法解决】
    //   游戏本来就带一个设置项「隐藏交配弹窗」（NN.PF.Core.CommonData.isHideBreedingPopup），
    //   专为挂机/自动繁育准备。打开它，游戏自己就不会弹结果窗，扣心和加经验照常。
    //   这比我们手动去关那个弹窗干净得多：只是写一个 bool 静态字段，不碰任何界面对象。

    /// <summary>读游戏自己的「隐藏繁育弹窗」开关。只读。</summary>
    internal static bool GetHideBreedingPopup(out string err)
    {
        err = "";
        try { return NN.PF.Core.CommonData.isHideBreedingPopup; }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    /// <summary>
    /// 设置游戏自己的「隐藏繁育弹窗」开关。
    /// 这是纯静态 bool 字段写入，不触碰任何界面对象，也不改存档以外的配置模型。
    /// </summary>
    internal static bool SetHideBreedingPopup(bool on, out string detail)
    {
        detail = "";
        try
        {
            var before = NN.PF.Core.CommonData.isHideBreedingPopup;
            if (before != on) NN.PF.Core.CommonData.isHideBreedingPopup = on;
            var after = NN.PF.Core.CommonData.isHideBreedingPopup;
            detail = $"隐藏繁育弹窗 {before} → {after}";
            Journal.Write(detail);
            return after == on;
        }
        catch (Exception ex)
        {
            Journal.Error("设置隐藏繁育弹窗失败", ex);
            detail = ex.Message;
            return false;
        }
    }
}
