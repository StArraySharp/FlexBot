using FlexBot;
using FlexBot.PluginApi;
using Xunit;

namespace FlexBot.Tests;

/// <summary>P3 尾巴：长期记忆条目的存储（mem 表，与消息库共文件）。</summary>
public class MemoryNotesStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fb-mem-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _db;
    public MemoryNotesStoreTests() { Directory.CreateDirectory(_dir); _db = Path.Combine(_dir, "flexbot.db"); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void 写入后可读回_按重要度排序()
    {
        using var s = new MemoryNotesStore(_db);
        s.Add("group:1", "preference", "主人喜欢简洁回复", 5, "风格");
        s.Add("group:1", "fact", "群里常聊投资", 3, "话题");
        var list = s.List("group:1");
        Assert.Equal(2, list.Count);
        Assert.Equal("主人喜欢简洁回复", list[0].Text);   // 重要度高者在前
        Assert.Equal(5, list[0].Importance);
    }

    [Fact]
    public void 同一条重复写入不会堆积_只抬重要度()
    {
        using var s = new MemoryNotesStore(_db);
        s.Add("group:1", "fact", "同一句话", 2, "");
        s.Add("group:1", "fact", "同一句话", 4, "");
        var list = s.List("group:1");
        var n = Assert.Single(list);
        Assert.Equal(4, n.Importance);
    }

    [Fact]
    public void scope隔离_空值被忽略()
    {
        using var s = new MemoryNotesStore(_db);
        s.Add("group:1", "fact", "群里的", 3, "");
        s.Add("private:2", "fact", "私聊里的", 3, "");
        s.Add("", "fact", "无 scope 应被忽略", 3, "");
        s.Add("group:1", "fact", "", 3, "");
        Assert.Single(s.List("group:1"));
        Assert.Single(s.List("private:2"));
    }

    [Fact]
    public void 解析器与存储可以串起来()
    {
        using var s = new MemoryNotesStore(_db);
        var notes = MemoryNoteParser.Parse("[{\"kind\":\"rule\",\"text\":\"群里不谈政治\",\"importance\":5,\"tags\":\"规则\"}]");
        foreach (var n in notes) s.Add("group:9", n.Kind, n.Text, n.Importance, n.Tags);
        var got = s.List("group:9");
        var one = Assert.Single(got);
        Assert.Equal("rule", one.Kind);
        Assert.Equal("群里不谈政治", one.Text);
        Assert.Equal("规则", one.Tags);
    }
}
