using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NN.PF.UI.Merge;

namespace PCFishAutoHelper;

/// <summary>
/// 后台自动合成。走游戏自己的 UIMerge.Merge()（就是合成窗口的合成按钮），
/// 只设置候选列表和合成类型，不修改任何界面对象。
/// </summary>
internal static class MergeBridge
{
    internal const int RequiredCount = 10;

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

    /// <summary>只读探测：报告合成面板是否存在、能否安全提交。不执行任何动作。</summary>
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
            var fieldCount = 0;
            var current = 0;
            if (sample?.mergeFishList != null)
            {
                fieldCount = sample.mergeFishList.Length;
                foreach (var id in sample.mergeFishList) if (!string.IsNullOrEmpty(id)) current++;
            }
            return $"合成面板 实例={total} 可见={active} 结果对象可用={mergeable} MAX={max} 当前候选={current}/{fieldCount}";
        }
        catch (Exception ex)
        {
            Journal.Error("探测合成面板失败", ex);
            return "合成面板探测失败：" + ex.GetBaseException().Message;
        }
    }

    internal static bool TryMerge(IList<string> ids, out string reason)
    {
        reason = "";
        if (ids == null || ids.Count != RequiredCount)
        {
            reason = $"合成需要正好 {RequiredCount} 条鱼";
            return false;
        }
        var ui = Find();
        if (ui == null)
        {
            reason = "合成面板尚未创建；请手动打开一次合成界面让游戏建好它";
            return false;
        }
        if (!GameBridge.TryGetDataManager(out var dm))
        {
            reason = "游戏数据尚未就绪";
            return false;
        }
        var fish = GameBridge.Snapshot(dm);
        if (fish == null)
        {
            reason = "鱼群读取失败";
            return false;
        }
        foreach (var id in ids)
        {
            var candidate = fish.Find(f => f.Id == id);
            if (candidate == null) { reason = "候选中已不存在 " + id; return false; }
            if (candidate.IsPlaced || candidate.IsLocked) { reason = "候选已展示或已锁定"; return false; }
        }
        try
        {
            ui.mergeType = MergeType.Standard;
            var array = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++) array[i] = ids[i];
            ui.mergeFishList = new Il2CppStringArray(array);
            ui.Merge();
            Journal.Write($"后台合成已提交：{string.Join(",", ids)}");
            return true;
        }
        catch (Exception ex)
        {
            Journal.Error("提交合成失败", ex);
            reason = "合成入口执行失败：" + ex.GetBaseException().Message;
            return false;
        }
    }
}
