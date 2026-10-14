using FlexBot.PluginApi;
using Xunit;

namespace FlexBot.Tests;

/// <summary>群内禁用仅私聊命令（fs_* / pb_*）的策略判定。</summary>
public class CommandPolicyTests
{
    [Theory]
    [InlineData("fs_read", true)]
    [InlineData("fs_write", true)]
    [InlineData("fs_replace", true)]
    [InlineData("pb_build", true)]
    [InlineData("pb_list", true)]
    [InlineData("FS_READ", true)]        // 大小写不敏感
    [InlineData("ping", false)]
    [InlineData("help", false)]
    [InlineData("fs", false)]            // 只有前缀，不算
    [InlineData("pbs_x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 仅私聊命令判定(string? name, bool expected)
        => Assert.Equal(expected, CommandPolicy.IsPrivateOnly(name));

    [Fact]
    public void 回执文案固定()
        => Assert.Contains("仅限私聊", CommandPolicy.GroupDenyMessage);
}
