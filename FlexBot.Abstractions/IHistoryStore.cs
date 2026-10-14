namespace FlexBot.PluginApi;

/// <summary>历史库中的一条消息（时间已格式化为可读字符串）</summary>
public sealed record HistoryMessage(string Time, string Dir, long UserId, string Nick, string Text);

/// <summary>
/// 只读的历史消息库（宿主实现，经 <see cref="IBotContext.History"/> 给插件）。
///
/// 用途：让插件（尤其 Agent）能回答"之前聊过什么"，而不是只看得见内存里的当前会话窗口 ——
/// 宿主重启会清空那个窗口，表现为"刚聊过的事它就不记得了"。
///
/// 注意：scope 由调用方给出，**插件必须用当前回合的真实 scope**（例如 Agent 用 TurnContext 里的
/// 私聊发起人 / 群号），绝不能把模型或用户提供的字符串直接当成 scope 传进来，否则会越权读到别人的私聊。
/// </summary>
public interface IHistoryStore
{
    /// <summary>消息库是否可用（宿主未启用落库时为 false）</summary>
    bool Enabled { get; }

    /// <summary>某会话最近若干条（时间正序）</summary>
    IReadOnlyList<HistoryMessage> Recent(string scope, int limit = 20);

    /// <summary>某会话中早于指定时间的若干条（时间正序），用于补回丢失的上下文</summary>
    IReadOnlyList<HistoryMessage> Before(string scope, long beforeUnix, int limit = 20);

    /// <summary>在某会话内按关键词检索（中文按二元切分，2 字词也能命中）</summary>
    IReadOnlyList<HistoryMessage> Search(string scope, string query, int limit = 10);
}
