using System;
using System.Collections.Generic;
using NN.PF.UI.Merge;

namespace PCFishAutoHelper;

/// <summary>一次自动合成的运行状态。收尾只回一次执，避免重复或互相覆盖。</summary>
internal sealed class MergeRun
{
    private readonly Action<bool, string> _finish;
    internal readonly List<FishInfo> Before;
    internal readonly List<string> Steps = new();
    internal UIMerge Ui;
    internal bool Opened;
    internal int Placed;
    /// <summary>等待结果期间见到过游戏自己的错误提示弹窗（例如「发生网络错误」）。</summary>
    internal bool GameErrorSeen;
    private bool _settled;

    internal MergeRun(Action<bool, string> finish, List<FishInfo> before)
    {
        _finish = finish;
        Before = before ?? new List<FishInfo>();
    }

    internal void Log(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Steps.Add(text);
    }

    private int _resultAttempts;
    private bool _resultSettled;

    internal void Finish(bool ok, string message)
    {
        if (_settled) return;
        _settled = true;
        _finish?.Invoke(ok, message);
    }

    /// <summary>等待结果弹窗出现：最多 16 秒，出现后关掉。不阻塞主线程。</summary>
    internal void WaitResult(Action<int> watch)
    {
        if (_resultSettled) return;
        GameBridge.RunOnMainThreadAfter(0.4, () => watch(_resultAttempts++));
    }

    /// <summary>等待超时或出错；不再关闭弹窗，仅记录，让后续步骤继续收尾。</summary>
    internal void ResultThrew(string reason)
    {
        if (_resultSettled) return;
        _resultSettled = true;
        Log(reason);
    }
}

