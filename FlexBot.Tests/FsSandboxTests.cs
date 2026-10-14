using System;
using System.IO;
using Zio;
using Zio.FileSystems;
using FlexBot.BuiltInPlugins.FileSystem;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// fs 插件的 Zio 沙箱：越界类输入必须全部被拒。
/// 用例直接取材自 GHSA-h39g-6x3c-7fq9（SubFileSystem 的 /../ 越界）与我们自己在 zip 守卫里
/// 抓到过的反斜杠穿越（..\）—— 同一类坑，必须在沙箱这一层有回归测试。
/// </summary>
public class FsSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fb-sbx-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FsSandbox _sb;

    public FsSandboxTests() => _sb = new FsSandbox(_root);
    public void Dispose() { try { _sb.Dispose(); Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("/../")]        // GHSA-h39g-6x3c-7fq9 的原始 PoC 输入
    [InlineData("/..\\")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../../b")]
    [InlineData("..\\..\\windows\\win.ini")]
    [InlineData("C:/Windows/system32/evil.dll")]
    [InlineData("name:stream.txt")]
    public void 越界或非法路径一律拒绝(string bad)
        => Assert.False(_sb.TryResolve(bad, out _), bad);

    [Fact]
    public void 正常相对路径解析到沙箱之内()
    {
        Assert.True(_sb.TryResolve("a/b.txt", out var p));
        var full = Path.GetFullPath(_sb.Fs.ConvertPathToInternal(p));
        Assert.StartsWith(_sb.Root, full, StringComparison.OrdinalIgnoreCase);
        Assert.True(_sb.TryResolve("/etc/passwd", out var p2));   // 前导斜杠按相对处理，仍落在沙箱内
        var full2 = Path.GetFullPath(_sb.Fs.ConvertPathToInternal(p2));
        Assert.StartsWith(_sb.Root, full2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 写入与读取都在沙箱内完成()
    {
        Assert.True(_sb.TryResolve("sub/hello.txt", out var p));
        _sb.Fs.CreateDirectory(UPath.Root / "sub");
        _sb.Fs.WriteAllText(p, "内容");
        Assert.Equal("内容", _sb.Fs.ReadAllText(p));
        Assert.True(File.Exists(Path.Combine(_root, "sub", "hello.txt")));   // 物理上就在根目录下
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "hello.txt")));
    }
    [Fact]
    public void 前导斜杠按沙箱内路径处理()
    {
        // 与 fs_list 显示 [/] 的语义一致：/a/b.txt 就是沙箱根下的 a/b.txt，而不是宿主的绝对路径
        Assert.True(_sb.TryResolve("/a/b.txt", out var p1));
        Assert.True(_sb.TryResolve("a/b.txt", out var p2));
        Assert.Equal(p2.FullName, p1.FullName);
        var full = Path.GetFullPath(_sb.Fs.ConvertPathToInternal(p1));
        Assert.StartsWith(_sb.Root, full, StringComparison.OrdinalIgnoreCase);
    }
}