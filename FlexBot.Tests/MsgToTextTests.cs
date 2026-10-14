using FlexBot.PluginApi;
using OneBotLib.MessageSegment;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// MsgToText 的形态覆盖：插件发送时给的是 List&lt;MessageSegment&gt;，
/// 早期实现只认 JsonElement，其余走 ToString() ⇒ 出站记录里出现类型名。
/// </summary>
public class MsgToTextTests
{
    [Fact]
    public void 消息段列表渲染成可读文本()
    {
        var segs = new List<MessageSegment>
        {
            MessageSegment.Text("你好 "),
            MessageSegment.At(12345),
            MessageSegment.Text(" "),
            new() { Type = "image", Data = new() { ["file"] = "x.jpg" } },
            new() { Type = "face", Data = new() { ["id"] = 1 } },
        };
        Assert.Equal("你好 @12345 [图片][表情]", BotHelpers.MsgToText(segs));
    }

    [Fact]
    public void 不再退化成类型名()
    {
        var text = BotHelpers.MsgToText(new List<MessageSegment> { MessageSegment.Text("hi") });
        Assert.Equal("hi", text);
        Assert.DoesNotContain("System.Collections", text);
    }

    [Fact]
    public void 纯字符串与普通对象仍可用()
    {
        Assert.Equal("abc", BotHelpers.MsgToText("abc"));
        Assert.Equal("42", BotHelpers.MsgToText(42));
        Assert.Equal("", BotHelpers.MsgToText(null!));
    }

    [Fact]
    public void 合并转发与回复引用有合理占位()
    {
        var segs = new List<MessageSegment>
        {
            new() { Type = "reply", Data = new() { ["id"] = 1 } },
            new() { Type = "forward", Data = new() { ["id"] = 2 } },
            new() { Type = "text", Data = new() { ["text"] = "正文" } },
        };
        Assert.Equal("[合并转发]正文", BotHelpers.MsgToText(segs));
    }
}
