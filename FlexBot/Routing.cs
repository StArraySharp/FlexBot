using FlexBot.PluginApi;
using OneBotLib.Events;

namespace FlexBot;

// 插件日志：统一加 [plugin:Name] 前缀（Console.Out 已被 TimestampWriter 包装，自动带时间戳）
sealed class PluginLog(string name) : ILog
{
    private readonly string _tag = $"[plugin:{name}]";

    public void Info(string message) => Console.WriteLine($"{_tag} {message}");
    public void Warn(string message) => Console.WriteLine($"{_tag} [warn] {message}");
    public void Error(string message) => Console.WriteLine($"{_tag} [error] {message}");
    public void Error(string message, Exception exception) => Console.WriteLine($"{_tag} [error] {message}: {exception}");
}

// 消息路由：按优先级（大者优先，同优先级按注册顺序）分发给各插件注册的处理器；
// 返回 Stop 则停止向后续插件传播；单个插件异常不影响其他插件
sealed class MessageRouter : IMessageHub
{
    private sealed class Entry(int priority, int seq, string tag, Delegate handler)
    {
        public int Priority = priority;
        public int Seq = seq;
        public string Tag = tag;
        public Delegate Handler = handler;
    }

    private readonly object _lock = new();
    private readonly List<Entry> _private = [];
    private readonly List<Entry> _group = [];
    private int _seq;

    public IDisposable OnPrivate(Func<PrivateMessageEventArgs, Task<Handled>> handler, int priority = 0, string? tag = null) =>
        Add(_private, handler, priority, tag);

    public IDisposable OnGroup(Func<GroupMessageEventArgs, Task<Handled>> handler, int priority = 0, string? tag = null) =>
        Add(_group, handler, priority, tag);

    private IDisposable Add(List<Entry> list, Delegate handler, int priority, string? tag)
    {
        var entry = new Entry(priority, _seq++, tag ?? handler.Method.DeclaringType?.Assembly.GetName().Name ?? "?", handler);
        lock (_lock)
        {
            list.Add(entry);
            list.Sort((a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Seq.CompareTo(b.Seq));
        }
        return new Disposer(() =>
        {
            lock (_lock) list.Remove(entry);
        });
    }

    public Task DispatchPrivateAsync(PrivateMessageEventArgs e) => Dispatch(_private, e);
    public Task DispatchGroupAsync(GroupMessageEventArgs e) => Dispatch(_group, e);

    private async Task Dispatch<TArgs>(List<Entry> list, TArgs e) where TArgs : EventArgs
    {
        Entry[] snapshot;
        lock (_lock) snapshot = [.. list];
        foreach (var entry in snapshot)
        {
            try
            {
                var result = await ((Func<TArgs, Task<Handled>>)entry.Handler)(e);
                if (result == Handled.Stop)
                {
                    // 记录哪个插件阻止了消息传播
                    var msgPreview = (e as GroupMessageEventArgs)?.Message?.PlainText 
                        ?? (e as PrivateMessageEventArgs)?.Message?.PlainText 
                        ?? "event";
                    var preview = msgPreview.Length > 50 ? msgPreview[..50] + "..." : msgPreview;
                    Console.WriteLine($"[route] 插件 {entry.Tag} 停止消息传播: {preview}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[route] 插件 {entry.Tag} 消息处理异常: {ex.Message}");
            }
        }
    }
}

// 事件路由：按事件类型分发（戳一戳、群成员变动、名片变更、连接状态等）
sealed class EventRouter : IEventHub
{
    private sealed class Entry(string tag, Delegate handler)
    {
        public string Tag = tag;
        public Delegate Handler = handler;
    }

    private readonly object _lock = new();
    private readonly Dictionary<Type, List<Entry>> _handlers = [];

    public IDisposable On<TEvent>(Func<TEvent, Task> handler, string? tag = null) where TEvent : EventArgs
    {
        var entry = new Entry(tag ?? typeof(TEvent).Name, handler);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
                _handlers[typeof(TEvent)] = list = [];
            list.Add(entry);
        }
        return new Disposer(() =>
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var list))
                    list.Remove(entry);
            }
        });
    }

    public async Task RaiseAsync<TEvent>(TEvent e) where TEvent : EventArgs
    {
        Entry[] snapshot;
        lock (_lock)
            snapshot = _handlers.TryGetValue(typeof(TEvent), out var list) ? [.. list] : [];
        foreach (var entry in snapshot)
        {
            try
            {
                await ((Func<TEvent, Task>)entry.Handler)(e);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[route] 插件 {entry.Tag} 事件处理异常: {ex.Message}");
            }
        }
    }
}

sealed class Disposer(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
