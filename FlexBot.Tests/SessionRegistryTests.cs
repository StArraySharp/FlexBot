using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>消息库的区间查询（日报用）与 P4 会话注册表/记忆组。</summary>
public class HistoryRangeAndSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fb-sess-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _db;

    public HistoryRangeAndSessionTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "flexbot.db");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void Range只取区间内的消息且保持时间正序()
    {
        var watched = Path.Combine(_dir, "watched_groups.txt");
        File.WriteAllText(watched, "888");
        using var store = new MessageStore(_db, watched);
        var t = Now;
        store.RecordIn("onebot", "group:888", 1, t - 200, 1, "甲", "区间外-早", null);
        store.RecordIn("onebot", "group:888", 2, t - 50, 1, "甲", "区间内-1", null);
        store.RecordIn("onebot", "group:888", 3, t - 10, 1, "乙", "区间内-2", null);
        store.RecordIn("onebot", "group:888", 4, t + 100, 1, "甲", "区间外-晚", null);
        Thread.Sleep(700);

        var rows = store.Range("group:888", t - 100, t, 100);
        Assert.Equal(2, rows.Count);
        Assert.Equal("区间内-1", rows[0].Text);
        Assert.Equal("区间内-2", rows[1].Text);
    }

    [Fact]
    public void 新会话自动登记并独占记忆组()
    {
        using var reg = new SessionRegistry(_db);
        reg.Ensure("group:111");
        reg.Ensure("private:222");
        var list = reg.List();
        Assert.Equal(2, list.Count);
        Assert.NotEqual(list[0].ClusterId, list[1].ClusterId);      // 默认各自独占
        Assert.Single(reg.ScopesFor("group:111"));                  // 未关联时只看得见自己
        Assert.StartsWith("s_", list[0].Id);
    }

    [Fact]
    public void 关联后共享记忆_分离后恢复独占()
    {
        using var reg = new SessionRegistry(_db);
        reg.Ensure("group:111");
        reg.Ensure("private:222");

        // 涉及私聊必须显式确认
        Assert.Contains("暴露", reg.Link("group:111", "private:222", allowPrivate: false));
        Assert.Contains("已关联", reg.Link("group:111", "private:222", allowPrivate: true));

        var shared = reg.ScopesFor("private:222");
        Assert.Equal(2, shared.Count);
        Assert.Contains("group:111", shared);

        var id = reg.List().First(x => x.Scope == "private:222").Id;
        Assert.Contains("已分离", reg.Unlink(id));
        Assert.Single(reg.ScopesFor("private:222"));                 // 分离后互不可见
    }

    [Fact]
    public void 改名与审计都会落库()
    {
        using var reg = new SessionRegistry(_db);
        reg.Ensure("group:111");
        var id = reg.List()[0].Id;
        Assert.Contains("已把", reg.Rename(id, "主群"));
        Assert.Contains("主群", reg.Describe());
    }
}
