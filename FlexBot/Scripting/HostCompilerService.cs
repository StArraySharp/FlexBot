using FlexBot.PluginApi;

namespace FlexBot.Scripting;

/// <summary>
/// 宿主对插件暴露的编译服务实现：内部复用 ScriptCompiler 的编译器前端，
/// 因此全进程只有一份 Roslyn（插件不需要自带编译器，也不需要装 SDK）。
/// </summary>
sealed class HostCompilerService : ICompilerService
{
    public async Task<PluginCompileResult> CompileAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string>? referencePaths = null,
        string assemblyName = "DynamicPlugin",
        CancellationToken cancellationToken = default)
    {
        var (ok, pe, pdb, diag) = await ScriptCompiler.CompileToAssemblyAsync(
            sourcePaths, referencePaths, assemblyName, cancellationToken);
        return new PluginCompileResult(ok, pe, pdb, diag);
    }
}
