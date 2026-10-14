using FlexBot;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// ⑤ 安全测试：zip 安装的路径守卫（防目录穿越 / 绝对路径 / 盘符与 ADS 冒号）。
/// 直接测守卫本身 —— 它与目标目录无关，是安装流程的前置判定。
/// 用例依据实现原文：空串、以 / 开头、含冒号、Path.IsPathRooted、任一 .. 段。
/// </summary>
public class PluginZipPathGuardTests
{
    [Theory]
    [InlineData("../evil.txt", true)]
    [InlineData("..\\evil.txt", true)]           // 反斜杠：Windows 上会被视作分隔，.. 段同样拦截（含冒号规则兜底）
    [InlineData("a/../../evil.txt", true)]
    [InlineData("..", true)]
    [InlineData("/etc/passwd", true)]
    [InlineData("C:/Windows/system32/evil.dll", true)]
    [InlineData("name:stream.txt", true)]        // ADS / 盘符冒号
    [InlineData("", true)]
    [InlineData("plugin.json", false)]
    [InlineData("src/main.cs", false)]
    [InlineData("a/b/c/d.md", false)]
    public void 越界或不安全路径一律拦截(string rel, bool unsafePath)
        => Assert.Equal(unsafePath, PluginManager.IsUnsafeRelPath(rel));

    [Fact]
    public void 正常插件包内路径全部放行()
    {
        foreach (var ok in new[] { "src", "src/Main.cs", "plugin.json", "backups/x.bak", "libs/a.dll" })
            Assert.False(PluginManager.IsUnsafeRelPath(ok), ok);
    }
}
