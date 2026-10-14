using FlexBot.PluginApi;

namespace FlexBot;

/// <summary>
/// <see cref="IHistoryStore"/> 的宿主实现：把 MessageStore 的行映射成插件侧的消息记录。
/// scope 由调用方保证来自当前回合；这里再按 **会话记忆组**（P4）展开成可见 scope 集合 ——
/// 关联过的会话共享记忆，分离后自动互不可见（因为行本身仍按原始 scope 落库）。
/// </summary>
sealed class HistoryStoreAccess : IHistoryStore
{
    public bool Enabled => MessageStore.Current is not null;

    private static List<string> Visible(string scope)
        => SessionRegistry.Current?.ScopesFor(scope) ?? [scope];

    public IReadOnlyList<HistoryMessage> Recent(string scope, int limit = 20)
    {
        var store = MessageStore.Current;
        if (store is null) return [];
        var all = new List<MessageStore.HistoryRow>();
        foreach (var sc in Visible(scope)) all.AddRange(store.Recent(sc, limit));
        return Map(all.OrderBy(r => r.Ts).TakeLast(limit).ToList());
    }

    public IReadOnlyList<HistoryMessage> Before(string scope, long beforeUnix, int limit = 20)
    {
        var store = MessageStore.Current;
        if (store is null) return [];
        var all = new List<MessageStore.HistoryRow>();
        foreach (var sc in Visible(scope)) all.AddRange(store.Before(sc, beforeUnix, limit));
        return Map(all.TakeLast(limit).ToList());
    }

    public IReadOnlyList<HistoryMessage> Range(string scope, long fromUnix, long toUnix, int limit = 2000)
    {
        var store = MessageStore.Current;
        if (store is null) return [];
        var all = new List<MessageStore.HistoryRow>();
        foreach (var sc in Visible(scope)) all.AddRange(store.Range(sc, fromUnix, toUnix, limit));
        return Map(all.Take(limit).ToList());
    }

    public IReadOnlyList<HistoryMessage> Search(string scope, string query, int limit = 10)
    {
        var store = MessageStore.Current;
        if (store is null) return [];
        var all = new List<MessageStore.HistoryRow>();
        foreach (var sc in Visible(scope)) all.AddRange(store.Search(sc, query, limit));
        return Map(all.Take(limit).ToList());
    }

    private static List<HistoryMessage> Map(IEnumerable<MessageStore.HistoryRow>? rows)
        => rows is null ? [] : rows.Select(r => new HistoryMessage(r.Ts, r.Dir, r.UserId, r.Nick, r.Text)).ToList();
}