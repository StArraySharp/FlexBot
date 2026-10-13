namespace FlexBot;

/// <summary>
/// 入站消息去重。
///
/// 为什么需要：OneBot 侧断线重连后可能重放消息，实现层（LLBot/NapCat）也可能重复投递同一条；
/// 官方通道重推同理。重复处理一条消息的代价可能是**重复回复**（AI 回复最贵），也可能是重复执行命令。
///
/// 判据是 (通道, 会话类型, 消息 id)：同一条消息的 id 唯一，而用户先后发两条同样内容的消息
/// 是两个不同的 id，因此不会误杀。TTL 十分钟、容量上限 2000 条，满了先清过期再丢最旧的。
/// 进程重启后集合清空（重启瞬间的重放无法覆盖，代价可接受）。
/// </summary>
static class MessageDedup
{
    private const int TtlSeconds = 600;
    private const int MaxEntries = 2000;

    private static readonly Dictionary<string, long> _seen = new();
    private static readonly object _lock = new();

    /// <summary>返回 true 表示这条消息在 TTL 内已经处理过，调用方应直接跳过</summary>
    public static bool IsDuplicate(string key)
    {
        var now = DateTime.UtcNow.Ticks;
        var ttl = TimeSpan.FromSeconds(TtlSeconds).Ticks;
        lock (_lock)
        {
            if (_seen.TryGetValue(key, out var t) && now - t < ttl) return true;

            if (_seen.Count >= MaxEntries)
            {
                foreach (var k in _seen.Where(kv => now - kv.Value >= ttl).Select(kv => kv.Key).ToList())
                    _seen.Remove(k);
                while (_seen.Count >= MaxEntries)
                {
                    var oldest = _seen.OrderBy(kv => kv.Value).First().Key;
                    _seen.Remove(oldest);
                }
            }
            _seen[key] = now;
            return false;
        }
    }

    /// <summary>当前缓存条数（诊断用）</summary>
    public static int Count { get { lock (_lock) return _seen.Count; } }
}
