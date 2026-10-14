using System;
using System.IO;
using Zio;
using Zio.FileSystems;

namespace FlexBot.BuiltInPlugins.FileSystem;

/// <summary>
/// fs 插件的文件沙箱（Zio 虚拟文件系统）。
///
/// 三级防线：1) 自己的相对路径检查（拒绝 .. 段与冒号）；2) Zio SubFileSystem 的结构化约束
/// （0.24.0 >= 0.22.2，已含 GHSA-h39g-6x3c-7fq9 的 /../ 越界修复）；3) 转换回物理路径后再断言
/// 仍落在根目录内。任何一层挡住就越界失败，不依赖单一机制的实现细节。
/// </summary>
public sealed class FsSandbox : IDisposable
{
    private readonly PhysicalFileSystem _physical = new();

    public FsSandbox(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        // subPath 必须在"委托 FS 自己的坐标空间"里 —— 别手写路径形式，交给 Zio 自己转换（与 ConvertPathToInternal 互为逆）
        var subUPath = _physical.ConvertPathFromInternal(Root);
        Fs = new SubFileSystem(_physical, subUPath);


    }

    public string Root { get; }
    public IFileSystem Fs { get; }

    /// <summary>相对路径 -> 沙箱内 UPath；越界或非法返回 false（不向调用方抛异常）</summary>
    public bool TryResolve(string? relative, out UPath path)
    {
        path = UPath.Root;
        var rel = (relative ?? "").Trim((char)34).Trim();
        rel = rel.Replace("\\", "/").TrimStart("/".ToCharArray());
        if (rel.Length == 0) return true;
        foreach (var seg in rel.Split("/", StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..") return false;
            if (seg.Contains(":")) return false;
        }
        var candidate = UPath.Root / rel;
        try
        {
            var internalPath = Fs.ConvertPathToInternal(candidate);
            var full = Path.GetFullPath(internalPath);   // ConvertPathToInternal 返回物理路径字符串
            var ok = string.Equals(full, Root, StringComparison.OrdinalIgnoreCase)
                     || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     || full.StartsWith(Root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!ok) return false;
        }
        catch { return false; }
        path = candidate;
        return true;
    }

    public void Dispose()
    {
        try { Fs.Dispose(); } catch { }
        try { _physical.Dispose(); } catch { }
    }
}
