namespace FlexBot.PluginApi;

/// <summary>
/// AI 用量上报口（宿主实现，经 <see cref="IBotContext.Usage"/> 给插件）。
///
/// 为什么要这层：用量表在宿主里（与消息库共用一个 SQLite 文件），插件看不到宿主类型；
/// 用最小接口把它露出来，插件只负责"把这一次调用的 token 报上来"。
/// 参数含义对照参考项目 AstrBot 的 provider_stats：输入/输出/缓存命中 token 与耗时。
/// </summary>
public interface IUsageSink
{
    /// <summary>记录一次 AI 调用（失败也算一次，token 传 0）</summary>
    void Record(string provider, string model, long inputTokens, long outputTokens, long cachedTokens, long elapsedMs);

    /// <summary>
    /// 累计用量快照(次数/输入/输出/缓存命中 token)。
    /// 带默认实现：旧实现不必改动即可编译；宿主实现会覆盖它返回真实数据。
    /// Agent 插件据此算缓存命中率与额度占比，无需触碰宿主内部类型。
    /// </summary>
    UsageTotals Totals() => new(0, 0, 0, 0);
}

/// <summary>累计用量(tokens)。Calls=调用次数，Cached=缓存命中 token。</summary>
public readonly record struct UsageTotals(long Calls, long InputTokens, long OutputTokens, long CachedTokens);
