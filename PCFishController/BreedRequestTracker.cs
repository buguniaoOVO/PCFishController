namespace PCFishController;

/// <summary>保留超时请求身份，最终回包最多处理一次。</summary>
internal sealed class BreedRequestTracker
{
    internal string Id { get; private set; } = "";
    internal bool Active => Id.Length > 0;
    internal bool Waiting { get; private set; }
    internal bool TimedOut { get; private set; }
    internal void Begin(string id) { Id = id; Waiting = true; TimedOut = false; }
    internal void MarkTimeout() { if (Waiting) TimedOut = true; }
    internal bool Accept(string id, bool terminal)
    {
        if (!Waiting || Id != id) return false;
        if (terminal) Waiting = false;
        else TimedOut = true;
        return true;
    }
    internal void Clear() { Id = ""; Waiting = TimedOut = false; }
}
