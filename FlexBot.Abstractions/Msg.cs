using OneBotLib.MessageSegment;

namespace FlexBot.PluginApi;

/// <summary>插件间共享的小工具</summary>
public static class Msg
{
    /// <summary>回复时引用消息源</summary>
    public static List<MessageSegment> Quote(long messageId, string text) =>
    [
        MessageSegment.Reply(messageId),
        MessageSegment.Text(text)
    ];
}
