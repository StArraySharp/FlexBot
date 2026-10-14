using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>检索（P2）：二元切分 + FTS5。重点是中文 2 字词必须能命中（trigram 做不到）。</summary>
public class HistorySearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fb-histsearch-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _db;
    private readonly string _watched;

    public HistorySearchTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "flexbot.db");
        _watched = Path.Combine(_dir, "watched_groups.txt");
        File.WriteAllText(_watched, "111");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Theory]
    [InlineData("今天在群里聊了投资和基金的事", "基金", true)]
    [InlineData("今天在群里聊了投资和基金的事", "投资", true)]
    [InlineData("今天在群里聊了投资和基金的事", "熬夜", false)]
    [InlineData("他买了 BTC 和 ETH", "btc", true)]
    [InlineData("他买了 BTC 和 ETH", "BTC", true)]
    public void 二元切分让中文两字词也能命中(string text, string query, bool shouldHit)
    {
        using var store = new MessageStore(_db, _watched);
        store.RecordIn("onebot", "private:1", 1, Now, 1, "甲", text, null);
        Thread.Sleep(700);
        var hits = store.Search("private:1", query, 10);
        Assert.Equal(shouldHit ? 1 : 0, hits.Count);
        if (shouldHit) Assert.Equal(text, hits[0].Text);
    }

    [Fact]
    public void 检索严格限定在指定scope内()
    {
        using var store = new MessageStore(_db, _watched);
        store.RecordIn("onebot", "private:1", 1, Now, 1, "甲", "甲说基金的事", null);
        store.RecordIn("onebot", "private:2", 1, Now, 2, "乙", "乙也说基金的事", null);
        Thread.Sleep(700);
        var mine = store.Search("private:1", "基金", 10);
        Assert.Single(mine);
        Assert.Equal("甲说基金的事", mine[0].Text);
    }

    [Fact]
    public void 重复写入不会让索引重复()
    {
        using var store = new MessageStore(_db, _watched);
        for (var i = 0; i < 3; i++)
            store.RecordIn("onebot", "private:1", 42, 1_700_000_000, 1, "甲", "重复的基金消息", null);
        Thread.Sleep(700);
        Assert.Equal(1, store.Stats().Total);
        Assert.Single(store.Search("private:1", "基金", 10));
    }

    [Fact]
    public void 旧库升级会补建索引()
    {
        // 忠实模拟 P1 时期的库：只有 msg 表（没有 msg_fts），且已有数据。
        // 注意：contentless FTS5 不允许 DELETE，所以不能用"清空索引"来模拟。
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = """
                CREATE TABLE msg(
                  id INTEGER PRIMARY KEY, transport TEXT NOT NULL, scope TEXT NOT NULL,
                  dir TEXT NOT NULL DEFAULT 'in', message_id INTEGER NOT NULL, ts INTEGER NOT NULL,
                  user_id INTEGER NOT NULL, nick TEXT NOT NULL, text TEXT NOT NULL, raw_json TEXT,
                  recalled INTEGER NOT NULL DEFAULT 0,
                  UNIQUE(transport, scope, message_id, ts));
                INSERT INTO msg(transport, scope, dir, message_id, ts, user_id, nick, text)
                VALUES('onebot','private:1','in',7,1700000000,1,'甲','老数据里的基金');
                """;
            c.ExecuteNonQuery();
        }

        using var reopened = new MessageStore(_db, _watched);   // 打开时自动建索引并回填
        Assert.Single(reopened.Search("private:1", "基金", 10));
    }

    [Fact]
    public void Before能取到指定时间之前的消息()
    {
        using var store = new MessageStore(_db, _watched);
        var now = Now;
        store.RecordIn("onebot", "private:1", 1, now - 100, 1, "甲", "更早的一条", null);
        store.RecordIn("onebot", "private:1", 2, now, 1, "甲", "最新一条", null);
        Thread.Sleep(700);
        var older = store.Before("private:1", now, 10);
        Assert.Single(older);
        Assert.Equal("更早的一条", older[0].Text);
    }
}
