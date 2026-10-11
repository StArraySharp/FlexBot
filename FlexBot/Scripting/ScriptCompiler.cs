using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace FlexBot.Scripting;

/// <summary>一次脚本编译的结果：成功时给出 PE（与可选 PDB）字节，失败时给出可读诊断。</summary>
public sealed record ScriptBuild(bool Ok, byte[]? Pe, byte[]? Pdb, string Diagnostics, bool FromCache);

/// <summary>
/// csx 插件编译前端：把 plugins/&lt;名&gt;/ 下的全部 *.cs 编译成一个内存程序集，
/// 交给宿主既有的影子目录 + PluginLoadContext 机制加载 —— 与编译好的 DLL 插件走完全相同的生命周期。
///
/// 引用集 = 框架（TPA） + 宿主目录的 FlexBot.Abstractions.dll / OneBotLib.dll + 插件自有 libs/。
/// 脚本看不到宿主内部类型：契约层被剥离成独立程序集后，脚本能碰到的宿主世界就只有契约本身。
///
/// 产物按「源码内容 + 引用集指纹 + 编译选项版本」缓存到插件目录的 .cache/，
/// 所以进程重启后的加载是读文件（毫秒级）而不是重新编译。
/// </summary>
public static class ScriptCompiler
{
    /// <summary>缓存目录名（位于插件目录下，不参与源码枚举）</summary>
    public const string CacheDirName = ".cache";

    /// <summary>编译选项版本：改动编译参数/隐式 using 时递增，使旧缓存自动失效</summary>
    private const int CompileOptionsVersion = 1;

    /// <summary>
    /// 脚本的隐式全局 using：与宿主项目的 ImplicitUsings=enable 对齐，
    /// 脚本里无需重复写 System / Task / Linq 等常用命名空间。
    /// </summary>
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static readonly CSharpCompilationOptions CompileOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        nullableContextOptions: NullableContextOptions.Enable,
        deterministic: true);

    private static readonly Lazy<List<MetadataReference>> FrameworkRefs = new(() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    /// <summary>
    /// 不参与编译的首层子目录（与 PluginBuilder 的目录约定保持一致）。
    /// 尤其是 backups/：源码快照就在插件目录内，若不排除会被当源码二次编译 → CS0101 重复定义。
    /// </summary>
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "libs",     // 第三方 DLL（只作编译引用与运行时解析）
        "backups",  // PluginBuilder 的源码快照
        "bin", "obj"
    };

    /// <summary>枚举参与编译的源码：递归全部 *.cs，跳过 .cache / 点开头目录 / libs / backups</summary>
    public static List<string> SourceFiles(string pluginDir)
    {
        if (!Directory.Exists(pluginDir)) return [];
        return Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(pluginDir, f);
                var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return !first.StartsWith('.') && !ExcludedDirs.Contains(first);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>该目录是否是「脚本插件」（有 *.cs 源码）</summary>
    public static bool IsScriptPlugin(string pluginDir) => SourceFiles(pluginDir).Count > 0;

    /// <summary>编译期引用集：框架 + 契约层 + 协议库 + 插件自有 libs/**/*.dll</summary>
    public static List<MetadataReference> BuildReferences(string pluginDir)
    {
        var refs = new List<MetadataReference>(FrameworkRefs.Value);

        // 契约层与协议库：取宿主目录下的同一份文件，保证与运行时加载的是同一程序集身份
        foreach (var dll in new[] { "FlexBot.Abstractions.dll", "OneBotLib.dll" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }

        // 插件自带第三方依赖（libs/ 下的 DLL，运行时由 PluginLoadContext 从影子目录解析）
        var libsDir = Path.Combine(pluginDir, "libs");
        if (Directory.Exists(libsDir))
            foreach (var dll in Directory.EnumerateFiles(libsDir, "*.dll", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                refs.Add(MetadataReference.CreateFromFile(dll));

        return refs;
    }

    /// <summary>编译插件目录下的全部源码；命中缓存则直接返回缓存的程序集字节。</summary>
    public static async Task<ScriptBuild> CompileAsync(string pluginDir, string name, CancellationToken ct = default)
    {
        var sources = SourceFiles(pluginDir);
        if (sources.Count == 0)
            return new ScriptBuild(false, null, null, $"目录内没有 *.cs 源码: {pluginDir}", false);

        var refs = BuildReferences(pluginDir);
        var cacheDir = Path.Combine(pluginDir, CacheDirName);
        var fingerprint = Fingerprint(sources, refs);
        var pePath = Path.Combine(cacheDir, $"{name}.{fingerprint}.dll");
        var pdbPath = Path.Combine(cacheDir, $"{name}.{fingerprint}.pdb");

        // ---- 缓存命中：直接读盘（重启/重载后的常态路径） ----
        if (File.Exists(pePath))
        {
            try
            {
                var pe = await File.ReadAllBytesAsync(pePath, ct);
                var pdb = File.Exists(pdbPath) ? await File.ReadAllBytesAsync(pdbPath, ct) : null;
                return new ScriptBuild(true, pe, pdb, "", true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[script] 读取编译缓存失败，将重新编译: {ex.Message}");
            }
        }

        // ---- 编译（CPU 密集，放到线程池） ----
        var (ok, peBytes, pdbBytes, diag) = await Task.Run(
            () => Compile(sources, refs, $"ScriptPlugin.{name}", ct), ct);
        if (!ok) return new ScriptBuild(false, null, null, diag, false);

        // ---- 落盘缓存（失败不影响本次加载） ----
        try
        {
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllBytesAsync(pePath, peBytes!, ct);
            if (pdbBytes is not null) await File.WriteAllBytesAsync(pdbPath, pdbBytes, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[script] 写入编译缓存失败（忽略）: {ex.Message}");
        }
        return new ScriptBuild(true, peBytes, pdbBytes, diag, false);
    }

    /// <summary>
    /// 把指定源码文件编译为内存程序集（供 ICompilerService / 插件工厂复用；不做缓存、不写盘）。
    /// 契约层与协议库自动加入引用，其余由 referencePaths 提供。
    /// </summary>
    public static Task<(bool Ok, byte[]? Pe, byte[]? Pdb, string Diagnostics)> CompileToAssemblyAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string>? referencePaths,
        string assemblyName,
        CancellationToken ct = default)
    {
        var refs = new List<MetadataReference>(FrameworkRefs.Value);
        foreach (var dll in new[] { "FlexBot.Abstractions.dll", "OneBotLib.dll" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        if (referencePaths is not null)
            foreach (var r in referencePaths)
                if (!string.IsNullOrWhiteSpace(r) && File.Exists(r))
                    refs.Add(MetadataReference.CreateFromFile(r));

        var sources = sourcePaths.Where(File.Exists).ToList();
        if (sources.Count == 0)
            return Task.FromResult<(bool, byte[]?, byte[]?, string)>((false, null, null, "没有可编译的源码文件"));

        return Task.Run(() => Compile(sources, refs, assemblyName, ct), ct);
    }

    private static (bool Ok, byte[]? Pe, byte[]? Pdb, string Diagnostics) Compile(
        List<string> sources, List<MetadataReference> refs, string assemblyName, CancellationToken ct)
    {
        var trees = new List<SyntaxTree>
        {
            // 隐式 using 单独成树：脚本源码不必重复声明常用命名空间。
            // 注意必须用 SourceText 并显式给出编码：否则生成 PDB 时报 CS8055
            //（"无法在不进行编码的情况下发出源文本的调试信息"）。
            CSharpSyntaxTree.ParseText(
                SourceText.From(ImplicitUsings, Encoding.UTF8), ParseOptions,
                path: "<implicit-usings>.cs", cancellationToken: ct)
        };
        trees.AddRange(sources.Select(f => CSharpSyntaxTree.ParseText(
            SourceText.From(File.ReadAllText(f), Encoding.UTF8), ParseOptions,
            path: f, cancellationToken: ct)));

        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: trees,
            references: refs,
            options: CompileOptions);

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = compilation.Emit(
            peStream,
            pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: ct);

        if (emit.Success)
            return (true, peStream.ToArray(), pdbStream.ToArray(), "");

        // 诊断按「文件(行,列): 级别 编号: 消息」输出 —— 用户与 AI 都能直接定位到源码行
        var diagnostics = string.Join("\n", emit.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .Select(d => "  " + d.ToString()));
        return (false, null, null, diagnostics);
    }

    // 指纹 = 源码内容 + 引用集（路径/时间戳/大小） + 编译选项版本
    private static string Fingerprint(List<string> sources, List<MetadataReference> refs)
    {
        var sb = new StringBuilder();
        sb.Append("v").Append(CompileOptionsVersion).Append('\n');
        foreach (var f in sources)
        {
            sb.Append(Path.GetFullPath(f)).Append('\n');
            using var stream = File.OpenRead(f);
            sb.Append(Convert.ToHexString(SHA256.HashData(stream))).Append('\n');
        }
        foreach (var r in refs)
        {
            var p = (r as PortableExecutableReference)?.FilePath;
            if (p is null) continue;
            var fi = new FileInfo(p);
            sb.Append(p).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }
}
