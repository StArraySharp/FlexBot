namespace FlexBot.PluginApi;

/// <summary>一次编译的结果：成功时给出程序集字节（与可选符号），失败时给出可读诊断（精确到 文件(行,列)）。</summary>
public sealed record PluginCompileResult(bool Ok, byte[]? AssemblyBytes, byte[]? SymbolBytes, string Diagnostics);

/// <summary>
/// 宿主编译服务：把 C# 源码编译为内存程序集。
///
/// 供"插件工厂"这类需要动态产出代码的插件使用 —— 插件运行在自己的 ALC 里，
/// 拿不到宿主内部的编译器；把编译作为宿主能力暴露，全进程只有一份 Roslyn。
///
/// 契约层与协议库（FlexBot.Abstractions / OneBotLib）由宿主自动加入引用，
/// 其余依赖由 referencePaths 指定（通常是插件目录 libs/ 下的 DLL）。
/// </summary>
public interface ICompilerService
{
    /// <param name="sourcePaths">参与编译的源码文件绝对路径（*.cs）</param>
    /// <param name="referencePaths">额外引用的程序集路径（可为 null）</param>
    /// <param name="assemblyName">产物程序集名</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<PluginCompileResult> CompileAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string>? referencePaths = null,
        string assemblyName = "DynamicPlugin",
        CancellationToken cancellationToken = default);
}
