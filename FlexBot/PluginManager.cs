using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FlexBot.PluginApi;

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
        "FlexBot",  // 宿主程序集（含 PluginApi 契约层 + Routing 等共享类型）
        "OneBotLib"
    };

    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
            return null; // 回落到默认上下文（与宿主共享同一份类型）
        // ★ 依赖插件：借用其 ALC 的实例（不重复加载；其"家"ALC 决定卸载语义）
        if (assemblyName.Name is not null &&
            dependencyAssemblies.TryGetValue(assemblyName.Name, out var borrowed))
            return borrowed;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

// 插件清单（plugins/<名>/plugin.json）：声明依赖
class PluginManifest
{
    public List<string> Depends { get; set; } = [];

    public static PluginManifest LoadFrom(string pluginDir)
    {
        try
        {
            var file = Path.Combine(pluginDir, "plugin.json");
            if (!File.Exists(file)) return new PluginManifest();
            var m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file));
            return m ?? new PluginManifest();
        }
        catch
        {
            return new PluginManifest();
        }
    }
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
        public required PluginManifest Manifest; // 依赖声明
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

    public string PluginRoot { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");

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
        if (!Directory.Exists(PluginRoot))
        {
            Console.WriteLine($"[plugin] 插件目录不存在: {PluginRoot}");
            return;
        }

        // 一次性迁移旧全局配置（模型/人格/前缀/Chat→Agent）到插件设置
        MigrateLegacyHostSettings();

        // 收集候选（含清单），按依赖拓扑排序：被依赖者优先
        var candidates = new List<(string Name, string Dir, PluginManifest Manifest)>();
        foreach (var dir in Directory.GetDirectories(PluginRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            var n = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, n + ".dll"))) continue;
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

        try
        {
            // 影子拷贝：复制到 .shadow 下的唯一目录再加载，避免锁住源 DLL（构建/覆盖不受影响）；
            // 目录带时间戳后缀：旧程序集可能尚未被 GC 回收（文件仍被锁），不能原地覆盖
            var shadowRoot = Path.Combine(PluginRoot, ".shadow", $"{name}_{DateTime.Now.Ticks}");
            CopyDirectory(sourceDir, shadowRoot);
            var shadowDll = Path.Combine(shadowRoot, name + ".dll");

            var alc = new PluginLoadContext(shadowDll, dependencyAssemblies);
            var asm = alc.LoadFromAssemblyPath(shadowDll);
            var pluginType = asm.GetTypes()
                .FirstOrDefault(t => typeof(IBotPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
            if (pluginType is null)
            {
                alc.Unload();
                Console.WriteLine($"[plugin] {name}: 未找到 IBotPlugin 实现");
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
                Manifest = manifest
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
            var depNote = dependencyRecords.Count > 0 ? $"（依赖: {string.Join(", ", dependencyRecords)}）" : "";
            Console.WriteLine($"[plugin] 已加载 {instance.Name} v{instance.Version} - {instance.Description}{depNote}");

            // 顺带清理同插件的历史影子目录（旧 ALC 可能还没释放文件锁，删不掉就留给下次）
            CleanupShadowDirs(name, keep: shadowRoot);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[plugin] 加载 {name} 失败: {ex}");
            return false;
        }
    }

    // ===================== 卸载 / 重载 =====================

    // 卸载：自动先级联卸载全部依赖方（a 依赖 b：卸 b 时连 a 一起卸）
    public async Task<bool> UnloadAsync(string name)
    {
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

    /// <summary>设置项定义（来自已加载实例；未加载返回空）</summary>
    public IReadOnlyList<PluginSettingDef> GetSettingDefs(string name)
    {
        lock (_lock) return _plugins.TryGetValue(name, out var p) ? p.Instance.SettingDefs : [];
    }

    /// <summary>当前设置值（插件未加载也返回持久化值）</summary>
    public Dictionary<string, object?> GetSettings(string name)
    {
        var dict = LoadPluginSettings(name);
        return dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>保存设置：仅接受 defs 声明的键，按类型规范化后写插件目录的 settings.json；
    /// 插件已加载时回调 OnSettingsChangedAsync 热应用</summary>
    public async Task<(bool Ok, string? Error)> UpdateSettingsAsync(string name, Dictionary<string, object?>? values)
    {
        try
        {
            var existing = LoadPluginSettings(name);
            LoadedPlugin? p = null;
            lock (_lock) _plugins.TryGetValue(name, out p);
            var defs = p?.Instance.SettingDefs ?? [];
            if (defs.Count == 0 && p is not null)
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
            SavePluginSettings(name, existing);

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

    private string PluginSettingsPath(string name) => Path.Combine(PluginRoot, name, "settings.json");

    private Dictionary<string, JsonElement> LoadPluginSettings(string name)
    {
        if (_pluginSettings.TryGetValue(name, out var cached)) return cached;

        var path = PluginSettingsPath(name);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                return _pluginSettings[name] = values ?? new(StringComparer.OrdinalIgnoreCase);
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

    private void SavePluginSettings(string name, Dictionary<string, JsonElement> values)
    {
        var path = PluginSettingsPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        // 中文/特殊字符原样存储，不转 \uXXXX，便于人工编辑与 git diff
        File.WriteAllText(temp, JsonSerializer.Serialize(values, BotJson.Indented));
        File.Move(temp, path, true);
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
                list.Add(new PluginInfo(p.Name, p.Instance.Version, p.Instance.Description, true, IsAutoload(p.Name)));
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
                if (File.Exists(Path.Combine(dir, n + ".dll")))
                    list.Add(new PluginInfo(n, "", "", false, IsAutoload(n)));
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 是否随启动自动加载（未配置默认 true）
    public bool IsAutoload(string name) =>
        !_settings.PluginAutoload.TryGetValue(name, out var enabled) || enabled;

    public record PluginInfo(string Name, string Version, string Description, bool Loaded, bool AutoLoad);

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
                sb.AppendLine($"  [已加载] {p.Name} v{p.Instance.Version} - {p.Instance.Description}{deps}{depsOn}");
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
                if (File.Exists(Path.Combine(dir, n + ".dll")))
                    sb.AppendLine($"  [未加载] {n}");
            }
        }
        sb.Append("命令: !plugin load|unload|reload <名称>");
        return sb.ToString().TrimEnd();
    }

    // !plugin 命令入口（宿主级，仅主人私聊可用）
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
                return arg.Length == 0 ? "用法: !plugin load <名称>"
                    : await LoadAsync(arg) ? $"已加载插件 {arg}" : $"加载 {arg} 失败（详见控制台日志）";
            case "unload":
                return arg.Length == 0 ? "用法: !plugin unload <名称>"
                    : await UnloadAsync(arg) ? $"已卸载插件 {arg}" : $"卸载 {arg} 失败";
            case "reload":
                return arg.Length == 0 ? "用法: !plugin reload <名称>"
                    : await ReloadAsync(arg) ? $"已重载插件 {arg}" : $"重载 {arg} 失败（详见控制台日志）";
            default:
                return "用法: !plugin [list|load|unload|reload] [名称]";
        }
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
        // 形态 2：插件名 → plugins/<名>/<名>.dll
        var dir = Path.Combine(PluginRoot, dllOrName);
        return File.Exists(Path.Combine(dir, dllOrName + ".dll")) ? dir : null;
    }

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
