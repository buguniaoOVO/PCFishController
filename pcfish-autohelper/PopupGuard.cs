using System;
using NN.PF.UI.Friend;

namespace PCFishAutoHelper;

/// <summary>
/// 常驻弹窗守卫：检测到「邀请好友领取免费爱心」这类挡路的游戏弹窗就自动点「关闭」。
///
/// 【为什么需要它】
///   这个弹窗由游戏自己在繁育计数器恢复时弹出（UIBreed.CheckFriendInvitePopup，
///   条件是邀请名额还有剩余）。它会盖住功能窗口，用户和助手都点不到下面的内容。
///   助手动作前有输入锁检查，弹窗在的时候什么也做不了，所以必须在它出现后立刻关掉。
///
/// 【只走游戏自己的入口】
///   调用 UIFriendInvitePopup.Close()，和玩家点红色「关闭」按钮走的是同一条路。
///   不做任何手工 SetActive，也不改界面对象。
///
/// 【绝不误伤】
///   只处理这一个类型。合成结果弹窗、普通确认弹窗、以及玩家自己打开的界面
///   都不在范围内，不会被它碰到。
/// </summary>
internal static class PopupGuard
{
    /// <summary>最多每 0.5 秒看一次，够快也够省。</summary>
    private const double CheckIntervalSeconds = 0.5;

    /// <summary>同一条日志最多每 30 秒写一次，避免刷屏。</summary>
    private const double LogIntervalSeconds = 30;

    private static DateTime _lastCheckAt = DateTime.MinValue;
    private static DateTime _lastLogAt = DateTime.MinValue;
    private static int _closedCount;

    /// <summary>已被守卫关掉的弹窗总数，给诊断用。</summary>
    internal static int ClosedCount => _closedCount;

    /// <summary>由主循环每帧驱动。只在主线程调用。</summary>
    internal static void Pump()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCheckAt).TotalSeconds < CheckIntervalSeconds) return;
        _lastCheckAt = now;

        try
        {
            var popup = FindVisible();
            if (popup == null) return;

            popup.Close();
            _closedCount++;

            if ((now - _lastLogAt).TotalSeconds >= LogIntervalSeconds)
            {
                _lastLogAt = now;
                Journal.Write($"已自动关闭好友邀请弹窗（第 {_closedCount} 次）");
            }
        }
        catch (Exception ex)
        {
            Journal.Error("关闭好友邀请弹窗失败", ex);
        }
    }

    private static UIFriendInvitePopup FindVisible()
    {
        var arr = UnityEngine.Object.FindObjectsOfType<UIFriendInvitePopup>(true);
        if (arr == null) return null;
        for (var i = 0; i < arr.Length; i++)
        {
            var p = arr[i];
            if (p != null && p.gameObject != null && p.gameObject.activeInHierarchy) return p;
        }
        return null;
    }

    /// <summary>只读探测：当前有没有可见的好友邀请弹窗，以及守卫已关掉几次。</summary>
    internal static string Describe()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<UIFriendInvitePopup>(true);
            var total = all?.Length ?? 0;
            var visible = 0;
            if (all != null)
                foreach (var p in all)
                    if (p != null && p.gameObject != null && p.gameObject.activeInHierarchy) visible++;
            return $"好友邀请弹窗 实例={total} 可见={visible} 已自动关闭={_closedCount}";
        }
        catch (Exception ex)
        {
            Journal.Error("探测好友邀请弹窗失败", ex);
            return "好友邀请弹窗探测失败：" + ex.GetBaseException().Message;
        }
    }
}
