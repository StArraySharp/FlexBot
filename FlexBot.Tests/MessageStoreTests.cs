using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// 消息落库（数据层 P1）的核心行为测试。
/// 关注点：只存关注群与私聊、重投去重、保留期清理、重启后数据仍在。
/// </summary>
public class MessageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fb-msgstore-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _db;
    private readonly string _watched;

    public MessageStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "flexbot.db");
        _watched = Path.Combine(_dir, "watched_groups.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Watch(params long[] groupIds) =>
        File.WriteAllLines(_watched, groupIds.Select(x => x.ToString()));

    private long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void 私聊消息会落库并能在重启后读到()
    {
        using (var store = new MessageStore(_db, _watched))
        {
            Assert.Equal("private:1001", store.ScopeFor(null, 1001));
            Assert.True(store.ShouldStore("private:1001"));

            store.Record("onebot", "private:1001", messageId: 55, ts: Now, userId: 1001, nick: "甲", text: "你好", raw: "{}");
        }

        using var reopened = new MessageStore(_db, _watched);
        var stats = reopened.Stats();
        Assert.Equal(1, stats.Total);
        Assert.Equal(1, stats.Private);
        Assert.Equal(0, stats.Group);

        var recent = reopened.Recent("private:1001", 10);
        Assert.Single(recent);
    }

    [Fact]
    public void 未关注群不落库_关注后落库()
    {
        Watch();   // 名单为空
        using var store = new MessageStore(_db, _watched);

        Assert.False(store.ShouldStore("group:748394300"));
        store.Record("onebot", "group:748394300", 1, Now, 7, "甲", "不想被存", null);

        Watch(748394300);                       // 加进关注名单（按 mtime 重新读取）
        File.SetLastWriteTimeUtc(_watched, DateTime.UtcNow.AddSeconds(2));
        Assert.True(store.ShouldStore("group:748394300"));
        store.Record("onebot", "group:748394300", 2, Now, 7, "甲", "现在要存", null);

        Thread.Sleep(700);                      // 等后台批量写入
        var stats = store.Stats();
        Assert.Equal(1, stats.Total);           // 只有关注之后的那条
        Assert.Equal(1, stats.Group);
    }

    [Fact]
    public void 同一条消息重复投递只存一份()
    {
        using var store = new MessageStore(_db, _watched);
        for (var i = 0; i < 5; i++)
            store.Record("onebot", "private:2002", messageId: 99, ts: 1_700_000_000, userId: 2002, nick: "乙", text: "重复", null);

        Thread.Sleep(700);
        Assert.Equal(1, store.Stats().Total);
    }

    [Fact]
    public void 超过保留期会被清理()
    {
        using (var store = new MessageStore(_db, _watched))
        {
            var old = Now - (MessageStore.GroupKeepDays + 5) * 86400L;      // 群 90 天
            var fresh = Now - 86400;
            Watch(111);
            store.Record("onebot", "group:111", 1, old, 7, "甲", "过期", null);
            store.Record("onebot", "group:111", 2, fresh, 7, "甲", "还新鲜", null);
        }

        using var reopened = new MessageStore(_db, _watched);
        Assert.Equal(2, reopened.Stats().Total);
        Assert.Equal(1, reopened.Purge());      // 只删过期那条
        Assert.Equal(1, reopened.Stats().Total);
    }

    [Fact]
    public void 队列软上限下不会崩_统计返回时间范围()
    {
        using var store = new MessageStore(_db, _watched);
        for (var i = 0; i < 50; i++)
            store.Record("official", "private:3003", i + 1, Now - 50 + i, 3003, "丙", "文本" + i, null);

        Thread.Sleep(700);
        var stats = store.Stats();
        Assert.Equal(50, stats.Total);
        Assert.NotEqual("—", stats.Oldest);
        Assert.NotEqual("—", stats.Newest);
        Assert.True(stats.Bytes > 0);
    }
}
