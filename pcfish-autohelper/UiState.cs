using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NN.PF.UI.Breed;
using NN.PF.UI.ForecastPopup;

namespace PCFishAutoHelper;

/// <summary>
/// 只读界面诊断。回答两个问题：界面现在长什么样、有没有该收没收的东西留在屏幕上。
///
/// 绝对只读：只调 FindObjectsOfType，只读 name / activeInHierarchy / childCount / sizeDelta。
/// 不调 SetActive、不调 Open/Close/Hide/Refresh、不写字段。
/// </summary>
internal static class UiState
{
    private const float HugeForecastWidth = 400f;
    private const float HugeForecastHeight = 300f;

    /// <summary>繁育结果弹窗的类名，用来判断它有没有被留在屏幕上。</summary>
    private static readonly string[] ResultTypeNames = { "UIFishResult", "UIFishResultFx" };

    internal static string Summarize()
    {
        var sb = new StringBuilder();
        try
        {
            var breedViews = UnityEngine.Object.FindObjectsOfType<UIBreed>(true);
            var forecasts = UnityEngine.Object.FindObjectsOfType<UIForecast>(true);

            var visibleBreed = 0;
            if (breedViews != null)
                foreach (var b in breedViews)
                    if (b != null && b.gameObject != null && b.gameObject.activeInHierarchy) visibleBreed++;

            var visibleForecast = 0; var huge = 0;
            var details = new List<string>();
            if (forecasts != null)
                foreach (var f in forecasts)
                {
                    if (f == null || f.gameObject == null) continue;
                    if (!f.gameObject.activeInHierarchy) continue;
                    visibleForecast++;
                    var w = -1f; var h = -1f;
                    try
                    {
                        if (f.rectTransform != null)
                        {
                            var sd = f.rectTransform.sizeDelta;
                            w = sd.x; h = sd.y;
                        }
                    }
                    catch { }
                    var isHuge = w >= HugeForecastWidth || h >= HugeForecastHeight;
                    if (isHuge) huge++;
                    details.Add($"{(isHuge ? "!!" : "  ")} {Path(f.gameObject.transform)} size={(int)w}x{(int)h}");
                }

            sb.Append("UIBreed=").Append(breedViews == null ? 0 : breedViews.Length);
            sb.Append("(可见").Append(visibleBreed).Append(")");
            sb.Append("  UIForecast=").Append(forecasts == null ? 0 : forecasts.Length);
            sb.Append("(可见").Append(visibleForecast).Append(" 异常放大").Append(huge).Append(")");
            sb.Append("  可见结果弹窗=").Append(CountVisibleResultPopups());
            foreach (var d in details) sb.Append('\n').Append(d);
        }
        catch (Exception ex)
        {
            sb.Append("UiState 异常：").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }
        return sb.ToString();
    }

    /// <summary>数一数屏幕上还留着几个可见的繁育结果弹窗。只读。</summary>
    internal static int CountVisibleResultPopups()
    {
        var n = 0;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            if (all == null) return 0;
            var seen = new HashSet<string>();
            for (var i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                var name = t.name ?? "";
                var hit = false;
                for (var k = 0; k < ResultTypeNames.Length; k++)
                {
                    if (name.IndexOf(ResultTypeNames[k], StringComparison.Ordinal) >= 0) { hit = true; break; }
                }
                if (!hit) continue;
                if (!t.gameObject.activeInHierarchy) continue;
                if (!seen.Add(name + "#" + t.GetInstanceID())) continue;
                n++;
            }
        }
        catch (Exception ex) { Journal.Error("统计结果弹窗失败", ex); }
        return n;
    }

    internal static bool BreedPanelVisible()
    {
        try
        {
            var views = UnityEngine.Object.FindObjectsOfType<UIBreed>(true);
            if (views == null) return false;
            foreach (var v in views)
                if (v != null && v.gameObject != null && v.gameObject.activeInHierarchy) return true;
        }
        catch (Exception ex) { Journal.Error("判断繁育面板可见性失败", ex); }
        return false;
    }

    internal static bool HasHugeForecast()
    {
        try
        {
            var forecasts = UnityEngine.Object.FindObjectsOfType<UIForecast>(true);
            if (forecasts == null) return false;
            foreach (var f in forecasts)
            {
                if (f == null || f.gameObject == null) continue;
                if (!f.gameObject.activeInHierarchy) continue;
                try
                {
                    if (f.rectTransform == null) continue;
                    var sd = f.rectTransform.sizeDelta;
                    if (sd.x >= HugeForecastWidth || sd.y >= HugeForecastHeight) return true;
                }
                catch { }
            }
        }
        catch (Exception ex) { Journal.Error("检查预测浮窗尺寸失败", ex); }
        return false;
    }

    internal static int WindowActiveNodes(out string rootName)
    {
        rootName = "";
        try
        {
            var breedViews = UnityEngine.Object.FindObjectsOfType<UIBreed>(true);
            if (breedViews == null || breedViews.Length == 0) return -1;
            Transform best = null;
            foreach (var b in breedViews)
            {
                if (b == null || b.gameObject == null) continue;
                best = TopWindowAncestor(b.transform);
                if (best != null) break;
            }
            if (best == null) return -1;
            rootName = best.name ?? "";
            var nodes = 0;
            var stack = new Stack<Transform>();
            stack.Push(best);
            var guard = 0;
            while (stack.Count > 0 && guard++ < 30000)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                nodes++;
                try
                {
                    var n = cur.childCount;
                    for (var i = 0; i < n; i++)
                    {
                        var c = cur.GetChild(i);
                        if (c == null) continue;
                        if (!c.gameObject.activeInHierarchy) continue;
                        stack.Push(c);
                    }
                }
                catch { }
            }
            return nodes;
        }
        catch (Exception ex) { Journal.Error("统计窗口节点失败", ex); return -1; }
    }

    private static Transform TopWindowAncestor(Transform from)
    {
        try
        {
            var cur = from;
            Transform best = null;
            var guard = 0;
            while (cur != null && guard++ < 30)
            {
                var n = cur.name ?? "";
                // 游戏会给这些节点改名标记状态（实测形如 Group_MainWindow[OFF]），所以用 StartsWith。
                if (n.StartsWith("Group_", StringComparison.Ordinal)) best = cur;
                cur = cur.parent;
            }
            return best ?? from;
        }
        catch { return null; }
    }

    internal static string DumpWindows()
    {
        var sb = new StringBuilder();
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            if (all == null) return "(没有 Transform)";
            var seen = new HashSet<string>();
            var n = 0;
            for (var i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                var name = t.name ?? "";
                if (name.IndexOf("[ON]", StringComparison.Ordinal) < 0 &&
                    name.IndexOf("[OFF]", StringComparison.Ordinal) < 0) continue;
                if (!seen.Add(name + "#" + t.GetInstanceID())) continue;
                sb.Append(name)
                  .Append(" activeSelf=").Append(t.gameObject.activeSelf)
                  .Append(" inHierarchy=").Append(t.gameObject.activeInHierarchy)
                  .Append(" 子=").Append(t.childCount)
                  .Append(" 路径=").Append(Path(t))
                  .Append('\n');
                if (++n >= 50) { sb.Append("...(截断)\n"); break; }
            }
            if (n == 0) sb.Append("(没有带状态标记的对象)");
        }
        catch (Exception ex)
        {
            sb.Append("DumpWindows 异常：").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }
        return sb.ToString().TrimEnd('\n');
    }

    internal static string DumpDetail()
    {
        var sb = new StringBuilder();
        try
        {
            sb.Append("=== UIForecast（预测浮窗）===").Append('\n');
            var forecasts = UnityEngine.Object.FindObjectsOfType<UIForecast>(true);
            if (forecasts == null || forecasts.Length == 0) sb.Append("  (场景里没有实例)").Append('\n');
            else
                for (var i = 0; i < forecasts.Length; i++)
                {
                    var f = forecasts[i];
                    if (f == null) { sb.Append("  [").Append(i).Append("] null").Append('\n'); continue; }
                    var go = f.gameObject;
                    var size = "?";
                    try
                    {
                        if (f.rectTransform != null)
                        {
                            var sd = f.rectTransform.sizeDelta;
                            size = (int)sd.x + "x" + (int)sd.y;
                        }
                    }
                    catch { }
                    sb.Append("  [").Append(i).Append("] activeSelf=").Append(go.activeSelf)
                      .Append(" inHierarchy=").Append(go.activeInHierarchy)
                      .Append(" size=").Append(size)
                      .Append(" 路径=").Append(Path(go.transform)).Append('\n');
                }

            sb.Append("=== UIBreed（繁育面板）===").Append('\n');
            var breed = UnityEngine.Object.FindObjectsOfType<UIBreed>(true);
            if (breed == null || breed.Length == 0) sb.Append("  (场景里没有实例)").Append('\n');
            else
                for (var i = 0; i < breed.Length; i++)
                {
                    var b = breed[i];
                    if (b == null) { sb.Append("  [").Append(i).Append("] null").Append('\n'); continue; }
                    var go = b.gameObject;
                    var slots = "?";
                    try { slots = b.listViewParent == null ? "-" : b.listViewParent.Length.ToString(); } catch { }
                    var parents = "?";
                    try { parents = b.parentFishList == null ? "-" : b.parentFishList.Length.ToString(); } catch { }
                    sb.Append("  [").Append(i).Append("] activeSelf=").Append(go.activeSelf)
                      .Append(" inHierarchy=").Append(go.activeInHierarchy)
                      .Append(" 槽位=").Append(slots).Append(" 亲鱼槽=").Append(parents)
                      .Append(" 路径=").Append(Path(go.transform)).Append('\n');
                }

            sb.Append("=== 可见的繁育结果弹窗 ===").Append('\n');
            sb.Append("  数量=").Append(CountVisibleResultPopups()).Append('\n');
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            if (all != null)
                for (var i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    var name = t.name ?? "";
                    var hit = false;
                    for (var k = 0; k < ResultTypeNames.Length; k++)
                        if (name.IndexOf(ResultTypeNames[k], StringComparison.Ordinal) >= 0) { hit = true; break; }
                    if (!hit) continue;
                    sb.Append("  ").Append(name)
                      .Append(" activeSelf=").Append(t.gameObject.activeSelf)
                      .Append(" inHierarchy=").Append(t.gameObject.activeInHierarchy)
                      .Append(" 路径=").Append(Path(t)).Append('\n');
                }
        }
        catch (Exception ex)
        {
            sb.Append("DumpDetail 异常：").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Path(Transform t)
    {
        try
        {
            var parts = new List<string>();
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 14)
            {
                parts.Insert(0, cur.name);
                cur = cur.parent;
            }
            return string.Join(" / ", parts);
        }
        catch { return "(?)"; }
    }
}

