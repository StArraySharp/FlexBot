using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// 命令行选项解析的回归护栏。
///
/// 这类功能"错了不报错，只会行为诡异"：把 <c>--HELP</c> 当未知参数、两个开关同时给时静默取其一、
/// 或者帮助文本里漏写某个新开关（文档与实现漂移）。所以判定顺序、大小写策略、帮助文本覆盖度都用测试钉住。
/// </summary>
public class CliOptionsTests
{
    [Fact]
    public void 无参数时全部取默认值_且不需要控制台()
    {
        var c = CliOptions.Parse([]);

        Assert.False(c.Help);
        Assert.False(c.Version);
        Assert.False(c.ForceGui);
        Assert.False(c.NoGui);
        Assert.False(c.Conflict);
        Assert.Empty(c.Unknown);
        Assert.False(c.NeedsConsole);   // 默认形态是开窗口：WinExe 下不该弹控制台
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("--HELP")]        // Windows 用户常这么敲
    [InlineData("  --Help  ")]    // 前后空白也要容错
    public void 帮助的多种写法都能识别(string arg)
    {
        var c = CliOptions.Parse([arg]);

        Assert.True(c.Help);
        Assert.True(c.NeedsConsole);   // 必须先开控制台，否则帮助文本看不到
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("--Version")]
    public void 版本开关可识别(string arg) => Assert.True(CliOptions.Parse([arg]).Version);

    [Fact]
    public void gui与no_gui同时给算冲突()
    {
        var c = CliOptions.Parse(["--gui", "--no-gui"]);

        Assert.True(c.ForceGui);
        Assert.True(c.NoGui);
        Assert.True(c.Conflict);
        Assert.True(c.NeedsConsole);
    }

    [Fact]
    public void 未知参数被收集_且不影响已知参数()
    {
        var c = CliOptions.Parse(["--no-gui", "--bogus", "-x"]);

        Assert.True(c.NoGui);
        Assert.Equal(["--bogus", "-x"], c.Unknown);
        Assert.True(c.NeedsConsole);   // 参数错了也要让人看见
    }

    [Fact]
    public void 空串与空白参数被忽略()
    {
        var c = CliOptions.Parse(["", "   ", null!]);

        Assert.Empty(c.Unknown);
        Assert.False(c.NeedsConsole);
    }

    [Fact]
    public void 帮助文本必须覆盖全部受支持选项()
    {
        // 文档与实现漂移是 CLI 最常见的腐坏方式：新增开关忘了写进 --help，用户就只能靠猜
        foreach (var flag in new[] { "--gui", "--no-gui", "--help", "--version" })
            Assert.Contains(flag, CliOptions.Usage);
    }
}
