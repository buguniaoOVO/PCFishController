using System;

namespace PCFishAutoHelper;

/// <summary>
/// 动作前后对比「功能窗口内容」的保险丝。
///
/// 【为什么需要它】
///   用户报的故障是：功能窗口里只有图标、内容全消失，按键无反应，但鱼缸动画照跑。
///   要判断这是不是助手造成的，就得能读到界面对象的真实状态。
///
/// 【上一版错在哪】
///   上一版按名字找「Group_MainWindow」，实测场景里根本没有这个名字 —— 快照永远
///   返回「不可用」，等于没有保护。这一版改为**按游戏自己的类型定位**：
///   先用 UIBreed 实例找到繁育面板，再往上找最高一层 Group_ 祖先作为窗口容器。
///   不再猜对象名。
///
/// 【绝对只读】
///   只调 FindObjectsOfType / 读 name 与 activeInHierarchy / 读 childCount，
///   不调 SetActive、不调 Close / Open / Refresh、不写任何字段。
/// </summary>
internal static class WindowGuard
{
    /// <summary>「内容塌陷」判据：动作后活跃节点数不足动作前的这个比例，就算异常。</summary>
    private const double CollapseRatio = 0.6;

    internal readonly struct Signature
    {
        /// <summary>是否成功取到快照。取不到就不做判断，避免误锁。</summary>
        internal readonly bool Valid;
        /// <summary>动作时繁育面板是否可见。</summary>
        internal readonly bool WindowOpen;
        /// <summary>窗口容器内的活跃节点数。</summary>
        internal readonly int ActiveNodes;
        /// <summary>容器名，仅用于日志。</summary>
        internal readonly string RootName;

        internal Signature(bool valid, bool windowOpen, int activeNodes, string rootName)
        {
            Valid = valid;
            WindowOpen = windowOpen;
            ActiveNodes = activeNodes;
            RootName = rootName;
        }

        internal string Describe()
            => Valid ? $"{RootName} 面板可见={WindowOpen} 活跃节点={ActiveNodes}" : "(快照不可用)";
    }

    /// <summary>取一次窗口内容快照。只读。</summary>
    internal static Signature Snapshot()
    {
        try
        {
            var open = UiState.BreedPanelVisible();
            var nodes = UiState.WindowActiveNodes(out var root);
            if (nodes < 0) return new Signature(false, open, 0, root ?? "");
            return new Signature(true, open, nodes, root ?? "");
        }
        catch (Exception ex)
        {
            Journal.Error("窗口内容快照失败", ex);
            return new Signature(false, false, 0, "");
        }
    }

    /// <summary>
    /// 动作后的复核。返回 null 表示正常，返回非 null 是异常描述（应当锁停）。
    /// 判据刻意收窄：只在「动手前面板可见 + 动手后仍可见 + 节点数塌陷」时判异常，
    /// 免得把玩家自己开关窗口误判成故障。
    /// </summary>
    internal static string Compare(Signature before, Signature after)
    {
        if (!before.Valid || !after.Valid) return null;
        if (!before.WindowOpen) return null;    // 动手前面板就不可见，没有判据
        if (!after.WindowOpen) return null;     // 面板被关掉了（玩家自己关的也算），不算异常
        if (before.ActiveNodes <= 1) return null;

        var ratio = after.ActiveNodes / (double)before.ActiveNodes;
        if (ratio >= CollapseRatio) return null;

        return $"动作前 {before.ActiveNodes} 个活跃节点，动作后只剩 {after.ActiveNodes} 个" +
               $"（{ratio:P0}），功能窗口内容疑似被破坏";
    }
}

