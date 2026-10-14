using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlexBot.PluginApi;
using FlexBot.Scripting;

namespace FlexBot;

// 插件程序集加载上下文：
// - isCollectible=true 使宿主可随时 Unload 回收整个程序集
// - PluginApi / OneBotLib 固定回落到默认上下文（保证宿主与插件类型一致）
// - 依赖插件（plugin.json 的 depends）→ 借用对方 ALC 中的实例（类型同一性 + 随对方卸载）
// - 其余依赖通过 AssemblyDependencyResolver 从插件目录解析（插件可自带 NuGet 依赖）
sealed class PluginLoadContext(
    string pluginPath,
    IReadOnlyDictionary<string, Assembly> dependencyAssemblies) : AssemblyLoadContext(
    name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "FlexBot.Abstractions", // 契约层：宿主与全部插件共享同一份类型身份（热重载的地基）
        "OneBotLib",            // OneBot 协议库
        "FlexBot"               // 宿主程序集：兼容旧插件 DLL（其引用的是旧的整体宿主程序集）
    };

    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);
    // 插件自带第三方依赖目录（影子目录下的 libs/）：脚本插件与手放 DLL 统一从这里解析
    private readonly string _libDir = Path.Combine(Path.GetDirectoryName(pluginPath) ?? ".", "libs");

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
            return null; // 回落到默认上下文（与宿主共享同一份类型）
        // ★ 依赖插件：借用其 ALC 的实例（不重复加载；其"家"ALC 决定卸载语义）
        if (assemblyName.Name is not null &&
            dependencyAssemblies.TryGetValue(assemblyName.Name, out var borrowed))
            return borrowed;
        // ★ 插件自带 libs/：AssemblyDependencyResolver 只读 .deps.json 不扫目录，
        //   故这里显式探测（脚本插件没有 .deps.json，第三方依赖全靠这一条）。
        //   但先问默认上下文：框架/宿主已提供同名程序集时用它，避免 libs/ 里的旧副本遮蔽框架实现
        //   （NuGet 包常带 System.* / netstandard 门面副本）。
        if (assemblyName.Name is not null)
        {
            var libPath = Path.Combine(_libDir, assemblyName.Name + ".dll");
            if (File.Exists(libPath))
            {
                try { return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName); }
                catch { /* 框架/宿主没有 → 用插件自带副本 */ }
                return LoadFromAssemblyPath(libPath);
            }
        }
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

// 插件清单（plugins/<名>/plugin.json）：声明依赖、展示信息与设置项。
// 编译型插件与脚本型插件共用这一份清单（脚本插件主要靠它提供设置项与展示信息）。
class PluginManifest
{
    public List<string> Depends { get; set; } = [];

    /// <summary>展示用版本（已加载插件的实际版本以插件类为准；未加载时用此值展示）</summary>
    public string? Version { get; set; }

    /// <summary>展示用描述（同上）</summary>
    public string? Description { get; set; }

    /// <summary>设置项声明：插件类未声明 SettingDefs 时由此提供（脚本插件的主要配置入口）</summary>
    public List<PluginSettingDef> Settings { get; set; } = [];

    /// <summary>第三方 NuGet 依赖声明（保留字段：v1 未实现自动下载，手动放入 libs/ 即生效）</summary>
    public List<string> NuGet { get; set; } = [];

    /// <summary>来源标注（由 PluginBuilder 写入）：by = ai（AI 生成，带审计）| manual（pb_build 手工生成）。
    /// 缺省表示没有生成记录（内置插件 / 手工放入 / zip 安装）。</summary>
    public ProvenanceInfo? Provenance { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static PluginManifest LoadFrom(string pluginDir)
    {
        try
        {
            var file = Path.Combine(pluginDir, "plugin.json");
            if (!File.Exists(file)) return new PluginManifest();
            var m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), JsonOpts);
            return m ?? new PluginManifest();
        }
        catch
        {
            return new PluginManifest();
        }
    }
}

/// <summary>插件来源标注（plugin.json 的 Provenance）：区分 AI 生成与手工生成</summary>
class ProvenanceInfo
{
    /// <summary>ai = AI 生成（有 generation.llm.jsonl 审计）；manual = pb_build 手工生成</summary>
    public string? By { get; set; }
    /// <summary>生成它的工具（如 pb_create / pb_update）</summary>
    public string? Tool { get; set; }
    /// <summary>生成时使用的模型</summary>
    public string? Model { get; set; }
    /// <summary>生成时间</summary>
    public string? At { get; set; }
}

// IBotContext 实现：追踪该插件注册的全部订阅，Dispose 时统一退订（卸载兜底，防泄漏导致程序集无法回收）
sealed class BotContextImpl : IBotContext
{
    private readonly string _pluginName;
    private readonly Dictionary<string, JsonElement> _pluginSettings;
    private readonly PluginManager _manager;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<IDisposable> _commandSubs = [];
    private readonly object _lock = new();

    public IBotApi Api { get; }
    public IBotConfig Config { get; }
    public string PluginDir { get; }
    public string DataDir { get; }
    public ILog Log { get; }
    public IMessageHub Messages { get; }
    public IEventHub Events { get; }
    public IBotScheduler Scheduler { get; }
    public PluginKeyValueStore KV { get; }
    public SharedHttp Http { get; }
    // 全进程共享一份编译服务（Roslyn 无状态；插件无需自带编译器）
    private static readonly ICompilerService SharedCompiler = new HostCompilerService();
    public ICompilerService Compiler => SharedCompiler;

    // 历史库（P2）：只读；全进程共享一个实现，无状态
    private static readonly IHistoryStore SharedHistory = new HistoryStoreAccess();
    public IHistoryStore History => SharedHistory;
    // 用量上报（宿主未启用时用一个空实现，插件无需判空）
    private sealed class NullUsage : IUsageSink { public void Record(string p, string m, long i, long o, long c, long ms) { } }
    public IUsageSink Usage => UsageStore.Current ?? (IUsageSink)NullUsageInstance;
    private static readonly NullUsage NullUsageInstance = new();

    public BotContextImpl(string pluginName, IBotApi api, IBotConfig config, string pluginDir, string dataDir, MessageRouter messages, EventRouter events, Dictionary<string, JsonElement> pluginSettings, PluginManager manager)
    {
        _pluginName = pluginName;
        _pluginSettings = pluginSettings;
        _manager = manager;
        Api = api;
        Config = config;
        PluginDir = pluginDir;
        DataDir = dataDir;
        Log = new PluginLog(pluginName);
        Messages = new TrackedMessageHub(pluginName, this, messages);
        Events = new TrackedEventHub(this, events);
        // 每插件独立的调度器与 KV（卸载即释放）；HTTP 全局共享单例
        Scheduler = new PluginApi.BotScheduler();
        KV = new PluginApi.PluginKeyValueStore(dataDir);
        Http = manager.SharedHttp;
    }

    // ---- 插件设置（由 PluginManager 从 plugins/<名称>/settings.json 缓存到此处） ----

    public T GetSetting<T>(string key, T? defaultValue = default)
    {
        try
        {
            if (_pluginSettings.TryGetValue(key, out var je))
            {
                if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
                if (typeof(T) == typeof(bool) && je.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return (T)(object)je.GetBoolean();
                if (typeof(T) == typeof(int))
                {
                    if ((je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var i)) || int.TryParse(je.ToString(), out i))
                        return (T)(object)i;
                }
                else if (typeof(T) == typeof(long))
                {
                    if ((je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var l)) || long.TryParse(je.ToString(), out l))
                        return (T)(object)l;
                }
                else if (typeof(T) == typeof(double))
                {
                    if ((je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d)) || double.TryParse(je.ToString(), out d))
                        return (T)(object)d;
                }
                else
                {
                    return je.Deserialize<T>() ?? defaultValue!;
                }
            }
        }
        catch { }
        return defaultValue!;
    }

    public IReadOnlyDictionary<string, object?> GetAllSettings() =>
        _pluginSettings.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);

    // ---- 命令注册（转发给 PluginManager，Dispose 时统一注销） ----
    public IDisposable RegisterCommand(string name, string description, CommandHandler handler, string usage = "")
    {
        var sub = _manager.RegisterCommand(_pluginName, name, description, handler, usage);
        lock (_lock) _commandSubs.Add(sub);
        return sub;
    }

    public Task<string?> TryInvokeCommandAsync(string name, string args = "") =>
        _manager.InvokeCommandAsync(name, args);

    public IReadOnlyList<CommandInfo> ListCommands() => _manager.GetCommands();

    private void Track(IDisposable sub)
    {
        lock (_lock) _subscriptions.Add(sub);
    }

    public void Dispose()
    {
        IDisposable[] subs;
        lock (_lock) subs = [.. _subscriptions, .. _commandSubs];
        foreach (var sub in subs)
        {
            try { sub.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"[plugin] {_pluginName} 退订异常: {ex.Message}"); }
        }
        lock (_lock) { _subscriptions.Clear(); _commandSubs.Clear(); }
        // 释放本插件专属的调度器与 KV（刷盘）
        try { Scheduler.Dispose(); } catch { }
        try { KV.Dispose(); } catch { }
    }

    private sealed class TrackedMessageHub(string tag, BotContextImpl owner, MessageRouter router) : IMessageHub
    {
        public IDisposable OnPrivate(Func<OneBotLib.Events.PrivateMessageEventArgs, Task<Handled>> handler, int priority = 0, string? tag0 = null)
        {
            var d = router.OnPrivate(handler, priority, tag0 ?? tag);
            owner.Track(d);
            return d;
        }

        public IDisposable OnGroup(Func<OneBotLib.Events.GroupMessageEventArgs, Task<Handled>> handler, int priority = 0, string? tag0 = null)
        {
            var d = router.OnGroup(handler, priority, tag0 ?? tag);
            owner.Track(d);
            return d;
        }
    }

    private sealed class TrackedEventHub(BotContextImpl owner, EventRouter router) : IEventHub
    {
        public IDisposable On<TEvent>(Func<TEvent, Task> handler, string? tag = null) where TEvent : EventArgs
        {
            var d = router.On(handler, tag);
            owner.Track(d);
            return d;
        }
    }
}

// 插件管理器：扫描 plugins/<名称>/<名称>.dll，加载到独立可回收 ALC；支持卸载/热重载
sealed class PluginManager
{
    private sealed class LoadedPlugin
    {
        public required string Name;
        public required string SourceDir;      // 原始插件目录（热重载从这重新拷贝）
        public required string ShadowDir;      // 影子目录（程序集实际加载处，避免锁源文件）
        public required IBotPlugin Instance;
        public required BotContextImpl Context;
        public required PluginLoadContext Alc;
        public required WeakReference AlcRef;
        public required PluginManifest Manifest; // 依赖声明 + 展示信息 + 设置项声明
        public bool IsScript;                    // 脚本型插件（*.cs 内存编译，无 <名>.dll）
        public readonly HashSet<string> Dependents = new(StringComparer.OrdinalIgnoreCase); // 依赖我的插件
    }

    private readonly IBotApi _api;
    private readonly IBotConfig _config;
    private readonly MessageRouter _messages;
    private readonly EventRouter _events;
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CommandRegistration> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _pluginSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cmdLock = new();
    private readonly object _lock = new();
    private readonly HostSettings _settings;

    // 最近一次加载失败的原因（编译诊断/异常消息）：让 !plugin load 的回复能带回诊断，
    // 插件工厂（pb_build）与 AI 才能直接看到 CS 错误而不是"详见控制台日志"
    private readonly Dictionary<string, string> _lastLoadError = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>失败时记录原因并打印（同时供命令回复回喂）</summary>
    private void NoteLoadFailure(string name, string reason)
    {
        lock (_lock) _lastLoadError[name] = reason;
        Console.WriteLine($"[plugin] {name} 加载失败: {reason}");
    }

    private void ClearLoadFailure(string name)
    {
        lock (_lock) _lastLoadError.Remove(name);
    }

    /// <summary>最近一次加载失败原因（无则 null）；供 UI/命令展示</summary>
    public string? LastLoadError(string name)
    {
        lock (_lock) return _lastLoadError.TryGetValue(name, out var v) ? v : null;
    }

    public string PluginRoot { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>插件名门禁：必须是"单一目录名"，不允许路径分隔符、`..`、Windows 保留字符，也不允许以点开头
    ///（后者是 .shadow/.cache/.pending 等内部目录）。所有"名字 → 路径"的入口都要过这一关，
    /// 否则 name 里塞 `..\` 就能读写 PluginRoot 之外的文件
    ///（实测：GET /api/plugins/..%5CDecoyDir/settings 能读出 PluginRoot 外的 settings.json）。</summary>
    public static bool IsSafePluginName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return false;
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name[0] == '.') return false;
        return name.All(c => !char.IsControl(c));
    }

    /// <summary>全局共享 HTTP 客户端（插件 ctx.Http 同一实例）。</summary>
    public SharedHttp SharedHttp { get; } = new();

    public PluginManager(IBotApi api, IBotConfig config, MessageRouter messages, EventRouter events, HostSettings settings)
    {
        _api = api;
        _config = config;
        _messages = messages;
        _events = events;
        _settings = settings;
    }

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

    // ===================== 卸载 / 重载 =====================

    // 卸载：自动先级联卸载全部依赖方（a 依赖 b：卸 b 时连 a 一起卸）
    public async Task<bool> UnloadAsync(string name)
    {
        if (!IsSafePluginName(name)) { Console.WriteLine($"[plugin] 卸载被拒绝：非法插件名 {name}"); return false; }
        if (PluginOpsBlocked("unload", name)) return false;
        return await UnloadAsyncCore(name, viaCascade: false);
    }

    private async Task<bool> UnloadAsyncCore(string name, bool viaCascade)
    {
        LoadedPlugin? p;
        List<string> cascade = [];
        lock (_lock)
        {
            if (!_plugins.Remove(name, out p))
            {
                if (!viaCascade) Console.WriteLine($"[plugin] 插件 {name} 未加载");
                return false;
            }
            // 收集依赖方（级联卸载：a 依赖我 → 先卸 a）
            cascade = [.. p.Dependents];
            foreach (var dep in p.Manifest.Depends)
                if (_plugins.TryGetValue(dep, out var dp))
                    dp.Dependents.Remove(name);
        }

        // 先卸载依赖方（它们引用了我的程序集实例）
        foreach (var dependent in cascade)
        {
            Console.WriteLine($"[plugin] {name} 被 {dependent} 依赖，级联卸载 {dependent}");
            await UnloadAsyncCore(dependent, viaCascade: true);
        }

        var displayName = p.Instance.Name;
        try
        {
            await p.Instance.OnUnloadAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin] {displayName} OnUnloadAsync 异常: {ex.Message}");
        }
        p.Context.Dispose(); // 兜底退订全部消息/事件/命令
        RemoveCommandsOf(name);

        var alc = p.Alc;
        var alcRef = p.AlcRef;
        var shadowDir = p.ShadowDir;
        p.Instance = null!;

        alc.Unload();
        Console.WriteLine($"[plugin] 已卸载 {displayName}，等待程序集回收...");

        // 后台验证 ALC 真正被 GC 回收（有泄漏时会一直 IsAlive）
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 150; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (!alcRef.IsAlive)
                {
                    Console.WriteLine($"[plugin] {displayName} 程序集已回收 ({i * 0.2:F1}s)");
                    try { if (Directory.Exists(shadowDir)) Directory.Delete(shadowDir, true); } catch { }
                    return;
                }
                await Task.Delay(200);
            }
            Console.WriteLine($"[plugin] [warn] {displayName} 程序集 30s 仍未回收（可能存在残留引用/未停止的后台任务），不影响继续使用");
        });
        return true;
    }

    // 重载：级联（a 依赖 b：重载 b 时先卸 a 连带重载；A 借用的 B 类型必须换新）
    public async Task<bool> ReloadAsync(string name)
    {
        if (!IsSafePluginName(name)) { Console.WriteLine($"[plugin] 重载被拒绝：非法插件名 {name}"); return false; }
        if (PluginOpsBlocked("reload", name)) return false;

        // 收集依赖方链（卸载会级联），重载时一并恢复
        List<string> dependents;
        lock (_lock) dependents = [.. (_plugins.TryGetValue(name, out var p) ? p.Dependents : new HashSet<string>())];

        await UnloadAsync(name); // 内部级联卸载依赖方
        await Task.Delay(300); // 给 ALC 卸载一点时间
        if (!await LoadAsync(name)) return false;

        // 恢复依赖方（保持原依赖顺序无所谓，LoadAsync 会递归处理依赖）
        var restored = true;
        foreach (var d in dependents)
            restored &= await LoadAsync(d);
        return restored;
    }

    public bool IsLoaded(string name)
    {
        lock (_lock) return _plugins.ContainsKey(name);
    }

    // ===================== 插件设置 =====================

    /// <summary>设置项定义：优先插件类声明的 SettingDefs，其次 plugin.json 的 Settings（脚本插件走后者）</summary>
    public IReadOnlyList<PluginSettingDef> GetSettingDefs(string name)
    {
        if (!IsSafePluginName(name)) return [];
        lock (_lock)
            if (_plugins.TryGetValue(name, out var p))
                return p.Instance.SettingDefs.Count > 0 ? p.Instance.SettingDefs : p.Manifest.Settings;
        return PluginManifest.LoadFrom(Path.Combine(PluginRoot, name)).Settings;
    }

    /// <summary>当前设置值（插件未加载也返回持久化值）</summary>
    public Dictionary<string, object?> GetSettings(string name)
    {
        if (!IsSafePluginName(name)) return [];
        var dict = LoadPluginSettings(name);
        return dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>保存设置：仅接受 defs 声明的键，按类型规范化后写插件目录的 settings.json；
    /// 插件已加载时回调 OnSettingsChangedAsync 热应用</summary>
    public async Task<(bool Ok, string? Error)> UpdateSettingsAsync(string name, Dictionary<string, object?>? values)
    {
        if (!IsSafePluginName(name)) return (false, "非法插件名");
        try
        {
            var existing = LoadPluginSettings(name);
            LoadedPlugin? p = null;
            lock (_lock) _plugins.TryGetValue(name, out p);
            // 设置项来源：插件类声明优先，其次 plugin.json 的 Settings（脚本插件靠清单声明）
            var defs = p is not null && p.Instance.SettingDefs.Count > 0
                ? p.Instance.SettingDefs
                : PluginManifest.LoadFrom(Path.Combine(PluginRoot, name)).Settings;
            // 无任何声明就不接收写入：否则空集合会把已有 settings.json 覆盖掉
            if (defs.Count == 0)
                return (false, "该插件没有声明可配置项");

            var clean = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in defs)
            {
                if (values is not null && values.TryGetValue(def.Key, out var raw) && raw is not null)
                {
                    clean[def.Key] = NormalizeSetting(raw, def.Type);
                }
                else if (existing.TryGetValue(def.Key, out var prev))
                {
                    clean[def.Key] = prev; // 未提交的项保留旧值
                }
                else if (def.Default is not null)
                {
                    clean[def.Key] = NormalizeSetting(def.Default, def.Type);
                }
            }
            existing.Clear();
            foreach (var item in clean) existing[item.Key] = item.Value;
            SavePluginSettings(name, existing, defs);

            // 这些键在插件构造时读取，变更必须热重载才生效
            var reloadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "ApiKey", "BaseUrl", "Model", "FallbackModels", "Personas" };
            var needsReload = values is not null && values.Keys.Any(k => reloadKeys.Contains(k));

            if (p is not null)
            {
                if (needsReload)
                {
                    Console.WriteLine($"[plugin] {name} 关键设置变更，自动热重载生效…");
                    var reloaded = await ReloadAsync(name);
                    Console.WriteLine($"[plugin] {name} 热重载 {(reloaded ? "完成" : "失败")}");
                }
                else
                {
                    await p.Instance.OnSettingsChangedAsync();
                    Console.WriteLine($"[plugin] {name} 设置已更新并热应用");
                }
            }
            else
            {
                Console.WriteLine($"[plugin] {name} 设置已保存（插件未加载，下次加载生效）");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // 按设置类型规范化为 JsonElement（WebUI 提交值可能是 JsonElement/基础类型）
    private static JsonElement NormalizeSetting(object raw, string? type)
    {
        var s = raw is JsonElement je ? je.ToString() : raw.ToString() ?? "";
        return (type ?? "text") switch
        {
            "number" when raw is JsonElement n && n.ValueKind == JsonValueKind.Number => n,
            "number" => double.TryParse(s, out var d) ? JsonSerializer.SerializeToElement(d) : JsonSerializer.SerializeToElement(0),
            "bool" when raw is JsonElement b && b.ValueKind is JsonValueKind.True or JsonValueKind.False => b,
            "bool" => JsonSerializer.SerializeToElement(s is "true" or "True" or "1" or "on"),
            _ => JsonSerializer.SerializeToElement(s)
        };
    }

    private string PluginSettingsPath(string name)
    {
        // 最后一道兜底：即使有入口漏了校验，这里也不接受能跑出 PluginRoot 的名字
        if (!IsSafePluginName(name)) throw new ArgumentException($"非法插件名: {name}");
        return Path.Combine(PluginRoot, name, "settings.json");
    }

    private Dictionary<string, JsonElement> LoadPluginSettings(string name)
    {
        if (!IsSafePluginName(name)) return [];
        if (_pluginSettings.TryGetValue(name, out var cached)) return cached;

        var path = PluginSettingsPath(name);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                return _pluginSettings[name] = DecryptSettings(values ?? new(StringComparer.OrdinalIgnoreCase));
            }

            // 将旧 config.json 中的嵌套 PluginSettings 自动迁移一次。
            if (_settings.PluginSettings.TryGetValue(name, out var legacy))
            {
                SavePluginSettings(name, legacy);
                _settings.PluginSettings.Remove(name);
                _settings.Save();
                Console.WriteLine($"[plugin] 已迁移 {name} 设置到 {path}");
                return _pluginSettings[name] = legacy;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin] 读取 {name} 设置失败: {ex.Message}");
        }
        return _pluginSettings[name] = new(StringComparer.OrdinalIgnoreCase);
    }

    private void SavePluginSettings(string name, Dictionary<string, JsonElement> values, IReadOnlyList<PluginSettingDef>? defs = null)
    {
        var path = PluginSettingsPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        // 落盘才加密（内存与插件始终拿明文）：只处理 defs 声明为 password 的键，且已是密文的不再套一层
        var disk = values;
        if (defs is { Count: > 0 })
        {
            var secretKeys = defs.Where(d => string.Equals(d.Type, "password", StringComparison.OrdinalIgnoreCase))
                                 .Select(d => d.Key).ToList();
            if (secretKeys.Count > 0)
            {
                disk = new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase);
                foreach (var k in secretKeys)
                    if (disk.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String)
                        disk[k] = JsonSerializer.SerializeToElement(SecretBox.Encrypt(v.GetString()));
            }
        }
        // 中文/特殊字符原样存储，不转 \uXXXX，便于人工编辑与 git diff
        File.WriteAllText(temp, JsonSerializer.Serialize(disk, BotJson.Indented));
        File.Move(temp, path, true);
    }

    /// <summary>
    /// 一次性迁移：把磁盘上仍是明文的机密设置（defs 里 type=password 的键）重新保存一遍，
    /// 从而被 SecretBox 加密。开机调用一次；已是密文的键不会被二次加密。
    /// </summary>
    public void EncryptExistingSecrets()
    {
        try
        {
            if (!SecretBox.IsReady) return;
            foreach (var dir in Directory.GetDirectories(PluginRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;
                if (!IsSafePluginName(name)) continue;
                if (!File.Exists(Path.Combine(dir, "settings.json"))) continue;

                var defs = GetSettingDefs(name);
                var secretKeys = defs.Where(d => string.Equals(d.Type, "password", StringComparison.OrdinalIgnoreCase))
                                     .Select(d => d.Key).ToList();
                if (secretKeys.Count == 0) continue;

                var values = LoadPluginSettings(name);
                var plain = secretKeys.Where(k => values.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String
                                                  && !SecretBox.IsEncrypted(v.GetString())).ToList();
                if (plain.Count == 0) continue;
                SavePluginSettings(name, values, defs);
                Console.WriteLine($"[secret] 已加密 {name} 的机密设置项: {string.Join(", ", plain)}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[secret] 迁移明文机密失败: {ex.Message}"); }
    }
    /// <summary>读取时把 enc:v1: 字符串解成明文（插件只应看到明文；非密文原样保留）</summary>
    private static Dictionary<string, JsonElement> DecryptSettings(Dictionary<string, JsonElement> src)
    {
        foreach (var key in src.Keys.ToList())
        {
            if (src[key].ValueKind != JsonValueKind.String) continue;
            var s = src[key].GetString();
            if (SecretBox.IsEncrypted(s))
                src[key] = JsonSerializer.SerializeToElement(SecretBox.Decrypt(s));
        }
        return src;
    }

    // 旧全局配置（模型/人格/前缀/Chat 设置）→ 插件设置 的一次性迁移（仅写缺失键，不覆盖用户已改值）
    public void MigrateLegacyHostSettings()
    {
        try
        {
            var agentPath = PluginSettingsPath("Agent");
            var agent = File.Exists(agentPath)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(agentPath)) ?? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var changed = false;

            // 旧 Chat 设置（插件改名前）→ Agent
            var oldChatPath = PluginSettingsPath("Chat");
            if (File.Exists(oldChatPath) && !File.Exists(agentPath))
            {
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(oldChatPath))
                             ?? new(StringComparer.OrdinalIgnoreCase))
                    agent[kv.Key] = kv.Value;
                changed = true;
                Console.WriteLine("[plugin] 已迁移 Chat 设置 → Agent");
            }

            // 全局模型/备用/人格 → Agent
            if (!string.IsNullOrWhiteSpace(_settings.Model) && PathExistsKey(agentPath, "Model") is false)
            {
                agent["ApiKey"] = JsonSerializer.SerializeToElement(_settings.ApiKey);
                agent["BaseUrl"] = JsonSerializer.SerializeToElement(_settings.BaseUrl);
                agent["Model"] = JsonSerializer.SerializeToElement(_settings.Model);
                if (_settings.FallbackModels is { Count: > 0 })
                    agent["FallbackModels"] = JsonSerializer.SerializeToElement(string.Join("\n",
                        _settings.FallbackModels.Select(f => $"{f.BaseUrl}|{f.Model}|{f.ApiKey}")));
                changed = true;
                Console.WriteLine("[plugin] 已迁移全局模型配置 → Agent 设置");
            }
            if (_settings.Personas is { Count: > 0 } && PathExistsKey(agentPath, "Personas") is false)
            {
                var active = _settings.Personas.FirstOrDefault(p => p.Enabled) ?? _settings.Personas[0];
                agent["Personas"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { name = active.Name, enabled = true, instructions = active.Instructions }
                });
                changed = true;
                Console.WriteLine("[plugin] 已迁移人格 → Agent 设置（Personas）");
            }
            // 旧管线格式 FallbackModels（"url|model|key" 每行）→ JSON 数组
            if (agent.TryGetValue("FallbackModels", out var fbRaw) && fbRaw.ValueKind == JsonValueKind.String)
            {
                var lines = (fbRaw.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var arr = lines.Select(l =>
                    {
                        var p = l.Split('|');
                        return new { baseUrl = p[0].Trim(), model = p.ElementAtOrDefault(1)?.Trim() ?? "", apiKey = p.ElementAtOrDefault(2)?.Trim() ?? "" };
                    }).Where(x => x.model.Length > 0).ToList();
                    agent["FallbackModels"] = JsonSerializer.SerializeToElement(arr);
                    changed = true;
                    Console.WriteLine("[plugin] 备用模型格式已升级为结构化数组");
                }
            }
            // 旧单人格键 → Personas 数组
            if (agent.ContainsKey("PersonaInstructions") && !agent.ContainsKey("Personas"))
            {
                agent["Personas"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { name = agent.TryGetValue("PersonaName", out var n) ? n.GetString() : "默认人格",
                          enabled = true,
                          instructions = agent["PersonaInstructions"].GetString() ?? "" }
                });
                agent.Remove("PersonaName");
                agent.Remove("PersonaInstructions");
                changed = true;
                Console.WriteLine("[plugin] 单人格键已升级为 Personas 数组");
            }
            if (changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
                File.WriteAllText(agentPath, JsonSerializer.Serialize(agent, BotJson.Indented));
            }

            // 命令前缀 → Admin
            if (!string.IsNullOrEmpty(_settings.CommandPrefix) && _settings.CommandPrefix != "!")
            {
                var adminPath = PluginSettingsPath("Admin");
                if (PathExistsKey(adminPath, "CommandPrefix") is false)
                {
                    var admin = File.Exists(adminPath)
                        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(adminPath)) ?? new(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    admin["CommandPrefix"] = JsonSerializer.SerializeToElement(_settings.CommandPrefix);
                    Directory.CreateDirectory(Path.GetDirectoryName(adminPath)!);
                    File.WriteAllText(adminPath, JsonSerializer.Serialize(admin, BotJson.Indented));
                    Console.WriteLine("[plugin] 已迁移命令前缀 → Admin 设置");
                }
            }

            // 迁移全部完成：清掉内存中的旧全局模型/人格值（config.json 也已通过 ShouldSerialize* 不再写出）
            _settings.ClearLegacyModelSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin] 旧配置迁移失败（忽略，用默认值）: {ex.Message}");
        }
    }

    private static bool? PathExistsKey(string settingsPath, string key)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(settingsPath))
                ?.ContainsKey(key);
        }
        catch { return null; }
    }

    // 插件快照（WebUI 用）：含已加载与磁盘上未加载的插件
    public List<PluginInfo> GetSnapshot()
    {
        var list = new List<PluginInfo>();
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var p in _plugins.Values)
            {
                var (src, model) = ProvenanceOf(p.Manifest, p.SourceDir);
                list.Add(new PluginInfo(p.Name, p.Instance.Version, p.Instance.Description, true, IsAutoload(p.Name), src, model));
                loadedNames.Add(p.Name);
            }
        }
        if (Directory.Exists(PluginRoot))
        {
            foreach (var dir in Directory.GetDirectories(PluginRoot))
            {
                var n = Path.GetFileName(dir);
                if (n.StartsWith('.')) continue;
                if (loadedNames.Contains(n)) continue;
                if (!IsPluginDir(dir, n)) continue;
                // 未加载时用 plugin.json 的展示信息兜底（脚本插件的版本/描述主要来自清单）
                var mf = PluginManifest.LoadFrom(dir);
                var (src, model) = ProvenanceOf(mf, dir);
                list.Add(new PluginInfo(n, mf.Version ?? "", mf.Description ?? "", false, IsAutoload(n), src, model));
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 是否随启动自动加载（未配置默认 true）
    public bool IsAutoload(string name) =>
        !_settings.PluginAutoload.TryGetValue(name, out var enabled) || enabled;

    /// <summary>来源判定：清单里的 Provenance 优先；缺失时看有没有生成日志（pb_build 生成过 → manual）；
    /// 两者都没有 → handmade（内置 / 手工放入 / zip 安装）</summary>
    private static (string Source, string? Model) ProvenanceOf(PluginManifest mf, string dir)
    {
        if (mf.Provenance?.By is { Length: > 0 } by)
            return (by, mf.Provenance.Model);
        return (File.Exists(Path.Combine(dir, "generation.log")) ? "manual" : "handmade", null);
    }

    public record PluginInfo(string Name, string Version, string Description, bool Loaded, bool AutoLoad,
        string Source, string? Model);

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
            static bool IsUnsafeRelPath(string rel) =>
                rel.Length == 0
                || rel.StartsWith('/')
                || rel.Contains(':')
                || Path.IsPathRooted(rel)
                || rel.Split('/').Any(seg => seg == "..");

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
