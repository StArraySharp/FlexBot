using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;

namespace PluginBuilderPlugin;

/// <summary>
/// PluginBuilder（v3：双通道 —— 脚本插件免编译 / 编译型插件走 dotnet build）。
///
/// ① 脚本型（推荐，免编译、免 SDK、秒级热重载）：
///    plugins/&lt;名&gt;/main.cs + plugin.json    宿主用 Roslyn 内存编译（编译型产物走 .cache/）
///    工作流：pb_build script-init → fs_write 到 _pb_stage/&lt;名&gt;/ → pb_build script
///
/// ② 编译型（需要第三方 NuGet、或想分发独立 dll）——默认由宿主内存编译，部署机无需装 SDK：
///    plugins/&lt;名&gt;/src/&lt;名&gt;.csproj + *.cs   标准项目（AI fs_write 直接写这里，多文件随意）
///    plugins/&lt;名&gt;/&lt;名&gt;.dll                 构建产物（csproj OutDir 直落插件根）
///    工作流：pb_build init → fs_write → create → 报错回喂 → update
///
/// 两者共用：plugin.json（Depends 依赖 + Build 档案）、backups/ 源快照、错误回喂 AI。
/// </summary>
public sealed class PluginBuilderPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;

    public string Name => "PluginBuilder";
    public string Version => "3.1.0";
    public string Description => "AI 插件工厂 v3.1：脚本插件免编译直载 / 编译型默认宿主内存编译（无需 SDK）、依赖、备份、错误回喂";

    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("BuildBackend", "编译型插件构建后端", "select", "roslyn",
            "roslyn = 宿主内存编译（默认，无需 dotnet SDK，诊断精确到行列）；dotnet = 子进程 dotnet build（支持 NuGet，部署机需装 SDK）",
            ["roslyn", "dotnet"]),
        new("BuildTimeoutSec", "dotnet 构建超时秒", "number", "120", "仅 dotnet 后端有效：dotnet build 子进程超时"),
        new("KeepBuildArtifacts", "保留构建中间目录", "bool", "false", "仅 dotnet 后端有效：true = 保留 src/obj 与 src/bin（调试）"),
    ];

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;
        context.RegisterCommand("pb_build", "PluginBuilder 统一入口（init/create/update/compile/read/list/deps/delete）", a => BuildAsync(a),
            "pb_build <init|create|update|compile|read|list|deps|delete> <名> …");
        context.RegisterCommand("pb_list", "列出已生成插件", _ => ListGeneratedAsync());
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync() { _ctx = null!; return Task.CompletedTask; }
    public Task OnSettingsChangedAsync() => Task.CompletedTask;

    // ===================== 路径 =====================

    private static readonly Regex ShadowDirRegex = new(@"PluginBuilder_\d+$", RegexOptions.Compiled);

    private string PluginsRoot => Path.GetFullPath(Path.Combine(_ctx.PluginDir,
        ShadowDirRegex.IsMatch(Path.GetFileName(_ctx.PluginDir.TrimEnd(Path.DirectorySeparatorChar)))
            ? Path.Combine("..", "..") : ".."));

    private string HostBinDir => Path.GetFullPath(Path.Combine(PluginsRoot, ".."));
    private string TargetRoot(string name) => Path.Combine(PluginsRoot, name);
    private string SrcDir(string name) => Path.Combine(TargetRoot(name), "src");
    private string CsprojPath(string name) => Path.Combine(SrcDir(name), name + ".csproj");
    private string MainSourcePath(string name) => Path.Combine(SrcDir(name), name + ".cs");
    private string ManifestPath(string name) => Path.Combine(TargetRoot(name), "plugin.json");
    private string DllPath(string name) => Path.Combine(TargetRoot(name), name + ".dll");
    private string MainScriptPath(string name) => Path.Combine(TargetRoot(name), "main.cs");
    // AI 审计暂存（点开头目录：不参与插件扫描），create/update 时归档进插件目录
    private string PendingPath(string name) => Path.Combine(PluginsRoot, ".pending", name + ".llm.json");
    private string BackupRoot(string name) => Path.Combine(TargetRoot(name), "backups");

    private static readonly Regex NameRegex = new("^[A-Za-z][A-Za-z0-9_]{1,30}$", RegexOptions.Compiled);
    private string? ValidateName(string name) =>
        !NameRegex.IsMatch(name) ? $"插件名非法：{name}（字母开头，字母/数字/下划线，2-31 字符）"
        : name.Equals("PluginBuilder", StringComparison.OrdinalIgnoreCase) ? "不能用 PluginBuilder 自己"
        : null;

    // 名字合法且 csproj 存在才放行；错误写入 out err
    private bool CheckReady(string name, out string err)
    {
        err = ValidateName(name) ?? (File.Exists(CsprojPath(name)) ? "" : $"插件 {name} 不存在（先 pb_build init）");
        return err.Length == 0;
    }

    /// <summary>不参与编译的首层目录（与宿主 ScriptCompiler.ExcludedDirs 对齐；点开头目录另行排除）</summary>
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "backups", "libs", "bin", "obj"
    };

    // 插件根下的脚本源码（排除点开头目录（.cache/.shadow）、backups、libs、bin、obj）
    private List<string> ScriptSources(string name)
    {
        var root = TargetRoot(name);
        if (!Directory.Exists(root)) return [];
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f);
                var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return !first.StartsWith('.') && !ExcludedSegments.Contains(first);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool HasScriptSource(string name) => ScriptSources(name).Count > 0;

    // 脚本型插件的判定必须与宿主一致：无 <名>.dll、无 src/<名>.csproj，且有根级 *.cs
    // （编译型插件的源码也在目录内，只看 "有没有 .cs" 会把它误判成脚本型）
    private bool IsScriptPlugin(string name) =>
        !File.Exists(DllPath(name)) && !File.Exists(CsprojPath(name)) && HasScriptSource(name);

    // 脚本型插件就绪检查：确实是脚本型（无 dll、无 csproj，且有根级 *.cs）
    private bool CheckReadyScript(string name, out string err)
    {
        err = ValidateName(name) ?? (IsScriptPlugin(name) ? "" : $"不是脚本型插件 {name}（不存在，或已是编译型；先 pb_build script-init {name}）");
        return err.Length == 0;
    }

    // 任一形态存在即可（read / list / deps 用）
    private bool ExistsAny(string name) =>
        File.Exists(CsprojPath(name)) || HasScriptSource(name) || File.Exists(DllPath(name));

    // ===================== 命令分发 =====================

    private async Task<string> BuildAsync(string args)
    {
        var parts = args.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return """
                用法: pb_build <动作> <名> […]
                ── 脚本型（免编译，推荐）──
                script-init <名>       生成脚本骨架（plugins/<名>/main.cs + plugin.json）
                script <名> <描述>     从暂存 fs:_pb_stage/<名>/*.cs 写入并加载
                script-update <名>     重新拉取暂存源码 → 热重载（宿主重新编译）
                ── 编译型（默认宿主内存编译，无需 SDK；可切 dotnet 支持 NuGet）──
                init <名>              生成骨架（csproj + 示例主类）
                write <名> <相对路径>  从暂存 fs:_pb_stage/<名>/<相对路径> 写入 src/<相对路径>
                create <名> <描述>     写 plugin.json + 构建 + 加载
                update <名>            备份 → 构建 → 热重载（每轮修改前自动备份）
                compile <名>           只构建验证
                ── 通用 ──
                read <名>              列出文件树
                readsrc <名> <相对路径> 读单文件内容
                log <名> [行数]        查看该插件的生成日志（默认最近 20 条）
                llm <名> [序号] [页码] 查看 AI 生成审计：完整工具调用参数与源码
                audit <名> <base64>    内部：工具桥暂存本次 AI 调用审计（base64 防转义破坏）
                deps <名> <A,B>        设置依赖插件（运行时加载顺序；编译型额外加编译引用）
                list                   列出全部生成插件
                delete <名>            卸载并删除全部目录
                编译型后端由设置 BuildBackend 决定：roslyn（默认，宿主内存编译）/ dotnet（子进程，支持 NuGet）
                """;
        var action = parts[0].ToLowerInvariant();
        var name = parts.Length > 1 ? parts[1] : "";
        var rest = parts.Length > 2 ? parts[2].Trim() : "";

        switch (action)
        {
            case "init": return InitProject(name);
            case "script-init": return InitScript(name);
            case "script": return await ScriptCreateAsync(name, rest.Length > 0 ? rest : "AI 生成的脚本插件");
            case "script-update": return await ScriptUpdateAsync(name);
            case "write": return StageWrite(name, rest);
            case "create": return await CreateAsync(name, rest.Length > 0 ? rest : "AI 生成插件");
            case "update": return await UpdateAsync(name);
            case "compile":
            {
                // 自动识别形态：脚本型只做纯编译校验，不加载
                if (IsScriptPlugin(name))
                    return await CompileScriptAsync(name);
                if (!CheckReady(name, out var e)) return e;
                var (ok, log) = await BuildProjectAsync(name);
                return ok ? $"构建成功\n{log}" : $"构建失败（修 src/ 后 pb_build update 再试）:\n{log}";
            }
            case "read": return ReadTree(name);
            case "readsrc": return ReadSrc(name, rest);
            case "audit": return RecordAudit(name, rest);
            case "log": return ReadGenLog(name, rest);
            case "llm":
            {
                var lp = (rest ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var pg = lp.Length > 1 && int.TryParse(lp[1], out var q) ? q : 1;
                return ReadLlmLog(name, lp.Length > 0 ? lp[0] : "", pg);
            }
            case "deps": return SetDeps(name, rest);
            case "list": return await ListGeneratedAsync();
            case "delete": return await DeleteAsync(name);
            default: return $"未知动作 {action}";
        }
    }

    // 暂存目录（FileSystem 沙箱）：AI fs_write 到 _pb_stage/<任意名> 后经此动作写进目标。
    // 注意 Root 是 FileSystem 插件的设置且按插件隔离，不能用 _ctx.GetSetting 读自己，只能读它的 settings.json。
    private string StageRoot()
    {
        var fsRoot = FileSystemRoot();
        if (string.IsNullOrWhiteSpace(fsRoot)) fsRoot = Path.Combine(_ctx.Config.MemoryDir, "fs");
        return Path.Combine(Path.GetFullPath(fsRoot), "_pb_stage");
    }

    private string FileSystemRoot()
    {
        try
        {
            var path = Path.Combine(PluginsRoot, "FileSystem", "settings.json");
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("Root", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private string StageWrite(string name, string relPath)
    {
        if (!CheckReady(name, out var e)) return e;
        if (relPath.Length == 0) return "用法: pb_build write <名> <src内相对路径>（源先 fs_write 到 _pb_stage/<路径同名>）";
        // 源暂存路径 = _pb_stage/<名>/<相对路径>（按插件分组避免互踩）
        var staged = Path.GetFullPath(Path.Combine(StageRoot(), name, relPath));
        if (!File.Exists(staged))
            return $"暂存不存在：fs:_pb_stage/{name}/{relPath}（AI 先 fs_write 该路径）";
        // 目标 = src/<相对路径>（拒绝越界）
        var srcRoot = Path.GetFullPath(SrcDir(name));
        var target = Path.GetFullPath(Path.Combine(srcRoot, relPath));
        if (!target.StartsWith(srcRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return $"拒绝：相对路径「{relPath}」越界（只能在 src/ 内）";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(staged, target, true);
        return $"已写入 {name}/src/{relPath}（{new FileInfo(staged).Length}B）\n全部写完执行：pb_build update {name}";
    }

    private string ReadSrc(string name, string relPath)
    {
        var vErr = ValidateName(name);
        if (vErr is not null) return vErr;
        if (!ExistsAny(name)) return $"插件 {name} 不存在（编译型先 pb_build init，脚本型先 pb_build script-init）";
        if (relPath.Trim().Length == 0)
            return "用法: pb_build readsrc <名> <相对路径>（编译型相对 src/，脚本型相对插件根）";

        // 两种形态的源码根不同：编译型在 src/ 下，脚本型在插件根（多文件脚本可带子目录）
        foreach (var root in new[] { SrcDir(name), TargetRoot(name) })
        {
            var rootFull = Path.GetFullPath(root);
            var target = Path.GetFullPath(Path.Combine(rootFull, relPath.Trim()));
            if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(target)) return File.ReadAllText(target);
        }
        return $"不存在: {relPath}（编译型相对 src/，脚本型相对插件根）";
    }

    // ===================== init =====================

    private string InitProject(string name)
    {
        var e = ValidateName(name);
        if (e is not null) return e;
        if (File.Exists(CsprojPath(name))) return $"已存在（直接改文件后 pb_build create）：{CsprojPath(name)}";
        Directory.CreateDirectory(SrcDir(name));

        var hostBin = HostBinDir;
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AssemblyName>{name}</AssemblyName>
                <RootNamespace>{name}</RootNamespace>
                <!-- 产物直落插件根（plugins/名/名.dll）；正斜杠跨平台 -->
                <OutDir>../</OutDir>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
              </PropertyGroup>

              <ItemGroup>
                <!-- 契约层（FlexBot.Abstractions）+ 协议库：Private=false 不拷贝，运行时由宿主 ALC 共享同一份 -->
                <Reference Include="FlexBot.Abstractions">
                  <HintPath>{Path.Combine(hostBin, "FlexBot.Abstractions.dll")}</HintPath>
                  <Private>false</Private>
                </Reference>
                <Reference Include="OneBotLib">
                  <HintPath>{Path.Combine(hostBin, "OneBotLib.dll")}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>

            </Project>
            """;

        var main = new StringBuilder()
            .AppendLine($"// {name} —— AI 生成插件（多文件：src/ 下全部 .cs 自动参与编译）")
            .AppendLine("using System.Threading.Tasks;")
            .AppendLine("using FlexBot.PluginApi;")
            .AppendLine()
            .AppendLine($"namespace {name};")
            .AppendLine()
            .AppendLine($"public sealed class {name}Plugin : IBotPlugin")
            .AppendLine("{")
            .AppendLine($"    public string Name => \"{name}\";")
            .AppendLine("    public string Version => \"1.0.0\";")
            .AppendLine("    public string Description => \"AI 生成插件\";")
            .AppendLine()
            .AppendLine("    public Task OnLoadAsync(IBotContext ctx)")
            .AppendLine("    {")
            .AppendLine($"        ctx.RegisterCommand(\"{name.ToLowerInvariant()}\", \"示例命令\", a => Task.FromResult(\"参数: \" + a));")
            .AppendLine("        return Task.CompletedTask;")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    public Task OnUnloadAsync() => Task.CompletedTask;")
            .AppendLine("}")
            .ToString();

        File.WriteAllText(CsprojPath(name), csproj);
        File.WriteAllText(MainSourcePath(name), main);
        LogGen(name, "init", "ok", "csproj + 示例主类已生成（编译型）");
        return $"骨架已生成：\n{CsprojPath(name)}\n{MainSourcePath(name)}\n改源码（fs_write）后：pb_build create {name} <描述>";
    }

    // ===================== create / update =====================

    private async Task<string> CreateAsync(string name, string description)
    {
        var err = ValidateName(name);
        if (err is not null) return err;
        if (!File.Exists(CsprojPath(name))) return $"csproj 不存在（先 pb_build init {name}）";
        if (File.Exists(ManifestPath(name))) return $"已注册（迭代用 pb_build update {name}）";

        await WriteManifestAsync(name, description);
        var llmNote = ConsumeLlmSidecar(name);
        var (ok, log) = await BuildProjectAsync(name);
        if (!ok) return $"plugin.json 已写，但构建失败（修 src/ 后 update）：\n{log}";
        await TryLoadAsync(name);
        return $"插件 {name} 构建成功并尝试加载。\n{log}"
             + (llmNote is null ? "" : $"\nAI 审计: {llmNote}（!pb_build llm {name} 查看完整记录）");
    }

    private async Task<string> UpdateAsync(string name)
    {
        var vErr = ValidateName(name);
        if (vErr is not null) return vErr;

        // 自动识别形态：脚本型走免编译热重载，其余走编译型构建
        if (IsScriptPlugin(name))
            return await ScriptUpdateAsync(name);

        if (!CheckReady(name, out var e)) return e;
        if (!File.Exists(ManifestPath(name))) return "未注册（先 pb_build create）";

        BackupSource(name);

        var llmNote = ConsumeLlmSidecar(name);
        var (ok, log) = await BuildProjectAsync(name);
        if (!ok) return $"已备份旧源码，但构建失败（继续修后再 update）：\n{log}";
        await TryReloadAsync(name);
        return $"已更新、构建成功并热重载。\n{log}"
             + (llmNote is null ? "" : $"\nAI 审计: {llmNote}（!pb_build llm {name} 查看完整记录）");
    }

    // 脚本型：用宿主编译服务做一次纯校验（不加载、不写盘）——AI 改完可先 self-check
    private async Task<string> CompileScriptAsync(string name)
    {
        var sources = ScriptSources(name);
        if (sources.Count == 0) return $"插件根下没有 *.cs（先 pb_build script-init {name}）";
        var refs = DependencyDlls(name).Concat(LibsDlls(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            var r = await _ctx.Compiler.CompileAsync(sources, refs, name);
            BumpBuild(name, r.Ok ? "ok" : "error", r.Ok ? $"编译校验通过（{sources.Count} 文件，{r.AssemblyBytes!.Length}B）" : r.Diagnostics);
            return r.Ok
                ? $"编译校验通过（{sources.Count} 个源文件，{r.AssemblyBytes!.Length}B；脚本型无需落盘，加载时宿主会再编译一次并命中缓存）"
                : $"编译失败:\n{TrimLog(r.Diagnostics)}";
        }
        catch (Exception ex)
        {
            BumpBuild(name, "error", ex.Message);
            return $"编译校验异常: {ex.Message}";
        }
    }

    // ===================== deps：插件间依赖 =====================

    private string SetDeps(string name, string depsCsv)
    {
        var vErr = ValidateName(name);
        if (vErr is not null) return vErr;
        if (!ExistsAny(name)) return $"插件 {name} 不存在（编译型先 init，脚本型先 script-init）";

        var deps = depsCsv.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var d in deps)
        {
            var err = ValidateName(d);
            if (err is not null) return $"依赖名非法：{err}";
            var depDir = Path.Combine(PluginsRoot, d);
            if (!File.Exists(Path.Combine(depDir, d + ".dll")) && !File.Exists(Path.Combine(depDir, "main.cs")))
                return $"依赖插件 {d} 不存在（plugins/{d}/ 下既无 {d}.dll 也无 main.cs）";
        }

        // manifest：Depends（宿主运行时按序加载 + ALC 借用）
        var mf = ReadManifest(name);
        mf["Depends"] = JsonSerializer.SerializeToElement(deps);
        File.WriteAllText(ManifestPath(name), JsonSerializer.Serialize(mf, BotJson.Indented));

        // 脚本型没有 csproj：只写 Depends（宿主负责加载顺序与 ALC 借用），不存在编译期引用
        if (!File.Exists(CsprojPath(name)))
        {
            BumpBuild(name, "deps", string.Join(",", deps));
            return deps.Count == 0
                ? $"已清空 {name} 依赖"
                : $"已设 {name} 依赖 [{string.Join(", ", deps)}]（脚本型：仅运行时加载顺序与 ALC 借用，无编译期引用）";
        }

        // csproj：替换 PB-DEPS 标记块（编译期引用依赖插件 dll）
        var csproj = File.ReadAllText(CsprojPath(name));
        csproj = Regex.Replace(csproj, @"\s*<!--PB-DEPS-->.*?<!--/PB-DEPS-->", "", RegexOptions.Singleline);
        if (deps.Count > 0)
        {
            var refs = string.Concat(deps.Select(d => $"""
                    <Reference Include="{d}">
                      <HintPath>{Path.Combine(PluginsRoot, d, d + ".dll")}</HintPath>
                      <Private>false</Private>
                    </Reference>
                """));
            csproj = csproj.Replace("</Project>", $"""
                  <ItemGroup><!--PB-DEPS--> 插件依赖（pb_build deps 维护）
                {refs}<!--/PB-DEPS--></ItemGroup>

                </Project>
                """.Replace("                ", ""));
        }
        File.WriteAllText(CsprojPath(name), csproj);

        BumpBuild(name, "deps", string.Join(",", deps));
        return deps.Count == 0
            ? $"已清空 {name} 依赖（下次 update 生效）"
            : $"已设 {name} 依赖 [{string.Join(", ", deps)}]（编译引用+运行时加载序），下次 update 生效";
    }

    // ===================== 构建核心 =====================

    // 后端选择：roslyn = 宿主内存编译（默认，无需 SDK）；dotnet = 子进程 dotnet build（支持 NuGet）
    private async Task<(bool Ok, string Log)> BuildProjectAsync(string name)
    {
        var backend = _ctx.GetSetting("BuildBackend", "roslyn");
        return string.Equals(backend, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? await BuildWithDotnetAsync(name)
            : await BuildWithRoslynAsync(name);
    }

    // 宿主内存编译：复用 ICompilerService（全进程一份 Roslyn，插件与部署机都不需要 SDK）
    private async Task<(bool Ok, string Log)> BuildWithRoslynAsync(string name)
    {
        var srcRoot = SrcDir(name);
        var sources = Directory.Exists(srcRoot)
            ? Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        if (sources.Count == 0)
            return (false, $"src/ 下没有 *.cs（先 pb_build init {name} 或 fs_write 源码）");

        // NuGet 依赖（plugin.json 的 NuGet 字段）先由宿主铺进 libs/，否则内存编译缺引用
        // （宿主命令是 plugin nuget；与下面 pb_build deps 的"插件间依赖"不是一回事）
        var depsMsg = await _ctx.TryInvokeCommandAsync("plugin", $"nuget {name}");
        if (!string.IsNullOrEmpty(depsMsg) && !depsMsg.Contains("未声明"))
            _ctx.Log.Info($"nuget {name}: {depsMsg.Replace("\n", " | ")}");

        // 引用：依赖插件的 dll（plugin.json Depends）+ 插件自带 libs/
        var refs = DependencyDlls(name).Concat(LibsDlls(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        PluginCompileResult result;
        try
        {
            result = await _ctx.Compiler.CompileAsync(sources, refs, name);
        }
        catch (Exception ex)
        {
            var msg = $"内存编译异常: {ex.Message}";
            BumpBuild(name, "error", msg);
            return (false, msg);
        }

        if (!result.Ok)
        {
            var diag = TrimLog(result.Diagnostics);
            BumpBuild(name, "error", diag);
            return (false, diag);
        }

        File.WriteAllBytes(DllPath(name), result.AssemblyBytes!);
        if (result.SymbolBytes is not null)
            File.WriteAllBytes(Path.Combine(TargetRoot(name), name + ".pdb"), result.SymbolBytes);

        var log = $"内存编译成功（roslyn，无需 SDK）：{sources.Count} 个源文件 → {name}.dll"
                + $"（{result.AssemblyBytes!.Length}B，外部引用 {refs.Count} 个 DLL）";
        BumpBuild(name, "ok", log);
        return (true, log);
    }

    // 依赖插件的 dll（编译期引用 + 运行期 ALC 借用）
    private IEnumerable<string> DependencyDlls(string name)
    {
        var mf = ReadManifest(name);
        if (!mf.TryGetValue("Depends", out var d) || d.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in d.EnumerateArray())
        {
            var dep = e.GetString();
            if (string.IsNullOrWhiteSpace(dep)) continue;
            var p = Path.Combine(PluginsRoot, dep, dep + ".dll");
            if (File.Exists(p)) yield return p;
        }
    }

    // 插件自带第三方依赖：插件根 libs/ 与 src/libs/
    private IEnumerable<string> LibsDlls(string name)
    {
        foreach (var dir in new[] { Path.Combine(TargetRoot(name), "libs"), Path.Combine(SrcDir(name), "libs") })
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
                    yield return f;
    }

    private static string TrimLog(string log)
    {
        var l = string.Join("\n", log.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0).Take(40));
        return l.Length > 3000 ? l[..3000] + "…" : (l.Length == 0 ? "(无诊断输出)" : l);
    }

    private async Task<(bool Ok, string Log)> BuildWithDotnetAsync(string name)
    {
        var timeout = Math.Max(30, _ctx.GetSetting("BuildTimeoutSec", 120));
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{CsprojPath(name)}\" -c Debug --nologo -v q",
            WorkingDirectory = SrcDir(name),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var exited = proc.WaitForExit(timeout * 1000);
        if (!exited)
        {
            try { proc.Kill(true); } catch { }
            return (false, $"构建超时（>{timeout}s）已终止。可能 NuGet 卡住，重试或调大 BuildTimeoutSec");
        }
        var output = await stdoutTask + await stderrTask;
        var ok = proc.ExitCode == 0;

        // 产物校验（OutDir 直落插件根）
        if (ok && !File.Exists(DllPath(name)))
        {
            ok = false;
            output += $"\n[PluginBuilder] ExitCode=0 但产物缺失：{DllPath(name)}";
        }

        // 清理中间目录
        if (ok && !_ctx.GetSetting("KeepBuildArtifacts", false))
            foreach (var d in new[] { Path.Combine(SrcDir(name), "obj"), Path.Combine(SrcDir(name), "bin") })
                try { Directory.Delete(d, true); } catch { }

        // 精简日志：去 restore 广告与空行，保留错误与警告
        var log = string.Join("\n", output.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .Take(40));
        if (log.Length > 3000) log = log[..3000] + "…";

        BumpBuild(name, ok ? "ok" : "error", ok ? null : log);
        return (ok, log.Length == 0 ? "(构建成功，无输出)" : log);
    }

    // ===================== 脚本型：免编译直载 =====================

    private string InitScript(string name)
    {
        var e = ValidateName(name);
        if (e is not null) return e;
        if (File.Exists(MainScriptPath(name))) return $"已存在：{MainScriptPath(name)}（改完直接 pb_build script-update {name}）";
        Directory.CreateDirectory(TargetRoot(name));

        var lower = name.ToLowerInvariant();
        var main = $$"""
            // {{name}} —— csx 脚本插件：宿主用 Roslyn 内存编译，无需 csproj / 无需 dotnet build。
            // 可用能力见 FlexBot.Abstractions 契约；System/Linq/Task/IO/Net.Http 已作为隐式 using 注入。
            // 改完执行：pb_build script-update {{name}}
            using FlexBot.PluginApi;

            namespace {{name}};

            public sealed class {{name}}Plugin : IBotPlugin
            {
                public string Name => "{{name}}";
                public string Version => "1.0.0";
                public string Description => "AI 生成的脚本插件";

                public Task OnLoadAsync(IBotContext ctx)
                {
                    ctx.RegisterCommand("{{lower}}", "示例命令", a => Task.FromResult($"参数: {a}"));
                    return Task.CompletedTask;
                }

                public Task OnUnloadAsync() => Task.CompletedTask;
            }
            """;

        File.WriteAllText(MainScriptPath(name), main);
        WriteScriptManifest(name, "AI 生成的脚本插件");
        LogGen(name, "script-init", "ok", "main.cs + plugin.json 已生成（脚本型）");
        return $"脚本骨架已生成（免编译）：\n{MainScriptPath(name)}\n"
             + $"改完执行 pb_build script-update {name}；或 fs_write 到 _pb_stage/{name}/ 后 pb_build script {name} <描述>";
    }

    // 从 _pb_stage/<名>/ 把全部 *.cs 拉到插件根（多文件脚本：插件根下任意 *.cs 都参与编译）
    private (bool Ok, string Info) PullStagedScripts(string name)
    {
        var stageDir = Path.Combine(StageRoot(), name);
        if (!Directory.Exists(stageDir))
            return (false, $"暂存目录不存在：fs:_pb_stage/{name}/（AI 先 fs_write 源码）");
        var files = Directory.GetFiles(stageDir, "*.cs").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return (false, $"暂存目录里没有 *.cs：fs:_pb_stage/{name}/");

        Directory.CreateDirectory(TargetRoot(name));
        var names = new List<string>();
        foreach (var f in files)
        {
            var target = Path.Combine(TargetRoot(name), Path.GetFileName(f));
            File.Copy(f, target, true);
            names.Add(Path.GetFileName(f));
        }
        return (true, string.Join(", ", names));
    }

    private async Task<string> ScriptCreateAsync(string name, string description)
    {
        var err = ValidateName(name);
        if (err is not null) return err;

        var (ok, info) = PullStagedScripts(name);
        if (!ok) return info;

        WriteScriptManifest(name, description);
        var llmNote = ConsumeLlmSidecar(name);   // 有 AI 审计旁车 → 留存完整记录并把来源改写为 ai
        var load = await TryLoadAsync(name);
        var okLoad = load.Contains("已加载插件");
        BumpBuild(name, okLoad ? "ok" : "error", load);   // 脚本通道也记构建档案 + 生成日志
        return $"脚本插件 {name} 已生成（源: {info}）\n{load}"
             + (llmNote is null ? "" : $"\nAI 审计: {llmNote}（!pb_build llm {name} 查看完整记录）")
             + $"\n加载失败时编译诊断会写进宿主日志与生成日志（精确到 文件(行,列)）；修完用 pb_build script-update {name}";
    }

    private async Task<string> ScriptUpdateAsync(string name)
    {
        if (!CheckReadyScript(name, out var e)) return e;

        var (ok, info) = PullStagedScripts(name);
        var note = ok ? $"已拉取暂存源码（{info}）" : "暂存为空，直接重载磁盘上的现有源码";
        BackupScript(name);
        var llmNote = ConsumeLlmSidecar(name);
        var reload = await TryReloadAsync(name);
        BumpBuild(name, reload.Contains("已重载插件") ? "ok" : "error", $"{note}；{reload}"); // 脚本通道也记档案+日志
        return $"{note}，热重载 {name}：\n{reload}"
             + (llmNote is null ? "" : $"\nAI 审计: {llmNote}（!pb_build llm {name} 查看）")
             + "\n宿主用 Roslyn 重新编译：源码变了才重编，未变则直接命中 .cache";
    }

    // 脚本型插件的 plugin.json：保留既有键（Depends/Settings/NuGet 由用户或 deps 维护）
    private void WriteScriptManifest(string name, string description)
    {
        var mf = ReadManifest(name);
        mf["name"] = JsonSerializer.SerializeToElement(name);
        mf["description"] = JsonSerializer.SerializeToElement(description);
        if (!mf.ContainsKey("Version")) mf["Version"] = JsonSerializer.SerializeToElement("1.0.0");
        if (!mf.ContainsKey("Depends")) mf["Depends"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
        if (!mf.ContainsKey("Settings")) mf["Settings"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
        if (!mf.ContainsKey("NuGet")) mf["NuGet"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
        // 来源标注：这里默认"手写"（pb_build 手工生成）；若本次带 AI 审计旁车，稍后会被改写成 ai
        if (!mf.ContainsKey("Provenance"))
            mf["Provenance"] = JsonSerializer.SerializeToElement(new
            {
                by = "manual",
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        mf["kind"] = JsonSerializer.SerializeToElement("script");
        Directory.CreateDirectory(TargetRoot(name));
        File.WriteAllText(ManifestPath(name), JsonSerializer.Serialize(mf, BotJson.Indented));
    }

    // 脚本备份：插件根下全部 *.cs（跳过 .cache 与 backups），保留最近 10 份
    private void BackupScript(string name)
    {
        try
        {
            var root = TargetRoot(name);
            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}"))
                .ToList();
            if (files.Count == 0) return;

            var dest = Path.Combine(BackupRoot(name), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dest);
            foreach (var f in files)
            {
                var target = Path.Combine(dest, Path.GetRelativePath(root, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
            }
            var backups = Directory.GetDirectories(BackupRoot(name)).OrderBy(d => d).ToList();
            while (backups.Count > 10) { try { Directory.Delete(backups[0], true); } catch { } backups.RemoveAt(0); }
        }
        catch (Exception ex) { _ctx.Log.Warn($"脚本备份失败（继续）: {ex.Message}"); }
    }

    // ===================== read / list / delete =====================

    private string ReadTree(string name)
    {
        var vErr = ValidateName(name);
        if (vErr is not null) return vErr;
        if (!ExistsAny(name)) return $"插件 {name} 不存在（编译型先 init，脚本型先 script-init）";
        var sb = new StringBuilder($"[{name}] {(IsScriptPlugin(name) ? "脚本型(csx)" : "编译型(dll)")}\n");
        var hidden = 0;
        foreach (var f in Directory.GetFiles(TargetRoot(name), "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}")
                              && !f.Contains(".shadow"))
                     .OrderBy(f => f))
        {
            var rel = Path.GetRelativePath(TargetRoot(name), f);
            // backups/ 是历次改动的源码快照，对"看当前长什么样"是噪音：只计数不列出
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (first.Equals("backups", StringComparison.OrdinalIgnoreCase)) { hidden++; continue; }
            sb.AppendLine($"📄 {rel}  ({new FileInfo(f).Length}B)");
        }
        if (hidden > 0) sb.AppendLine($"🗂 backups/ 内有 {hidden} 个历史快照文件（已省略；需要时用 fs_list 或直接看目录）");
        var mtime = File.Exists(DllPath(name)) ? new FileInfo(DllPath(name)).LastWriteTime.ToString("MM-dd HH:mm") : "无";
        sb.AppendLine($"产品: {name}.dll（{mtime}）");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListGeneratedAsync()
    {
        var mine = new List<string>();    // 有生成记录：pb_build 生成过（有 Provenance 或 generation.log）
        var others = new List<string>();  // 其它：内核自带 / 手工放入 / zip 安装
        foreach (var dir in Directory.GetDirectories(PluginsRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue; // .shadow / .cache / .pending 不是插件
            var hasDll = File.Exists(DllPath(name));
            var isCompiled = hasDll || File.Exists(Path.Combine(dir, "src", name + ".csproj"));
            var isScript = IsScriptPlugin(name);
            if (!isCompiled && !isScript) continue;

            var mf = ReadManifest(name);
            var desc = mf.TryGetValue("description", out var d) ? d.GetString() ?? ""
                     : mf.TryGetValue("Description", out var d2) ? d2.GetString() ?? "" : "";
            var deps = mf.TryGetValue("Depends", out var dp) && dp.ValueKind == JsonValueKind.Array
                ? string.Join(",", dp.EnumerateArray().Select(x => x.GetString())) : "";
            var kind = isScript ? "csx" : "dll";
            // 来源：优先看清单里的 Provenance；缺失但留有生成日志 → 也是 pb_build 生成（早期版本没写 Provenance）
            var origin = mf.TryGetValue("Provenance", out var pv) && pv.ValueKind == JsonValueKind.Object
                      && pv.TryGetProperty("by", out var by) && by.ValueKind == JsonValueKind.String
                ? by.GetString() ?? "?"
                : (File.Exists(Path.Combine(dir, "generation.log")) ? "manual" : "外部");
            var state = isScript
                ? (Directory.Exists(Path.Combine(dir, ".cache")) ? "✅" : "○") // ○ = 尚未编译过（首次加载时编译）
                : (File.Exists(DllPath(name)) ? "✅" : "❌");
            var line = $"{name} [{kind}][{origin}]: {state} {desc}{(deps.Length > 0 ? $" [依赖:{deps}]" : "")}";
            (origin == "外部" ? others : mine).Add(line);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"由 pb_build 生成的插件（{mine.Count}）:");
        if (mine.Count == 0) sb.AppendLine("  （暂无；pb_build script-init <名> 免编译 / pb_build init <名> 编译型）");
        else foreach (var l in mine) sb.AppendLine("  " + l);
        sb.AppendLine();
        sb.AppendLine($"其它插件（内核自带 / 手工放入 / zip 安装，{others.Count}，pb_build 无法 read/update）:");
        foreach (var l in others) sb.AppendLine("  " + l);
        await Task.CompletedTask;
        return sb.ToString().TrimEnd();
    }

    private async Task<string> DeleteAsync(string name)
    {
        var err = ValidateName(name);
        if (err is not null) return err;
        var root = TargetRoot(name);
        if (!Directory.Exists(root)) return $"插件 {name} 不存在";
        LogGen(name, "delete", "ok", "卸载并删除（含源码/产物/备份；这份生成日志随目录一起消失）");
        await _ctx.TryInvokeCommandAsync("plugin", $"unload {name}");
        await Task.Delay(800);
        try { Directory.Delete(root, true); return $"已删除 {name}（源码/产物/备份）"; }
        catch (Exception ex) { return $"删除失败（dll 被占用，重启宿主后手删）: {ex.Message}"; }
    }

    // ===================== manifest =====================

    private Dictionary<string, JsonElement> ReadManifest(string name)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(ManifestPath(name))) ?? []; }
        catch { return []; }
    }

    private async Task WriteManifestAsync(string name, string description)
    {
        var mf = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = description,
            ["author"] = "AI-Generated",
            ["created_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["created_by"] = "PluginBuilder",
            ["Depends"] = Array.Empty<string>(), // 宿主 PluginManifest 大写敏感
            ["NuGet"] = Array.Empty<string>(),   // 第三方依赖：形如 ["Newtonsoft.Json@13.0.4"]，宿主编译/加载前自动铺到 libs/
            ["Settings"] = Array.Empty<object>(), // 可选：WebUI 设置表单声明
            ["Provenance"] = new Dictionary<string, object?>          // 来源：manual=手工生成（AI 生成时会被改写成 ai）
            {
                ["by"] = "manual",
                ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            },
            ["Build"] = new Dictionary<string, object?> { ["rounds"] = 0, ["last_result"] = "init" }
        };
        await File.WriteAllTextAsync(ManifestPath(name),
            JsonSerializer.Serialize(mf, BotJson.Indented));
    }

    // ===================== 生成日志 =====================

    /// <summary>每插件一份追加式生成日志 plugins/&lt;名&gt;/generation.log：
    /// 记录每次 pb_build 动作（谁在什么时候生成/改了什么、结果如何、产物多大、备份在哪）。
    /// 与 plugin.json 的 Build 档案互补 —— 那个是"最近一次状态"，这个是历史。</summary>
    private void LogGen(string name, string action, string result, string detail)
    {
        try
        {
            var root = TargetRoot(name);
            Directory.CreateDirectory(root);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {OriginOf(name),-8} {action,-14} {result,-6} {detail}";
            File.AppendAllText(Path.Combine(root, "generation.log"),
                line + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            try { _ctx?.Log.Warn($"写生成日志失败: {ex.Message}"); } catch { }
        }
    }

    // ===================== 审计旁车（完整 LLM 输出 + 工具调用） =====================

    /// <summary>插件来源：ai = 由 AI 生成（有审计旁车），manual = pb_build 手工生成，
    /// 无标注（外部放入/zip 安装）由宿主按"未记录"展示</summary>
    private string OriginOf(string name)
    {
        var mf = ReadManifest(name);
        if (mf.TryGetValue("Provenance", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("by", out var by) && by.ValueKind == JsonValueKind.String)
            return by.GetString() ?? "manual";
        return "manual";
    }

    /// <summary>消费 AI 审计（Agent 通过 pb_build audit 暂存到 plugins/.pending/&lt;名&gt;.llm.json）：
    /// 把**完整工具调用参数**与**本次实际参与编译的源码**合并后追加到 plugins/&lt;名&gt;/generation.llm.jsonl，
    /// 并把来源标注为 ai。返回日志摘要（无待归档记录返回 null）。</summary>
    private string? ConsumeLlmSidecar(string name)
    {
        var pending = PendingPath(name);
        if (!File.Exists(pending)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pending));
            var root = doc.RootElement;

            // 源码不由命令行传输（fs_write 会把字面 \n 还原成真换行，JSON 经它必坏），
            // 这里直接从插件目录取"被编译的那一份"，更准也更省。
            var files = new Dictionary<string, string>();
            foreach (var f in ScriptSources(name))
                files[Path.GetRelativePath(TargetRoot(name), f).Replace('\\', '/')] = File.ReadAllText(f);
            var chars = files.Values.Sum(v => v.Length);

            var record = new Dictionary<string, object?>
            {
                ["recordedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["sourceFiles"] = files
            };
            foreach (var prop in root.EnumerateObject()) record[prop.Name] = prop.Value.Clone();

            File.AppendAllText(Path.Combine(TargetRoot(name), "generation.llm.jsonl"),
                JsonSerializer.Serialize(record, BotJson.Compact) + Environment.NewLine,
                new System.Text.UTF8Encoding(false));

            string Str(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            var tool = Str("tool");
            var model = Str("model");
            var at = Str("at");

            var mf = ReadManifest(name);
            mf["Provenance"] = JsonSerializer.SerializeToElement(new
            {
                by = "ai",
                tool,
                model,
                at = string.IsNullOrEmpty(at) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : at
            });
            File.WriteAllText(ManifestPath(name), JsonSerializer.Serialize(mf, BotJson.Indented));

            try { File.Delete(pending); } catch { }
            return $"model={model} tool={tool} 源码={files.Count} 文件/{chars} 字符";
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"归档 AI 审计失败（已丢弃，不影响生成）: {ex.Message}");
            try { File.Delete(pending); } catch { }
            return null;
        }
    }

    /// <summary>pb_build audit &lt;名&gt; &lt;base64(json)&gt;：Agent 工具桥用来暂存本次工具调用的
    /// 完整参数与模型信息。走 base64 是因为命令通道要经过 fs_write 的 \n 还原规则，明文 JSON 会被破坏。</summary>
    private string RecordAudit(string name, string b64)
    {
        var vErr = ValidateName(name);
        if (vErr is not null) return vErr;
        if (string.IsNullOrWhiteSpace(b64))
            return $"用法: pb_build audit <名> <base64(json)>";

        string json;
        try { json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim())); }
        catch (FormatException) { return "audit 参数不是合法 base64"; }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var path = PendingPath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, doc.RootElement.GetRawText(), new System.Text.UTF8Encoding(false));

            string Str(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            return $"AI 审计已记录（tool={Str("tool")} model={Str("model")} {json.Length} 字符）；"
                 + $"下次 pb_build create/update {name} 时归档进生成日志";
        }
        catch (JsonException ex)
        {
            return $"audit JSON 非法: {ex.Message}";
        }
    }

    /// <summary>pb_build llm &lt;名&gt; [序号]：列出台账或展开某一条完整记录</summary>
    private string ReadLlmLog(string name, string arg, int page)
    {
        if (!ExistsAny(name)) return $"插件 {name} 不存在";
        var path = Path.Combine(TargetRoot(name), "generation.llm.jsonl");
        if (!File.Exists(path)) return $"{name} 没有 LLM 审计（该插件不是由 AI 生成的）";

        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return $"{name} 的 LLM 审计为空";

        var idx = int.TryParse((arg ?? "").Trim(), out var k) && k >= 1 ? k : 0;
        if (idx == 0)
        {
            var sb = new StringBuilder($"{name} 的 LLM 生成台账（{lines.Count} 条，!pb_build llm {name} <序号> 看完整记录）:\n");
            for (var i = 0; i < lines.Count; i++)
            {
                try
                {
                    using var d = JsonDocument.Parse(lines[i]);
                    var r = d.RootElement;
                    string G(string key) => r.TryGetProperty(key, out var v) ? v.ToString() : "";
                    // 源码存在 sourceFiles 映射里（<相对路径> → 内容）
                    var chars = 0; var files = 0;
                    if (r.TryGetProperty("sourceFiles", out var sf) && sf.ValueKind == JsonValueKind.Object)
                        foreach (var f in sf.EnumerateObject())
                            if (f.Value.ValueKind == JsonValueKind.String)
                            {
                                files++;
                                chars += (f.Value.GetString() ?? "").Length;
                            }
                    sb.AppendLine($"#{i + 1} [{G("at")}] {G("tool")} kind={G("kind")} model={G("model")} 源={files} 文件/{chars} 字符");
                }
                catch { sb.AppendLine($"#{i + 1} （解析失败的一行）"); }
            }
            return sb.ToString().TrimEnd();
        }

        if (idx > lines.Count) return $"序号超范围（共 {lines.Count} 条）";
        return Paginate($"{name} LLM 记录 #{idx}（完整工具调用与源码）", lines[idx - 1], page, $"llm {name} {idx}");
    }

    // 简易分页：长文本（含完整源码的审计记录）拆成聊天消息长度可接受的多页
    private static string Paginate(string title, string body, int page, string retryArg)
    {
        const int limit = 2000;
        var pages = new List<string>();
        var cur = new StringBuilder();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (cur.Length + line.Length + 1 > limit && cur.Length > 0) { pages.Add(cur.ToString().TrimEnd()); cur.Clear(); }
            cur.AppendLine(line);
        }
        if (cur.Length > 0) pages.Add(cur.ToString().TrimEnd());
        if (pages.Count == 0) pages.Add("");

        var p = Math.Clamp(page, 1, pages.Count);
        var sb = new StringBuilder();
        sb.AppendLine($"【{title}】第 {p}/{pages.Count} 页");
        sb.Append(pages[p - 1]);
        if (p < pages.Count) sb.AppendLine($"\n(下一页: !pb_build {retryArg} {p + 1})");
        return sb.ToString().TrimEnd();
    }

    private string ReadGenLog(string name, string arg)
    {
        if (!ExistsAny(name)) return $"插件 {name} 不存在";
        var path = Path.Combine(TargetRoot(name), "generation.log");
        if (!File.Exists(path)) return $"{name} 没有生成日志（该插件不是由 pb_build 生成的）";

        var lines = File.ReadAllLines(path);
        var n = int.TryParse((arg ?? "").Trim(), out var k) && k > 0 ? Math.Min(k, 200) : 20;
        var tail = lines.Length > n ? lines[^n..] : lines;
        return $"{name} 生成日志（共 {lines.Length} 条，显示最近 {tail.Length} 条）:\n" + string.Join("\n", tail);
    }

    private void BumpBuild(string name, string result, string? info = null)
    {
        try
        {
            var mf = ReadManifest(name);
            var rounds = 0;
            if (mf.TryGetValue("Build", out var b) && b.ValueKind == JsonValueKind.Object
                && b.TryGetProperty("rounds", out var r) && r.ValueKind == JsonValueKind.Number)
                rounds = r.GetInt32();
            mf["Build"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["rounds"] = rounds + 1,
                ["last_result"] = result,
                ["last_error"] = result == "error" && info is not null ? info[..Math.Min(500, info.Length)] : null,
                ["last_time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            File.WriteAllText(ManifestPath(name), JsonSerializer.Serialize(mf, BotJson.Indented));
            // 每轮构建都进生成日志（脚本通道与编译通道共用这一个汇聚点）
            LogGen(name, "build", result, info is null ? "" : (info.Length > 300 ? info[..300] + "…" : info).Replace("\n", " ⏎ "));
        }
        catch { }
    }

    // ===================== 备份 =====================

    private void BackupSource(string name)
    {
        try
        {
            var src = SrcDir(name);
            if (!Directory.Exists(src)) return;
            var dest = Path.Combine(BackupRoot(name), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var rel = Path.GetRelativePath(src, f);
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
            }
            var backups = Directory.GetDirectories(BackupRoot(name)).OrderBy(d => d).ToList();
            while (backups.Count > 10) { try { Directory.Delete(backups[0], true); } catch { } backups.RemoveAt(0); }
            _ctx.Log.Info($"已备份 src/ → backups/{Path.GetFileName(dest)}");
        }
        catch (Exception ex) { _ctx.Log.Warn($"备份失败（继续构建）: {ex.Message}"); }
    }

    // ===================== 加载桥接 =====================

    private async Task<string> TryLoadAsync(string name)
    {
        var r = await _ctx.TryInvokeCommandAsync("plugin", $"load {name}") ?? "(无输出)";
        _ctx.Log.Info($"load {name}: {r}");
        return r;
    }

    private async Task<string> TryReloadAsync(string name)
    {
        var r = await _ctx.TryInvokeCommandAsync("plugin", $"reload {name}") ?? "(无输出)";
        _ctx.Log.Info($"reload {name}: {r}");
        return r;
    }
}
