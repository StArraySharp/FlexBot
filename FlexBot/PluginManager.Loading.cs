using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlexBot.PluginApi;
using FlexBot.Scripting;

namespace FlexBot;

// ② 拆分：扫描 / 加载插件 —— 从 PluginManager.cs 纯搬移至此（逻辑未改）。
sealed partial class PluginManager
{
    // ===================== 扫描 / 加载 =====================

    public async Task LoadAllAsync()
    {
        // 插件目录缺失时自动创建：发布包可以不带空目录，首次运行即就位（也避免用户误删后行为异常）
        if (!Directory.Exists(PluginRoot))
        {
            try
            {
                Directory.CreateDirectory(PluginRoot);
                Console.WriteLine($"[plugin] 插件目录不存在，已创建: {PluginRoot}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[plugin] 插件目录不存在: {PluginRoot}（创建失败: {ex.Message}）");
                return;
            }
        }

        // 一次性迁移旧全局配置（模型/人格/前缀/Chat→Agent）到插件设置
        MigrateLegacyHostSettings();

        // 收集候选（含清单），按依赖拓扑排序：被依赖者优先
        var candidates = new List<(string Name, string Dir, PluginManifest Manifest)>();
        foreach (var dir in Directory.GetDirectories(PluginRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            var n = Path.GetFileName(dir);
            if (!IsPluginDir(dir, n)) continue;
            if (_settings.PluginAutoload.TryGetValue(n, out var enabled) && !enabled)
            {
                Console.WriteLine($"[plugin] 跳过 {n}（启动自动加载已关闭，可用 !plugin load 手动加载）");
                continue;
            }
            candidates.Add((n, dir, PluginManifest.LoadFrom(dir)));
        }

        foreach (var (name, _, _) in TopologicalOrder(candidates))
            await LoadAsync(name, viaAll: true);
    }

    // 拓扑排序：depends 指向的插件排在前面；环/缺依赖的插件报错并跳过
    private static List<(string Name, string Dir, PluginManifest Manifest)> TopologicalOrder(
        List<(string Name, string Dir, PluginManifest Manifest)> candidates)
    {
        var byName = candidates.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=未访问 1=栈中 2=完成
        var result = new List<(string, string, PluginManifest)>();

        void Visit((string Name, string Dir, PluginManifest Manifest) node)
        {
            switch (visited.GetValueOrDefault(node.Name))
            {
                case 2: return;
                case 1:
                    Console.WriteLine($"[plugin] 依赖存在环，跳过 {node.Name}");
                    visited[node.Name] = 2; // 防重复报错
                    return;
            }
            visited[node.Name] = 1;
            foreach (var dep in node.Manifest.Depends)
            {
                if (byName.TryGetValue(dep, out var d)) Visit(d);
                else Console.WriteLine($"[plugin] {node.Name} 依赖的 {dep} 不在 plugins/ 目录，跳过该依赖");
            }
            visited[node.Name] = 2;
            result.Add(node);
        }

        foreach (var c in candidates) Visit(c);
        return result;
    }

    public Task<bool> LoadAsync(string dllOrName) => LoadAsync(dllOrName, viaAll: false);

    // 运行闸门：WS 未连接期间禁止手动 load/unload/reload（防离线误操作；启动自动加载不受限）
    private bool PluginOpsBlocked(string op, string target)
    {
        if (BotState.Connected) return false;
        Console.WriteLine($"[plugin] 拒绝 {op} {target}：WS 未连接（连上后可用）");
        return true;
    }

    private async Task<bool> LoadAsync(string dllOrName, bool viaAll)
    {
        // 手动加载（非启动链）需已连接 WS
        if (!viaAll && PluginOpsBlocked("load", dllOrName)) return false;

        var sourceDir = ResolveSourceDir(dllOrName);
        if (sourceDir is null)
        {
            Console.WriteLine($"[plugin] 找不到插件: {dllOrName}（期望 {PluginRoot}\\<名称>\\<名称>.dll）");
            return false;
        }
        var name = Path.GetFileName(sourceDir);

        lock (_lock)
        {
            if (_plugins.ContainsKey(name))
            {
                if (!viaAll) Console.WriteLine($"[plugin] 插件 {name} 已加载，如需重新加载请用 reload");
                return false;
            }
        }

        var manifest = PluginManifest.LoadFrom(sourceDir);

        // ---- 依赖处理：先递归加载依赖（除非已加载），再收集其主程序集供 ALC 借用 ----
        var dependencyAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var dependencyRecords = new List<string>();
        {
            // 检查环：依赖链上不能出现自己
            bool DependsOnSelf(string n, PluginManifest mf)
            {
                foreach (var dep in mf.Depends)
                {
                    if (string.Equals(dep, n, StringComparison.OrdinalIgnoreCase)) return true;
                    var depDir = Path.Combine(PluginRoot, dep);
                    if (DependsOnSelf(n, PluginManifest.LoadFrom(depDir))) return true;
                }
                return false;
            }
            if (DependsOnSelf(name, manifest))
            {
                Console.WriteLine($"[plugin] {name} 的依赖链存在环，拒绝加载");
                return false;
            }

            foreach (var dep in manifest.Depends)
            {
                LoadedPlugin? depPlugin;
                lock (_lock) _plugins.TryGetValue(dep, out depPlugin);
                if (depPlugin is null)
                {
                    // 依赖未加载：递归加载（autoload=off 的依赖在显式加载时也允许带上）
                    Console.WriteLine($"[plugin] {name} 依赖 {dep}，先加载依赖...");
                    if (!await LoadAsync(dep))
                    {
                        Console.WriteLine($"[plugin] 依赖 {dep} 加载失败，中止加载 {name}");
                        return false;
                    }
                    lock (_lock) _plugins.TryGetValue(dep, out depPlugin);
                }
                if (depPlugin is null)
                {
                    Console.WriteLine($"[plugin] 依赖 {dep} 不在位，中止加载 {name}");
                    return false;
                }

                // 借用依赖的主程序集实例（类型同一性：A 与 B 共用同一份 B 类型）
                var depAsm = depPlugin.Alc.Assemblies.FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, depPlugin.Instance.GetType().Assembly.GetName().Name,
                        StringComparison.OrdinalIgnoreCase));
                if (depAsm is not null)
                    dependencyAssemblies[depAsm.GetName().Name!] = depAsm;
                dependencyRecords.Add(dep);
            }
        }

        var pluginSettings = LoadPluginSettings(name);

        // NuGet 依赖：由 plugin.json 的 NuGet 字段声明，缺失的包自动下载铺进本插件 libs/
        // （宿主持有解析器，插件作者无需任何外部工具/脚本；编译期与运行期都从 libs/ 取）
        if (manifest.NuGet.Count > 0)
        {
            try
            {
                var nr = await NuGetResolver.EnsureLibsAsync(
                    sourceDir, manifest.NuGet, _settings.NuGetFeed, SharedHttp.Client,
                    m => Console.WriteLine($"[plugin] {name} {m}"));
                if (nr.Added.Count > 0)
                    Console.WriteLine($"[plugin] {name} NuGet 依赖已就位（{nr.Resolved} 个包）: {string.Join(", ", nr.Added)}");
                foreach (var note in nr.Notes)
                    Console.WriteLine($"[plugin] {name} NuGet: {note}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[plugin] {name} NuGet 依赖解析异常（继续尝试加载）: {ex.Message}");
            }
        }

        try
        {
            // 影子目录：编译型插件复制源目录，脚本型插件把内存编译产物写进去 —— 之后共用同一套
            // 加载/卸载机制。目录带时间戳后缀：旧程序集可能尚未被 GC 回收（文件仍被锁），不能原地覆盖
            var shadowRoot = Path.Combine(PluginRoot, ".shadow", $"{name}_{DateTime.Now.Ticks}");
            var shadowDll = Path.Combine(shadowRoot, name + ".dll");

            // 类型判定：有 <名>.dll 是编译型；无 dll 且无 src/<名>.csproj 才走脚本型
            // （编译型项目的源码在 src/ 下，若不排除会被误当脚本插件反复内存编译）
            var hasDll = File.Exists(Path.Combine(sourceDir, name + ".dll"));
            var hasCsproj = File.Exists(Path.Combine(sourceDir, "src", name + ".csproj"));
            var isScript = !hasDll && !hasCsproj && ScriptCompiler.IsScriptPlugin(sourceDir);
            if (!hasDll && !isScript)
            {
                NoteLoadFailure(name, $"存在源码项目但缺 {name}.dll（用 pb_build create/update 产出，或直接 dotnet build）");
                return false;
            }
            if (isScript)
            {
                var build = await ScriptCompiler.CompileAsync(sourceDir, name);
                if (!build.Ok)
                {
                    NoteLoadFailure(name, build.Diagnostics.Trim());
                    return false;
                }
                Directory.CreateDirectory(shadowRoot);
                await File.WriteAllBytesAsync(shadowDll, build.Pe!);
                if (build.Pdb is not null)
                    await File.WriteAllBytesAsync(Path.Combine(shadowRoot, name + ".pdb"), build.Pdb);
                // libs/ 随影子目录，供 PluginLoadContext 解析第三方依赖
                CopyDirectoryIfExists(Path.Combine(sourceDir, "libs"), Path.Combine(shadowRoot, "libs"));
                Console.WriteLine($"[plugin] {name} csx 编译完成（{build.Pe!.Length} bytes{(build.FromCache ? "，命中缓存" : "")}）");
            }
            else
            {
                CopyDirectory(sourceDir, shadowRoot);
            }

            var alc = new PluginLoadContext(shadowDll, dependencyAssemblies);
            var asm = alc.LoadFromAssemblyPath(shadowDll);
            var pluginType = asm.GetTypes()
                .FirstOrDefault(t => typeof(IBotPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
            if (pluginType is null)
            {
                alc.Unload();
                NoteLoadFailure(name, "程序集里没有 IBotPlugin 实现（需一个非抽象 public 类实现 FlexBot.PluginApi.IBotPlugin）");
                return false;
            }
            var instance = Activator.CreateInstance(pluginType) as IBotPlugin
                ?? throw new InvalidOperationException($"无法实例化 {pluginType.FullName}");

            var dataDir = Path.Combine(_config.MemoryDir, "plugins", instance.Name);
            var ctx = new BotContextImpl(instance.Name, _api, _config, shadowRoot, dataDir, _messages, _events, pluginSettings, this);
            var loaded = new LoadedPlugin
            {
                Name = name,
                SourceDir = sourceDir,
                ShadowDir = shadowRoot,
                Instance = instance,
                Context = ctx,
                Alc = alc,
                AlcRef = new WeakReference(alc),
                Manifest = manifest,
                IsScript = isScript
            };
            lock (_lock)
            {
                _plugins[name] = loaded;
                // 登记反向依赖（用于级联卸载）
                foreach (var dep in dependencyRecords)
                    if (_plugins.TryGetValue(dep, out var dp))
                        dp.Dependents.Add(name);
            }

            await instance.OnLoadAsync(ctx);
            ClearLoadFailure(name);
            var depNote = dependencyRecords.Count > 0 ? $"（依赖: {string.Join(", ", dependencyRecords)}）" : "";
            Console.WriteLine($"[plugin] 已加载 {instance.Name} v{instance.Version} - {instance.Description}{depNote}");

            // 顺带清理同插件的历史影子目录（旧 ALC 可能还没释放文件锁，删不掉就留给下次）
            CleanupShadowDirs(name, keep: shadowRoot);
            return true;
        }
        catch (Exception ex)
        {
            NoteLoadFailure(name, ex.Message);
            return false;
        }
    }

}
