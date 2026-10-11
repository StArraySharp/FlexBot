namespace AgentPlugin;

/// <summary>
/// 一轮对话的上下文：调用者身份、群号、本轮助手文本、搜索计数、本轮截图。
///
/// 为什么用 AsyncLocal 而不是普通字段：工具集是在 AgentService 构造时就建好的，
/// 整个进程共用一份；若把这些状态挂在共享字段上，多人并发对话时就会互相覆盖 ——
/// 实测后果是工具层的"主人判定"可能读到**别人**的 uin（非主人有机会通过、主人反被拒）。
/// AsyncLocal 随 await 链自然流入本轮的工具调用，各轮各自隔离。
/// </summary>
sealed class TurnContext
{
    public long CallerUin;
    public string CallerName = "";
    public long GroupId;
    public string AssistantText = "";
    public int SearchCount;
    public string? LastScreenshotPath;
}

static class CurrentTurn
{
    private static readonly AsyncLocal<TurnContext?> _cur = new();

    /// <summary>本轮上下文；不在任何一轮对话内时为 null</summary>
    public static TurnContext? Value
    {
        get => _cur.Value;
        set => _cur.Value = value;
    }

    /// <summary>取当前轮上下文，没有则临时建一个（调用方负责在轮末清空）</summary>
    public static TurnContext Ensure() => _cur.Value ??= new TurnContext();
}
