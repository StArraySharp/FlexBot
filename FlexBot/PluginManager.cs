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

    private sealed class NullNotes : IMemoryNotes
    {
        public void Add(string s, string k, string t, int i, string g) { }
        public IReadOnlyList<MemoryNote> List(string s, int l = 50) => [];
    }
    private static readonly NullNotes NullNotesInstance = new();
    public IMemoryNotes Notes => MemoryNotesStore.Current ?? (IMemoryNotes)NullNotesInstance;
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
sealed partial class PluginManager
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

}
