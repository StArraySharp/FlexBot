using FlexBot.PluginApi;
using Xunit;

namespace FlexBot.Tests;

/// <summary>P3 尾巴：结构化记忆条目解析（纯逻辑，可单测）。</summary>
public class MemoryNoteParserTests
{
    [Fact]
    public void 解析被代码块包裹的条目()
    {
        var outText = "好的，我顺便整理了一下：```json\n[\n {\"kind\":\"preference\",\"text\":\"主人喜欢简洁回复\",\"importance\":5,\"tags\":[\"风格\"]},\n {\"kind\":\"fact\",\"text\":\"群里常聊投资\",\"importance\":3,\"tags\":\"话题,投资\"}\n]\n```";
        var notes = MemoryNoteParser.Parse(outText);
        Assert.Equal(2, notes.Count);
        Assert.Equal("preference", notes[0].Kind);
        Assert.Equal(5, notes[0].Importance);
        Assert.Equal("风格", notes[0].Tags);
        Assert.Equal("话题,投资", notes[1].Tags);
    }

    [Fact]
    public void 未知kind降级为fact_importance越界被夹紧()
    {
        var notes = MemoryNoteParser.Parse("[{\"kind\":\"nonsense\",\"text\":\"甲乙丙\",\"importance\":99}]");
        var n = Assert.Single(notes);
        Assert.Equal("fact", n.Kind);
        Assert.Equal(5, n.Importance);
    }

    [Fact]
    public void 空文本条目被丢弃_同一批重复被去重()
    {
        var notes = MemoryNoteParser.Parse("[{\"kind\":\"fact\",\"text\":\"\"},{\"kind\":\"fact\",\"text\":\"同一句\"},{\"kind\":\"fact\",\"text\":\"同一句\"}]");
        var n = Assert.Single(notes);
        Assert.Equal("同一句", n.Text);
    }

    [Theory]
    [InlineData("完全没有 JSON")]
    [InlineData("")]
    [InlineData("[] 之外还说了话：{不是数组}")]
    [InlineData(null)]
    public void 解析不出就返回空列表而不是抛异常(string? text)
        => Assert.Empty(MemoryNoteParser.Parse(text));

    [Fact]
    public void 字符串里含方括号也能正确切出数组()
    {
        var notes = MemoryNoteParser.Parse("注释在这里 [{\"kind\":\"event\",\"text\":\"他说 [重要] 明天开会\",\"importance\":4}] 结束");
        var n = Assert.Single(notes);
        Assert.Contains("[重要]", n.Text);
    }
}
