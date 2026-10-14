using FlexBot.PluginApi;

namespace FlexBot;

/// <summary>
/// <see cref="IHistoryStore"/> 的宿主实现：把 MessageStore 的行映射成插件侧的消息记录。
/// scope 原样透传（越权风险由调用方保证：插件必须用当前回合的真实 scope）。
/// </summary>
sealed class HistoryStoreAccess : IHistoryStore
{
    public bool Enabled => MessageStore.Current is not null;

    public IReadOnlyList<HistoryMessage> Recent(string scope, int limit = 20)
        => Map(MessageStore.Current?.Recent(scope, limit));

    public IReadOnlyList<HistoryMessage> Before(string scope, long beforeUnix, int limit = 20)
        => Map(MessageStore.Current?.Before(scope, beforeUnix, limit));

    public IReadOnlyList<HistoryMessage> Search(string scope, string query, int limit = 10)
        => Map(MessageStore.Current?.Search(scope, query, limit));

    public IReadOnlyList<HistoryMessage> Range(string scope, long fromUnix, long toUnix, int limit = 2000)
        => Map(MessageStore.Current?.Range(scope, fromUnix, toUnix, limit));

    private static List<HistoryMessage> Map(IEnumerable<MessageStore.HistoryRow>? rows)
        => rows is null ? [] : rows.Select(r => new HistoryMessage(r.Ts, r.Dir, r.UserId, r.Nick, r.Text)).ToList();
}
