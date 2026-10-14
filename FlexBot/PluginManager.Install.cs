using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlexBot.PluginApi;
using FlexBot.Scripting;

namespace FlexBot;

// ② 拆分：插件安装（zip 安全解压 / 卸载旧版 / 立即加载）从 PluginManager.cs 移到这里。
// 纯搬移，逻辑一行未改；用 partial 保证编译器校验一致性。
sealed partial class PluginManager
{
    // ===================== WebUI：重新搜索 / zip 安装 =====================

    /// <summary>重新搜索插件目录（GetSnapshot 每次都是实时扫盘，这里额外给出统计与控制台记录）。</summary>
    public (int Total, int Loaded) Rescan()
    {
        var snapshot = GetSnapshot();
        var loaded = snapshot.Count(p => p.Loaded);
        Console.WriteLine($"[plugin] 重新搜索插件目录：发现 {snapshot.Count} 个（已加载 {loaded}）");
        return (snapshot.Count, loaded);
    }

    /// <summary>zip 安装结果。</summary>
    public sealed record InstallResult(bool Ok, string Name, string Message, bool Loaded, int FileCount, List<string> Warnings)
    {
        /// <summary>本次实际安装的插件名列表（一个 zip 可含多个插件；单插件时就是 [Name]）</summary>
        public List<string> Installed { get; init; } = [];
    }

    // zip 内出现这些扩展名时给出提醒（插件本身已是完全信任的执行体，此处只做可见性提醒，不阻断）
    private static readonly string[] RiskyExtensions =
        [".exe", ".bat", ".cmd", ".ps1", ".sh", ".msi", ".scr", ".com", ".vbs", ".lnk"];

    /// <summary>
    /// 从 zip 安装插件到 plugins/&lt;名&gt;/：
    /// 安全解压（防目录穿越 / 体积上限）→ 同名旧插件先卸载（保留 settings.json）→ 可选立即加载。
    /// 支持三种打包形态：单个顶层目录含插件（常见）、文件直接位于压缩包根、**一个 zip 含多个插件**
    /// （含 plugins/&lt;名&gt;/ 这种带容器前缀的形态，如发布用的 FlexBot_BuiltInPlugins.zip）。
    /// </summary>
    /// <summary>zip 条目相对路径是否不安全（空 / 绝对路径 / 含盘符或 ADS 冒号 / 含 .. 段）。
    /// 原为安装方法内的局部函数，提为类成员以便直接单测：逻辑一字未改。</summary>
            internal static bool IsUnsafeRelPath(string rel) =>
                rel.Length == 0
                || rel.StartsWith('/')
                || rel.Contains(':')
                || Path.IsPathRooted(rel)
                || rel.Split('/', '\\').Any(seg => seg == "..");
    public async Task<InstallResult> InstallFromZipAsync(
        Stream zip, string? fallbackName, bool load, CancellationToken ct = default)
    {
        InstallResult Fail(string msg) => new(false, "", msg, false, 0, []);

        const long MaxZipBytes = 64L * 1024 * 1024;
        const long MaxUnpackedBytes = 128L * 1024 * 1024;

        // ---- 1. 读入内存并打开 zip ----
        using var buffer = new MemoryStream();
        try { await zip.CopyToAsync(buffer, ct); }
        catch (Exception ex) { return Fail($"读取上传内容失败: {ex.Message}"); }
        if (buffer.Length == 0) return Fail("上传内容为空");
        if (buffer.Length > MaxZipBytes) return Fail($"zip 超过 {MaxZipBytes / 1024 / 1024}MB 上限");
        buffer.Position = 0;

        ZipArchive archive;
        try { archive = new ZipArchive(buffer, ZipArchiveMode.Read); }
        catch (Exception ex) { return Fail($"不是有效的 zip 文件: {ex.Message}"); }

        using (archive)
        {
            // ---- 2. 收集有效条目（跳过目录项与系统垃圾文件；剔除路径不安全条目）----
            // 安全判定与目标目录无关，故可前置：含 .. 段 / 绝对路径 / 带冒号（盘符、ADS）一律拒绝。

            var entries = new List<(string Rel, ZipArchiveEntry Entry)>();
            var warnings = new List<string>();
            foreach (var e in archive.Entries)
            {
                var rel = e.FullName.Replace('\\', '/').TrimStart('/');
                if (rel.Length == 0 || rel.EndsWith('/')) continue;                       // 目录项
                if (rel.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
                var leaf = rel.Split('/')[^1];
                if (leaf is ".DS_Store" or "Thumbs.db") continue;
                if (IsUnsafeRelPath(rel)) { warnings.Add($"已跳过不安全条目: {e.FullName}"); continue; }
                entries.Add((rel, e));
            }
            if (entries.Count == 0)
                return Fail(warnings.Count > 0
                    ? $"zip 内没有可解压的安全文件（已跳过 {warnings.Count} 个越界条目）"
                    : "zip 内没有文件");

            // ---- 3. 发现全部插件根（一个 zip 可含多个插件）----
            // 插件根 = 直接含有 plugin.json / *.dll / *.cs 的目录；再剔除明显的非插件目录与更深层候选。
            static bool IsMarkerFile(string leaf) =>
                leaf.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
                || leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || leaf.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

            var noPluginDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "src", "bin", "obj", "libs", "backups", ".cache", ".shadow", ".pending", "__MACOSX" };

            var dirsWithMarker = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootHasMarker = false;
            foreach (var (rel, _) in entries)
            {
                var slash = rel.LastIndexOf('/');
                if (slash < 0) { if (IsMarkerFile(rel)) rootHasMarker = true; continue; }
                if (IsMarkerFile(rel[(slash + 1)..])) dirsWithMarker.Add(rel[..slash]);
            }

            // 只保留最浅层：若 A/B 与 A 都是候选，取 A（A/B/src 这类子目录自然被排除）
            var roots = dirsWithMarker
                .Where(d =>
                {
                    var leaf = d.Split('/')[^1];
                    if (noPluginDir.Contains(leaf)) return false;
                    return !dirsWithMarker.Any(o =>
                        o.Length < d.Length && d.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase));
                })
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var targets = new List<(string Name, string Prefix)>();
            if (roots.Count == 0)
            {
                // 平铺 zip（文件直接在根）：按根目录的 <名>.dll 推断，否则用上传文件名兜底
                var rootDll = entries.Where(x => !x.Rel.Contains('/'))
                    .Select(x => x.Rel)
                    .FirstOrDefault(r => r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                var flatName = rootDll is not null ? Path.GetFileNameWithoutExtension(rootDll) : (fallbackName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(flatName) || (rootDll is null && !rootHasMarker))
                    return Fail("无法确定插件名：把插件放进以插件名命名的顶层目录（内含 <名>.dll 或 *.cs），或用 <插件名>.zip 命名压缩包");
                targets.Add((flatName, ""));
            }
            else
            {
                foreach (var r in roots) targets.Add((r.Split('/')[^1], r + "/"));
            }

            // ---- 4. 逐个安装（同名会覆盖旧版，并保留旧 settings.json）----
            var rootFull = Path.GetFullPath(PluginRoot);
            Directory.CreateDirectory(rootFull);
            var installed = new List<string>();
            var failed = new List<string>();
            var totalFiles = 0;

            foreach (var (rawName, prefix) in targets)
            {
                var name = rawName.Trim();
                if (name.Length is 0 or > 64 || name[0] == '.'
                    || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || name.Contains('/') || name.Contains('\\') || name is "." or "..")
                {
                    failed.Add($"{rawName}（名称非法）");
                    continue;
                }
                if (installed.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    failed.Add($"{name}（zip 内重名，已跳过后面的）");
                    continue;
                }

                var targetDir = Path.GetFullPath(Path.Combine(rootFull, name));
                if (!targetDir.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add($"{name}（路径越界）");
                    continue;
                }

                var existed = Directory.Exists(targetDir);
                var settingsPath = Path.Combine(targetDir, "settings.json");
                string? keepSettings = null;
                if (existed && File.Exists(settingsPath))
                {
                    try { keepSettings = await File.ReadAllTextAsync(settingsPath, ct); } catch { }
                }

                if (existed)
                {
                    if (IsLoaded(name))
                    {
                        Console.WriteLine($"[plugin] 安装 {name}：同名插件已加载，先卸载");
                        await UnloadAsync(name);
                        await Task.Delay(300); // 给 ALC 一点释放时间
                    }
                    try { Directory.Delete(targetDir, true); }
                    catch (Exception ex)
                    {
                        failed.Add($"{name}（旧目录无法删除：{ex.Message}）");
                        continue;
                    }
                    lock (_lock) _pluginSettings.Remove(name);
                }

                // ---- 解压（体积上限；路径安全已在收集阶段过滤，这里再校验一次目标落点）----
                Directory.CreateDirectory(targetDir);
                var written = 0;
                long total = 0;
                foreach (var (rel0, entry) in entries)
                {
                    if (prefix.Length > 0 && !rel0.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = prefix.Length > 0 ? rel0[prefix.Length..] : rel0;
                    if (rel.Length == 0) continue;

                    var dest = Path.GetFullPath(Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"已跳过越界条目: {rel0}");
                        continue;
                    }

                    total += entry.Length;
                    if (total > MaxUnpackedBytes)
                    {
                        warnings.Add($"{name}: 解压总量超过 {MaxUnpackedBytes / 1024 / 1024}MB，已停止后续条目");
                        break;
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await using var src = entry.Open();
                        await using var dst = File.Create(dest);
                        await src.CopyToAsync(dst, ct);
                        written++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"解压失败 {rel}: {ex.Message}");
                        continue;
                    }

                    if (RiskyExtensions.Any(x => dest.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                        warnings.Add($"含可执行/脚本文件: {rel}");
                }

                if (written == 0)
                {
                    try { Directory.Delete(targetDir, true); } catch { }
                    failed.Add($"{name}（zip 内该插件没有可解压的文件）");
                    continue;
                }

                // 保留用户设置（zip 未自带 settings.json 时回填）
                if (keepSettings is not null && !File.Exists(settingsPath))
                {
                    try { await File.WriteAllTextAsync(settingsPath, keepSettings, ct); } catch { }
                }

                installed.Add(name);
                totalFiles += written;
                Console.WriteLine($"[plugin] 已从 zip 安装 {name}：{written} 个文件{(existed ? "（覆盖旧版本）" : "")}");
            }

            if (installed.Count == 0)
                return Fail(failed.Count > 0 ? $"安装失败：{string.Join("；", failed)}" : "zip 内没有可安装的插件");

            // ---- 5. 可选立即加载 ----
            var loadedNames = new List<string>();
            var note = "";
            if (load)
            {
                if (!BotState.Connected)
                {
                    note = "（WS 未连接，仅解压未加载；连上后点「加载」或 !plugin load）";
                }
                else
                {
                    foreach (var n in installed)
                    {
                        try { if (await LoadAsync(n)) loadedNames.Add(n); } catch { }
                    }
                    note = loadedNames.Count == installed.Count
                        ? $"，已自动加载 {loadedNames.Count} 个"
                        : $"，加载成功 {loadedNames.Count}/{installed.Count}（失败见控制台日志）";
                }
            }

            var title = installed.Count == 1 ? $"已安装 {installed[0]}" : $"已安装 {installed.Count} 个插件：{string.Join("、", installed)}";
            var msg = $"{title}（共 {totalFiles} 个文件）{note}"
                    + (failed.Count > 0 ? $"\n失败 {failed.Count} 个：{string.Join("；", failed)}" : "");
            return new InstallResult(true, installed[0], msg, loadedNames.Count > 0, totalFiles, warnings)
            {
                Installed = installed
            };
        }
    }

    // ===================== 命令注册表 =====================

    private sealed record CommandRegistration(CommandInfo Info, CommandHandler Handler);

    /// <summary>插件调用的注册入口（经 BotContextImpl 转发，带插件名）。</summary>
    public IDisposable RegisterCommand(string pluginName, string name, string description, CommandHandler handler, string usage = "")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("命令名不能为空");
        name = name.Trim().ToLowerInvariant();
        var reg = new CommandRegistration(new CommandInfo(name, description, pluginName, usage), handler);
        lock (_cmdLock)
        {
            _commands[name] = reg; // 同名覆盖：后注册者优先
        }
        return new CommandUnsubscriber(this, name);
    }

    private void UnregisterCommand(string name)
    {
        lock (_cmdLock) _commands.Remove(name);
    }

    /// <summary>卸载插件时注销其全部命令。</summary>
    private void RemoveCommandsOf(string pluginName)
    {
        lock (_cmdLock)
        {
            foreach (var k in _commands.Where(kv => kv.Value.Info.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                                       .Select(kv => kv.Key).ToList())
                _commands.Remove(k);
        }
    }

    /// <summary>全部已注册命令（帮助/WebUI 用）。</summary>
    public IReadOnlyList<CommandInfo> GetCommands()
    {
        lock (_cmdLock) return _commands.Values.Select(r => r.Info).OrderBy(c => c.PluginName).ThenBy(c => c.Name).ToList();
    }

    /// <summary>查找命令（不存在返回 null）。</summary>
    public (CommandInfo Info, CommandHandler Handler)? FindCommand(string name)
    {
        lock (_cmdLock)
            return _commands.TryGetValue(name.Trim().ToLowerInvariant(), out var reg)
                ? (reg.Info, reg.Handler) : null;
    }

    /// <summary>跨插件调用命令：执行并返回结果文本；不存在或异常时返回带错误标记的文本。</summary>
    public async Task<string?> InvokeCommandAsync(string name, string args)
    {
        // 宿主内置命令转发（plugin/help 在 Program.cs 的 TryDispatchCommandAsync 处理，插件侧调用走这里）
        if (name is "plugin" or "help" or "帮助")
            return await HandleCommandAsync(args.Length > 0 ? $"{name} {args}" : name);

        var reg = FindCommand(name);
        if (reg is null) return null;
        try
        {
            return await reg.Value.Handler(args ?? "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[cmd] invoke {name} 异常: {ex.Message}");
            return $"命令执行出错: {ex.Message}";
        }
    }

    /// <summary>读取已加载插件的某个设置值（文本型；插件未加载/未设置返回 null）。</summary>
    public string? GetPluginSettingString(string plugin, string key)
    {
        lock (_lock)
            if (!_plugins.TryGetValue(plugin, out var p)) return null;
        var dict = LoadPluginSettings(plugin);
        return dict.TryGetValue(key, out var v) ? v.ToString() : null;
    }

    private sealed class CommandUnsubscriber(PluginManager owner, string name) : IDisposable
    {
        public void Dispose() => owner.UnregisterCommand(name);
    }

    public string BuildList()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("插件列表：");
        lock (_lock)
        {
            foreach (var p in _plugins.Values.OrderBy(x => x.Name))
            {
                var deps = p.Manifest.Depends.Count > 0 ? $"（依赖: {string.Join(",", p.Manifest.Depends)}）" : "";
                var depsOn = p.Dependents.Count > 0 ? $"（被依赖: {string.Join(",", p.Dependents)}）" : "";
                var kind = p.IsScript ? "（csx 脚本）" : "";
                sb.AppendLine($"  [已加载] {p.Name}{kind} v{p.Instance.Version} - {p.Instance.Description}{deps}{depsOn}");
            }
        }
        var loadedNames = new HashSet<string>(_plugins.Keys, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(PluginRoot))
        {
            foreach (var dir in Directory.GetDirectories(PluginRoot))
            {
                var n = Path.GetFileName(dir);
                if (n.StartsWith('.')) continue;
                if (loadedNames.Contains(n)) continue;
                if (IsPluginDir(dir, n))
                    sb.AppendLine($"  [未加载] {n}");
            }
        }
        sb.Append("命令: !plugin load|unload|reload <名称>");
        return sb.ToString().TrimEnd();
    }

    // !plugin 命令入口（宿主级，仅管理员私聊可用）
    public async Task<string> HandleCommandAsync(string text)
    {
        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
        var arg = parts.Length >= 3 ? parts[2].Trim() : "";
        switch (cmd)
        {
            case "list":
                return BuildList();
            case "load":
                if (arg.Length == 0) return "用法: !plugin load <名称>";
                if (await LoadAsync(arg)) return $"已加载插件 {arg}";
                // 把失败原因（编译诊断/异常）直接带出来，插件工厂与 AI 才能看到 CS 错误
                var why = LastLoadError(arg);
                if (string.IsNullOrEmpty(why)) return $"加载 {arg} 失败（详见控制台日志）";
                return $"加载 {arg} 失败:\n{(why.Length > 900 ? why[..900] + "…" : why)}";
            case "unload":
                return arg.Length == 0 ? "用法: !plugin unload <名称>"
                    : await UnloadAsync(arg) ? $"已卸载插件 {arg}" : $"卸载 {arg} 失败";
            case "reload":
                return arg.Length == 0 ? "用法: !plugin reload <名称>"
                    : await ReloadAsync(arg) ? $"已重载插件 {arg}" : $"重载 {arg} 失败（详见控制台日志）";
            case "nuget":
                return arg.Length == 0 ? "用法: !plugin nuget <名称>" : await ResolveDepsAsync(arg);
            default:
                return "用法: !plugin [list|load|unload|reload|nuget] [名称]";
        }
    }

    /// <summary>按 plugin.json 的 NuGet 字段把依赖铺进 libs/（插件加载时会自动做，这里可手动/被插件工厂预调）。
    /// 注意与 PluginBuilder 的 `pb_build deps`（设置插件间 Depends）不是一回事，故不叫 deps。</summary>
    public async Task<string> ResolveDepsAsync(string name)
    {
        if (!IsSafePluginName(name)) return $"插件名非法: {name}";
        var dir = Path.Combine(PluginRoot, name);
        if (!Directory.Exists(dir)) return $"插件 {name} 不存在";
        var manifest = PluginManifest.LoadFrom(dir);
        if (manifest.NuGet.Count == 0)
            return $"{name} 未声明 NuGet 依赖（在 plugin.json 里写 \"NuGet\": [\"包名@版本\"]）";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{name} 声明依赖: {string.Join(", ", manifest.NuGet)}");
        try
        {
            var r = await NuGetResolver.EnsureLibsAsync(dir, manifest.NuGet, _settings.NuGetFeed,
                SharedHttp.Client, m => Console.WriteLine($"[plugin] {name} {m}"));
            sb.AppendLine(r.Added.Count > 0
                ? $"已铺入 libs/（{r.Resolved} 个包）: {string.Join(", ", r.Added)}"
                : "libs/ 已是最新，或包内没有可用的 lib/ 资产");
            foreach (var note in r.Notes) sb.AppendLine($"· {note}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"解析失败: {ex.Message}");
        }
        return sb.ToString().TrimEnd();
    }

    // ===================== 内部工具 =====================

    private string? ResolveSourceDir(string dllOrName)
    {
        // 形态 1：绝对/相对 DLL 路径
        if (dllOrName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(dllOrName);
            return File.Exists(path) ? Path.GetDirectoryName(path) : null;
        }
        // 形态 2：插件名 → plugins/<名>/（编译型 <名>.dll 或 脚本型 *.cs 源码）
        if (!IsSafePluginName(dllOrName)) return null;
        var dir = Path.Combine(PluginRoot, dllOrName);
        return IsPluginDir(dir, dllOrName) ? dir : null;
    }

    /// <summary>插件目录判定：编译型（<名>.dll 存在）或脚本型（目录内有 *.cs 源码）</summary>
    private static bool IsPluginDir(string dir, string name) =>
        File.Exists(Path.Combine(dir, name + ".dll")) || ScriptCompiler.IsScriptPlugin(dir);

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    // 源目录存在才复制（libs/ 这类可选目录）
    private static void CopyDirectoryIfExists(string src, string dest)
    {
        if (Directory.Exists(src)) CopyDirectory(src, dest);
    }

    // 删除插件 name 的全部影子目录（保留 keep；失败静默，旧锁未释放时下次再清）
    private void CleanupShadowDirs(string name, string? keep = null)
    {
        try
        {
            var shadowRoot = Path.Combine(PluginRoot, ".shadow");
            if (!Directory.Exists(shadowRoot)) return;
            foreach (var dir in Directory.GetDirectories(shadowRoot))
            {
                var n = Path.GetFileName(dir);
                if (n != name && !n.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) continue;
                if (keep is not null && dir.Equals(Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }
}
