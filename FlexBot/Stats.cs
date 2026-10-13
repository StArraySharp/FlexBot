using System.Collections.Concurrent;

namespace FlexBot;

/// <summary>
/// 宿主级运行统计与最近消息环形缓冲（内存，重启归零）。
/// 统计用于 /api/stats 与 WebUI；消息缓冲用于 WebUI 的「实时消息流」页面 ——
/// 官方通道没有历史消息接口，想看机器人在群里说了什么只能自己留痕。
/// </summary>
static class Stats
{
    private static long _in, _out, _reconnects, _commands, _dedupDrop, _pluginErrors;
    private static readonly DateTime StartedAt = DateTime.Now;

    public static void IncIn() => Interlocked.Increment(ref _in);
    public static void IncOut() => Interlocked.Increment(ref _out);
    public static void IncReconnect() => Interlocked.Increment(ref _reconnects);
    public static void IncCommand() => Interlocked.Increment(ref _commands);
    public static void IncDedupDropped() => Interlocked.Increment(ref _dedupDrop);
    public static void IncPluginError() => Interlocked.Increment(ref _pluginErrors);

    public static object Snapshot() => new
    {
        startedAt = StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        uptimeSec = (long)(DateTime.Now - StartedAt).TotalSeconds,
        inMessages = Interlocked.Read(ref _in),
        outMessages = Interlocked.Read(ref _out),
        commands = Interlocked.Read(ref _commands),
        dedupDropped = Interlocked.Read(ref _dedupDrop),
        reconnects = Interlocked.Read(ref _reconnects),
        pluginErrors = Interlocked.Read(ref _pluginErrors),
        recentMessages = MessageRing.SnapshotCount
    };

    /// <summary>最近消息环形缓冲：只保留文本摘要，用于 WebUI 消息流（不回放、不落盘）</summary>
    public static readonly MessageRing Recent = new();
}

/// <summary>定长环形缓冲（线程安全），元素为一条消息摘要</summary>
sealed class MessageRing
{
    private const int Capacity = 500;
    private const int MaxTextChars = 600;

    private readonly record struct Item(long Seq, string Time, string Dir, string Kind, long Target, string Text);

    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly object _lock = new();
    private long _seq;

    public static int SnapshotCount { get; private set; }

    /// <summary>dir = "in"（收到）/ "out"（发出）；kind = "group" / "private"；target = 群号或用户号</summary>
    public void Add(string dir, string kind, long target, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > MaxTextChars) text = text[..MaxTextChars] + "…";

        lock (_lock)
        {
            var item = new Item(++_seq, DateTime.Now.ToString("HH:mm:ss"), dir, kind, target, text);
            _queue.Enqueue(item);
            while (_queue.Count > Capacity && _queue.TryDequeue(out _)) { }
            SnapshotCount = _queue.Count;
        }
    }

    /// <summary>取 seq 大于 after 的消息（最多 max 条），形如 /api/logs 的增量轮询协议</summary>
    public object GetAfter(long after, int max = 200)
    {
        Item[] items;
        lock (_lock) items = _queue.ToArray();
        var picked = items.Where(i => i.Seq > after).Take(max)
                          .Select(i => new { seq = i.Seq, time = i.Time, dir = i.Dir, kind = i.Kind, target = i.Target, text = i.Text })
                          .ToList();
        return new { next = items.Length > 0 ? items[^1].Seq : after, count = picked.Count, messages = picked };
    }
}
