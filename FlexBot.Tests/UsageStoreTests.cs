using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>AI 用量统计（对照 AstrBot 的 provider_stats）。</summary>
public class UsageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fb-usage-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _db;

    public UsageStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "flexbot.db");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void 记录后能汇总与按模型聚合()
    {
        using var store = new UsageStore(_db);
        store.Record("default", "gpt-x", 100, 50, 20, 1200);
        store.Record("default", "gpt-x", 200, 80, 0, 900);
        store.Record("default", "claude-y", 10, 5, 0, 300);

        var s = store.Summary();
        Assert.Equal(3, s.Calls);
        Assert.Equal(310, s.In);
        Assert.Equal(135, s.Out);
        Assert.Equal(20, s.Cached);
        Assert.Equal(3, s.TodayCalls);

        var byModel = store.ByModel();
        Assert.Equal(2, byModel.Count);
        Assert.Equal("gpt-x", byModel[0].Model);      // 输出 token 多的排前
        Assert.Equal(2, byModel[0].Calls);
    }

    [Fact]
    public void 空库统计数据为零()
    {
        using var store = new UsageStore(_db);
        var s = store.Summary();
        Assert.Equal(0, s.Calls);
        Assert.Equal(0, s.In);
        Assert.Empty(store.ByModel());
    }
}
