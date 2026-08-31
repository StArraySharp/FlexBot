using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FlexBot.PluginApi;

/// <summary>共享 JSON 序列化选项：中文等非 ASCII 字符原样输出，不转 \uXXXX。</summary>
public static class BotJson
{
    /// <summary>带缩进（人工可读的持久化文件）。</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>不带缩进（紧凑输出/日志）。</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>
/// 共享定时调度器：插件注册周期/每日任务，宿主统一驱动；插件卸载时其任务自动取消。
/// </summary>
public interface IBotScheduler : IDisposable
{
    /// <summary>周期任务：每 interval 执行一次（首轮在 interval 后）。返回 IDisposable 可提前取消。</summary>
    IDisposable Every(TimeSpan interval, Func<Task> job, string? name = null);

    /// <summary>每日定时（HH:mm 本地时间；明早/今晚最近时刻起）。返回 IDisposable 可提前取消。</summary>
    IDisposable DailyAt(string hhmm, Func<Task> job, string? name = null);

    /// <summary>一次性延迟任务。返回 IDisposable 可提前取消。</summary>
    IDisposable After(TimeSpan delay, Func<Task> job, string? name = null);
}

// ============================================ 宿主实现 ============================================

/// <summary>宿主调度器实现：单一循环驱动全部任务，避免每插件自建 Task.Run。</summary>
public sealed class BotScheduler : IBotScheduler
{
    private sealed record Entry(string Name, Func<Task> Job, CancellationTokenSource Cts);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly CancellationTokenSource _loopCts = new();
    private readonly List<(DateTime Next, Guid Id)> _queue = [];
    private readonly object _queueLock = new();
    private readonly Task _loop;

    public BotScheduler() => _loop = Task.Run(LoopAsync);

    public IDisposable Every(TimeSpan interval, Func<Task> job, string? name = null)
    {
        if (interval < TimeSpan.FromSeconds(1)) interval = TimeSpan.FromSeconds(1);
        return Add(name ?? "every", DateTime.Now + interval, () => DateTime.Now + interval, job, interval);
    }

    public IDisposable DailyAt(string hhmm, Func<Task> job, string? name = null)
    {
        var parts = hhmm.Split(':');
        var hour = int.TryParse(parts.ElementAtOrDefault(0), out var h) ? Math.Clamp(h, 0, 23) : 8;
        var minute = int.TryParse(parts.ElementAtOrDefault(1), out var m) ? Math.Clamp(m, 0, 59) : 0;
        DateTime NextOf() =>
            DateTime.Today.AddHours(hour).AddMinutes(minute) is { } t && t <= DateTime.Now ? t.AddDays(1) : t;
        return Add(name ?? $"daily@{hour:D2}:{minute:D2}", NextOf(), NextOf, job, null);
    }

    public IDisposable After(TimeSpan delay, Func<Task> job, string? name = null) =>
        Add(name ?? "after", DateTime.Now + delay, () => DateTime.MaxValue, job, null);

    private IDisposable Add(string name, DateTime first, Func<DateTime> next, Func<Task> job, TimeSpan? recurring)
    {
        var id = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        _entries[id] = new Entry(name, Wrap(id, name, next, job, recurring), cts);
        Schedule(id, name, first, _entries[id].Job, recurring);
        return new Cancel(this, id);
    }

    private Func<Task> Wrap(Guid id, string name, Func<DateTime> next, Func<Task> job, TimeSpan? recurring) =>
        async () =>
        {
            try { await job(); }
            catch (Exception ex) { Console.WriteLine($"[sched] {name} 执行异常: {ex.Message}"); }
            if (_entries.TryGetValue(id, out var e) && !e.Cts.IsCancellationRequested && recurring is { } r)
                Schedule(id, name, next(), e.Job, r);
        };

    private void Schedule(Guid id, string name, DateTime at, Func<Task> job, TimeSpan? _)
    {
        lock (_queueLock) _queue.Add((at, id));
    }

    private async Task LoopAsync()
    {
        while (!_loopCts.IsCancellationRequested)
        {
            List<(DateTime Next, Guid Id)> due = [];
            lock (_queueLock)
            {
                var now = DateTime.Now;
                for (var i = 0; i < _queue.Count; )
                {
                    if (_queue[i].Next <= now) { due.Add(_queue[i]); _queue.RemoveAt(i); }
                    else i++;
                }
            }
            foreach (var (_, id) in due)
                if (_entries.TryGetValue(id, out var e) && !e.Cts.IsCancellationRequested)
                    _ = Task.Run(() => e.Job());
            await Task.Delay(500);
        }
    }

    private void CancelEntry(Guid id)
    {
        if (_entries.TryRemove(id, out var e))
        {
            e.Cts.Cancel();
            e.Cts.Dispose();
        }
    }

    private sealed class Cancel(BotScheduler owner, Guid id) : IDisposable
    {
        public void Dispose() => owner.CancelEntry(id);
    }

    public void Dispose()
    {
        _loopCts.Cancel();
        foreach (var (_, e) in _entries) { e.Cts.Cancel(); e.Cts.Dispose(); }
        _entries.Clear();
    }
}

/// <summary>插件级 KV 存储：JSON 文件持久化，按插件目录隔离（DataDir/kv.json）。</summary>
public sealed class PluginKeyValueStore : IDisposable
{
    private readonly string _file;
    private readonly ConcurrentDictionary<string, JsonElement> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();
    private Timer? _flushTimer;
    private volatile bool _dirty;

    public PluginKeyValueStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "kv.json");
        try
        {
            if (File.Exists(_file))
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_file));
                if (doc is not null)
                    foreach (var kv in doc) _cache[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[kv] 读取失败: {ex.Message}"); }
        // 防抖落盘：变更后 2s 合并写
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (!_cache.TryGetValue(key, out var je)) return defaultValue;
        try
        {
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(int) && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var i)) return (T)(object)i;
            if (typeof(T) == typeof(long) && je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var l)) return (T)(object)l;
            if (typeof(T) == typeof(double) && je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d)) return (T)(object)d;
            if (typeof(T) == typeof(bool) && je.ValueKind is JsonValueKind.True or JsonValueKind.False) return (T)(object)je.GetBoolean();
            return je.Deserialize<T>() ?? defaultValue;
        }
        catch { return defaultValue; }
    }

    public void Set<T>(string key, T value)
    {
        _cache[key] = JsonSerializer.SerializeToElement(value);
        _dirty = true;
    }

    public bool Delete(string key) => _cache.TryRemove(key, out _) ? (_dirty = true) : false;

    public IReadOnlyList<string> Keys => [.. _cache.Keys];

    private void Flush()
    {
        if (!_dirty) return;
        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, BotJson.Indented);
                File.WriteAllText(_file, json);
                _dirty = false;
            }
            catch (Exception ex) { Console.WriteLine($"[kv] 保存失败: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        Flush();
    }
}

/// <summary>共享 HTTP 客户端：统一超时/UA，插件免自建 disposing 管家。</summary>
public sealed class SharedHttp
{
    public HttpClient Client { get; } = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        MaxConnectionsPerServer = 16
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    public SharedHttp() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) FlexBot/2.0");

    public Task<string> GetStringAsync(string url) => Client.GetStringAsync(url);
    public Task<byte[]> GetByteArrayAsync(string url) => Client.GetByteArrayAsync(url);
    public Task<HttpResponseMessage> GetAsync(string url) => Client.GetAsync(url);
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content) => Client.PostAsync(url, content);
}
