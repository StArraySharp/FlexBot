using OneBotLib.Events;

namespace FlexBot.PluginApi;

/// <summary>消息处理结果：Stop 表示已处理完毕，终止向低优先级插件传播。</summary>
public enum Handled
{
    Continue,
    Stop
}

/// <summary>
/// 消息订阅：按 priority 从大到小依次调用（同优先级按注册顺序），
/// 任一处理器返回 Stop 则不再继续。返回 IDisposable 用于退订。
/// </summary>
public interface IMessageHub
{
    /// <param name="priority">命令类插件建议 100+，AI 类建议 0</param>
    IDisposable OnPrivate(Func<PrivateMessageEventArgs, Task<Handled>> handler, int priority = 0, string? tag = null);

    IDisposable OnGroup(Func<GroupMessageEventArgs, Task<Handled>> handler, int priority = 0, string? tag = null);
}

/// <summary>事件订阅（戳一戳、群成员变动、名片变更、连接状态等，事件类型见 OneBotLib.Events）。</summary>
public interface IEventHub
{
    IDisposable On<TEvent>(Func<TEvent, Task> handler, string? tag = null) where TEvent : EventArgs;
}
