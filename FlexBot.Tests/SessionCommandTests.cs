using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>命令派发（⑤）：session 命令在聊天与 WebUI 两条路径共用同一实现，这里直接测它。</summary>
public class SessionCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fb-cmd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _db;
    public SessionCommandTests() { Directory.CreateDirectory(_dir); _db = Path.Combine(_dir, "flexbot.db"); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void 空参或无会话时给出可读回执()
    {
        using var reg = new SessionRegistry(_db);
        var r = SessionRegistry.HandleCommand("");
        Assert.False(string.IsNullOrWhiteSpace(r));
        Assert.Contains("会话", r);
    }

    [Fact]
    public void 非法子命令回到用法提示()
    {
        using var reg = new SessionRegistry(_db);
        Assert.Contains("用法", SessionRegistry.HandleCommand("frobnicate"));
    }

    [Fact]
    public void link缺少会话时不会误报成功()
    {
        using var reg = new SessionRegistry(_db);
        reg.Ensure("group:111");
        reg.Ensure("private:222");
        var id = reg.List().First(x => x.Scope == "group:111").Id;
        var other = reg.List().First(x => x.Scope == "private:222").Id;
        Assert.Contains("暴露", SessionRegistry.HandleCommand($"link {id} {other}"));     // 无 --yes
        Assert.Contains("已关联", SessionRegistry.HandleCommand($"link {id} {other} --yes"));
    }
}
