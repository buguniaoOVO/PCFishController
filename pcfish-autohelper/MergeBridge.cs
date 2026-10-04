using System;
using System.Collections.Generic;
using NN.PF.Core.Managers;
using NN.PF.UI.Merge;

namespace PCFishAutoHelper;

/// <summary>
/// 后台自动合成。按真实点击顺序驱动游戏自己的合成窗口：
///   打开功能窗口 → 切到合成标签 → 逐条点“+”放鱼 → 点“合成” → 等动画 → 关闭结果弹窗。
/// 只调用游戏公开的 UI 入口，不手工改界面对象。
/// </summary>
internal static class MergeBridge
{
    internal const int RequiredCount = 10;
    private const int MergeTabIndex = 3;   // 功能窗口底边第 4 个图标 = 合成

    internal static UIMerge Find()
    {
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType<UIMerge>(true);
            if (arr == null) return null;
            for (var i = 0; i < arr.Length; i++)
                if (arr[i] != null) return arr[i];
        }
        catch (Exception ex) { Journal.Error("查找合成面板失败", ex); }
        return null;
    }

    private static UIManager Manager()
    {
        try { return UIManager.HasInstance ? UIManager.Instance : null; }
        catch (Exception ex) { Journal.Error("读取 UI 管理器失败", ex); return null; }
    }

    /// <summary>
    /// 打开功能窗口并切到合成标签。
    /// 先 OpenWindow（等价于点向上箭头），再 ChangeTabMenu（等价于点第 4 个图标）。
    /// </summary>
    internal static bool OpenMergeWindow(out string detail)
    {
        detail = "";
        try
        {
            var ui = Manager();
            if (ui == null) { detail = "UI 管理器尚未就绪"; return false; }
            ui.OpenWindow();
            ui.ChangeTabMenu(MergeTabIndex);
            detail = "已打开功能窗口并切到合成标签";
            return true;
        }
        catch (Exception ex)
        {
            Journal.Error("打开合成标签失败", ex);
            detail = "打开合成标签失败：" + ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>关闭功能窗口。等价于点右上角 X。</summary>
    internal static void CloseWindow()
    {
        try { Manager()?.CloseWindow(); }
        catch (Exception ex) { Journal.Error("关闭功能窗口失败", ex); }
    }

    /// <summary>只读探测：报告合成面板状态，不执行任何动作。</summary>
    internal static string Describe()
    {
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType<UIMerge>(true);
            var total = arr?.Length ?? 0;
            var active = 0;
            var mergeable = 0;
            UIMerge sample = null;
            if (arr != null)
            {
                foreach (var ui in arr)
                {
                    if (ui == null) continue;
                    sample ??= ui;
                    if (ui.gameObject != null && ui.gameObject.activeInHierarchy) active++;
                    if (ui.uiFishResult != null && ui.uiFishResultFx != null) mergeable++;
                }
            }
            var max = 0;
            try { max = UIMerge.MAX_MERGE_COUNT; } catch { }
            var filled = 0;
            if (sample?.mergeFishList != null)
                foreach (var id in sample.mergeFishList) if (!string.IsNullOrEmpty(id)) filled++;
            var resultOpen = sample != null && sample.uiFishResult != null &&
                sample.uiFishResult.gameObject != null && sample.uiFishResult.gameObject.activeInHierarchy;
            return $"合成面板 实例={total} 可见={active} 结果对象可用={mergeable} 结果弹窗={resultOpen} MAX={max} 已放={filled}";
        }
        catch (Exception ex)
        {
            Journal.Error("探测合成面板失败", ex);
            return "合成面板探测失败：" + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// 逐步放鱼：先清空，再对每条鱼调用游戏自己的 SetParent（等同于点一次“+”）。
    /// 返回实际填入的条数。
    /// </summary>
    internal static int FillParents(UIMerge ui, IList<string> ids, out string detail)
    {
        detail = "";
        var placed = 0;
        try
        {
            ui.ResetParent();
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (ui.SetParent(id)) placed++;
            }
            detail = $"已放鱼 {placed}/{ids.Count}";
        }
        catch (Exception ex)
        {
            Journal.Error("放鱼到合成窗口失败", ex);
            detail = "放鱼失败：" + ex.GetBaseException().Message;
        }
        return placed;
    }

    /// <summary>点“合成”按钮。等价于点合成窗口里那个紫色合成按钮。</summary>
    internal static bool ClickMerge(UIMerge ui, out string detail)
    {
        detail = "";
        try
        {
            if (ui.mergeFishList == null || ui.mergeFishList.Length < RequiredCount)
            {
                detail = "合成候选不足，游戏不会接受";
                return false;
            }
            ui.Merge();
            detail = "已点击合成";
            return true;
        }
        catch (Exception ex)
        {
            Journal.Error("点击合成失败", ex);
            detail = "点击合成失败：" + ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>
    /// 关闭合成结果弹窗。结果页由游戏自己的收尾入口关闭：
    /// FinishFx 结束动画，Confirm 确认结果，和繁育用的是同一条链。
    /// </summary>
    internal static string CloseResult(UIMerge ui)
    {
        try
        {
            if (ui?.uiFishResult == null || ui.uiFishResultFx == null) return "结果对象不可用";
            var parts = new List<string>();
            if (ui.uiFishResultFx.gameObject.activeSelf)
            {
                ui.uiFishResultFx.FinishFx();
                parts.Add("动画已结束");
            }
            if (ui.uiFishResult.gameObject.activeSelf)
            {
                ui.uiFishResult.Confirm();
                parts.Add("结果已确认");
            }
            return parts.Count == 0 ? "结果弹窗已关闭" : string.Join("；", parts);
        }
        catch (Exception ex)
        {
            Journal.Error("关闭合成结果弹窗失败", ex);
            return "关闭结果弹窗失败：" + ex.GetBaseException().Message;
        }
    }

    /// <summary>合成结果弹窗是否仍显示。</summary>
    internal static bool ResultOpen(UIMerge ui)
    {
        try
        {
            return ui?.uiFishResult != null && ui.uiFishResult.gameObject != null &&
                   ui.uiFishResult.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }
}
