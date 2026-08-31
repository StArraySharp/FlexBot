using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;

namespace PluginBuilderPlugin;

/// <summary>
/// PluginBuilder（v2：dotnet build 子进程方案）。
///
/// plugins/&lt;名&gt;/
///   src/&lt;名&gt;.csproj + *.cs    标准项目（AI fs_write 直接写这里，多文件随意）
///   plugin.json                 描述文件（Depends 依赖 + Build 构建档案）
///   &lt;名&gt;.dll                   构建产物（csproj OutDir 直落插件根）
///   backups/&lt;时间戳&gt;/           每轮修改前源目录快照（含 csproj，不含 obj/bin）
///
/// 工作流：pb_build init（骨架）→ fs_write 改码 → pb_build create（注册+构建+加载）
///        → 报错回喂 AI 修 → pb_build update（备份+构建+热重载）；deps 管理插件间依赖。
/// </summary>
public sealed class PluginBuilderPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;

    public string Name => "PluginBuilder";
    public string Version => "2.0.0";
    public string Description => "AI 插件工厂 v2：dotnet build 标准 csproj、多文件、插件间依赖、源码备份、错误回喂";

    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("BuildTimeoutSec", "构建超时秒", "number", "120", "dotnet build 子进程超时"),
        new("KeepBuildArtifacts", "保留构建中间目录", "bool", "false", "true = 保留 src/obj 与 src/bin（调试）；默认构建后清理"),
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

    // ===================== 命令分发 =====================

    private async Task<string> BuildAsync(string args)
    {
        var parts = args.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return """
                用法: pb_build <动作> <名> […]
                init <名>            生成骨架（csproj + 示例主类）
                write <名> <相对路径> 从暂存 fs:_pb_stage/<文件名> 写入 src/<相对路径>（多文件迭代用）
                create <名> <描述>    写 plugin.json + 构建 + 加载
                update <名>          备份 → 构建 → 热重载（每轮修改前自动备份）
                compile <名>         只构建验证
                read <名>            列出文件树
                readsrc <名> <相对路径> 读单文件内容
                deps <名> <A,B>      设置依赖插件（编译引用对方 dll + 运行时加载顺序）
                list                 列出全部生成插件
                delete <名>          卸载并删除全部目录
                """;
        var action = parts[0].ToLowerInvariant();
        var name = parts.Length > 1 ? parts[1] : "";
        var rest = parts.Length > 2 ? parts[2].Trim() : "";

        switch (action)
        {
            case "init": return InitProject(name);
            case "write": return StageWrite(name, rest);
            case "create": return await CreateAsync(name, rest.Length > 0 ? rest : "AI 生成插件");
            case "update": return await UpdateAsync(name);
            case "compile":
            {
                if (!CheckReady(name, out var e)) return e;
                var (ok, log) = await BuildProjectAsync(name);
                return ok ? $"构建成功\n{log}" : $"构建失败（修 src/ 后 pb_build update 再试）:\n{log}";
            }
            case "read": return ReadTree(name);
            case "readsrc": return ReadSrc(name, rest);
            case "deps": return SetDeps(name, rest);
            case "list": return await ListGeneratedAsync();
            case "delete": return await DeleteAsync(name);
            default: return $"未知动作 {action}";
        }
    }

    // 暂存目录（FileSystem 沙箱）：AI fs_write 到 _pb_stage/<任意名> 后经此动作写进 src/<相对路径>
    private string StageRoot()
    {
        var fsRoot = _ctx.GetSetting("Root", "");
        if (string.IsNullOrWhiteSpace(fsRoot)) fsRoot = Path.Combine(_ctx.Config.MemoryDir, "fs");
        return Path.Combine(fsRoot, "_pb_stage");
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
        if (!CheckReady(name, out var e)) return e;
        var srcRoot = Path.GetFullPath(SrcDir(name));
        var target = Path.GetFullPath(Path.Combine(srcRoot, relPath.Trim()))  ;
        if (!target.StartsWith(srcRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return $"拒绝：越界";
        return File.Exists(target) ? File.ReadAllText(target) : $"不存在: {relPath}";
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
                <!-- 宿主契约（PluginApi）与协议库：Private=false 不拷贝，运行时由宿主 ALC 提供 -->
                <Reference Include="CSharpBot">
                  <HintPath>{Path.Combine(hostBin, "CSharpBot.dll")}</HintPath>
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
        var (ok, log) = await BuildProjectAsync(name);
        if (!ok) return $"plugin.json 已写，但构建失败（修 src/ 后 update）：\n{log}";
        await TryLoadAsync(name);
        return $"插件 {name} 构建成功并尝试加载。\n{log}";
    }

    private async Task<string> UpdateAsync(string name)
    {
        if (!CheckReady(name, out var e)) return e;
        if (!File.Exists(ManifestPath(name))) return "未注册（先 pb_build create）";

        BackupSource(name);

        var (ok, log) = await BuildProjectAsync(name);
        if (!ok) return $"已备份旧源码，但构建失败（继续修后再 update）：\n{log}";
        await TryReloadAsync(name);
        return $"已更新、构建成功并热重载。\n{log}";
    }

    // ===================== deps：插件间依赖 =====================

    private string SetDeps(string name, string depsCsv)
    {
        if (!CheckReady(name, out var e)) return e;
        var deps = depsCsv.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var d in deps)
        {
            var err = ValidateName(d);
            if (err is not null) return $"依赖名非法：{err}";
            if (!File.Exists(Path.Combine(PluginsRoot, d, d + ".dll")))
                return $"依赖插件 {d} 不存在（plugins/{d}/{d}.dll 缺失；对方要先构建过）";
        }

        // manifest：Depends（宿主运行时按序加载 + ALC 借用）
        var mf = ReadManifest(name);
        mf["Depends"] = JsonSerializer.SerializeToElement(deps);
        File.WriteAllText(ManifestPath(name), JsonSerializer.Serialize(mf, BotJson.Indented));

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

    private async Task<(bool Ok, string Log)> BuildProjectAsync(string name)
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

    // ===================== read / list / delete =====================

    private string ReadTree(string name)
    {
        if (!CheckReady(name, out var e)) return e;
        var sb = new StringBuilder($"[{name}]\n");
        foreach (var f in Directory.GetFiles(TargetRoot(name), "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !f.Contains(".shadow"))
                     .OrderBy(f => f))
        {
            var rel = Path.GetRelativePath(TargetRoot(name), f);
            sb.AppendLine($"📄 {rel}  ({new FileInfo(f).Length}B)");
        }
        var mtime = File.Exists(DllPath(name)) ? new FileInfo(DllPath(name)).LastWriteTime.ToString("MM-dd HH:mm") : "无";
        sb.AppendLine($"产品: {name}.dll（{mtime}）");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListGeneratedAsync()
    {
        var sb = new StringBuilder("已生成插件：\n");
        var found = 0;
        foreach (var dir in Directory.GetDirectories(PluginsRoot))
        {
            var name = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, "src", name + ".csproj"))) continue;
            found++;
            var mf = ReadManifest(name);
            var desc = mf.TryGetValue("description", out var d) ? d.GetString() ?? "" : "";
            var deps = mf.TryGetValue("Depends", out var dp) && dp.ValueKind == JsonValueKind.Array
                ? string.Join(",", dp.EnumerateArray().Select(x => x.GetString())) : "";
            sb.AppendLine($"{name}: {(File.Exists(DllPath(name)) ? "✅" : "❌")} {desc}{(deps.Length > 0 ? $" [依赖:{deps}]" : "")}");
        }
        if (found == 0) sb.AppendLine("（暂无，pb_build init <名> 开始）");
        await Task.CompletedTask;
        return sb.ToString().TrimEnd();
    }

    private async Task<string> DeleteAsync(string name)
    {
        var err = ValidateName(name);
        if (err is not null) return err;
        var root = TargetRoot(name);
        if (!Directory.Exists(root)) return $"插件 {name} 不存在";
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
            ["Build"] = new Dictionary<string, object?> { ["rounds"] = 0, ["last_result"] = "init" }
        };
        await File.WriteAllTextAsync(ManifestPath(name),
            JsonSerializer.Serialize(mf, BotJson.Indented));
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

    private async Task TryLoadAsync(string name)
    {
        var r = await _ctx.TryInvokeCommandAsync("plugin", $"load {name}");
        _ctx.Log.Info($"load {name}: {r ?? "(无输出)"}");
    }

    private async Task TryReloadAsync(string name)
    {
        var r = await _ctx.TryInvokeCommandAsync("plugin", $"reload {name}");
        _ctx.Log.Info($"reload {name}: {r ?? "(无输出)"}");
    }
}
