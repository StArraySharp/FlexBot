using System.Text.Json;
using FlexBot.PluginApi;

namespace FlexBot;

// 默认配置（config.json 缺失/缺项时兜底；本地部署，不公开）
static class BotConfig
{
    public const string WsUrl = "ws://127.0.0.1:3001";
    public const string Token = "";

    public const string ApiKey = "";
    public const string BaseUrl = "https://api.deepseek.com";
    public const string Model = "deepseek-v4-flash";
    public const long OwnerUin = 3058465749;
    // 记忆/日志/插件数据根目录：默认输出目录（exe 旁）下的 memory/
    public static readonly string MemoryDir = Path.Combine(AppContext.BaseDirectory, "memory");

    // 模型是否支持视觉（deepseek 系列不支持图像/多模态，传 image_url 会报 400）
    public static bool IsVisionOf(string model) =>
        !model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase);

    // ---- 上下文 / 性能参数（高级项，随默认值） ----
    public const int MaxContextTurns = 20;      // 会话历史保留轮次上限
    public const int MaxMsgChars = 3000;        // 单条消息文本长度上限
    public const int MaxImageBytes = 10 * 1024 * 1024; // 图片大小上限 10MB
    public const int GroupHistoryCount = 100;   // 拉取群历史条数
    public const int GroupHistoryUseCount = 100; // 实际取多少条（取全部拉到的）
    public const int GroupHistoryMaxImgs = 4;   // 历史图片最多几张
    public const int ContextCacheSeconds = 15;  // 群历史缓存秒数
    public const int SearchPerTurnLimit = 3;    // 每轮对话 search_web 上限
}

// 运行时配置：config.json（exe 旁），可经 WebUI 修改并热生效（改模型/提示词后重载 Chat 即应用）
class HostSettings
{
    public string WsUrl { get; set; } = BotConfig.WsUrl;
    public string Token { get; set; } = BotConfig.Token;
    /// <summary>全局命令前缀（消息以该前缀开头才会进入命令分发；默认 "!"）。</summary>
    public string CommandPrefix { get; set; } = "!";

    public string ApiKey { get; set; } = BotConfig.ApiKey;
    public string BaseUrl { get; set; } = BotConfig.BaseUrl;
    public string Model { get; set; } = BotConfig.Model;
    /// <summary>备用模型列表（按顺序回落；主模型失败时依次尝试）。</summary>
    public List<FallbackModelSettings> FallbackModels { get; set; } = [];
    public long OwnerUin { get; set; } = BotConfig.OwnerUin;
    /// <summary>额外管理员；OwnerUin 始终自动具有管理员权限。</summary>
    public List<long> AdminUins { get; set; } = [];
    /// <summary>可编辑人格；必须且只能有一个处于启用状态。</summary>
    public List<PersonaSettings> Personas { get; set; } = [];
    public string MemoryDir { get; set; } = BotConfig.MemoryDir;

    // ---- 旧全局模型/人格/前缀配置：已迁移到 Agent/Admin 插件设置，不再写入 config.json ----
    // 保留字段仅为读取旧文件完成一次性迁移（MigrateLegacyHostSettings），迁移后 ClearLegacyModelSettings 清空
    public bool ShouldSerializeCommandPrefix() => false;
    public bool ShouldSerializeApiKey() => false;
    public bool ShouldSerializeBaseUrl() => false;
    public bool ShouldSerializeModel() => false;
    public bool ShouldSerializeFallbackModels() => false;
    public bool ShouldSerializePersonas() => false;

    /// <summary>旧全局模型/人格配置迁移完成后调用：内存中清空，避免继续作为插件默认值残留。</summary>
    public void ClearLegacyModelSettings()
    {
        CommandPrefix = "!";
        ApiKey = BotConfig.ApiKey;
        BaseUrl = BotConfig.BaseUrl;
        Model = BotConfig.Model;
        FallbackModels = [];
        Personas = [];
    }

    /// <summary>配置保存后是否自动重载已加载插件（默认关闭）。</summary>
    public bool ReloadPluginsAfterSave { get; set; }

    /// <summary>插件是否随启动自动加载（缺省=true）；仅影响启动加载，不影响已加载插件</summary>
    public Dictionary<string, bool> PluginAutoload { get; set; } = [];

    // ---- 启动命令配置（用于一键启动 NapCat 和 LLBot） ----
    /// <summary>NapCat 启动命令（如 "D:\\NapCat\\launcher.bat"）；留空则不启动</summary>
    public string NapCatCmd { get; set; } = "";
    /// <summary>LLBot 启动命令（如 "D:\\LLBot\\start.bat"）；留空则不启动</summary>
    public string LLBotCmd { get; set; } = "";

    // ---- 日志隔离配置 ----
    /// <summary>是否为 NapCat/LLBot 重定向日志到独立文件（true 时避免混入主日志）</summary>
    public bool IsolateDependencyLogs { get; set; } = true;
    /// <summary>日志目录；留空则使用 MemoryDir/logs</summary>
    public string LogDir { get; set; } = "";

    // ---- WebUI 端口配置 ----
    /// <summary>WebUI 监听端口（1024-65535；不允许常见端口如 80/443/3000 等）；0 表示自动分配</summary>
    public int WebUiPort { get; set; } = 0;
    /// <summary>WebUI 绑定全部网卡（0.0.0.0，公网可达）；默认 false 仅 127.0.0.1。公网暴露必须同时设 WebUiPassword。</summary>
    public bool WebUiBindAll { get; set; }
    /// <summary>WebUI 登录密码；非空时启用登录页认证。公网暴露强烈建议设置。</summary>
    public string WebUiPassword { get; set; } = "";
    /// <summary>WebUI 界面（卡片/导航）不透明度百分比 0-100，默认 100。</summary>
    public int WebUiUiOpacity { get; set; } = 100;
    /// <summary>WebUI 控件（按钮/输入框/滑条）不透明度百分比 0-100，默认 100。</summary>
    public int WebUiCtlOpacity { get; set; } = 100;
    /// <summary>WebUI 背景图不透明度百分比 0-100，默认 45（有背景图时生效）。</summary>
    public int WebUiBgOpacity { get; set; } = 45;

    /// <summary>旧版全局插件设置的迁移缓存；新设置保存在 plugins/&lt;名称&gt;/settings.json。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, Dictionary<string, JsonElement>> PluginSettings { get; set; } = [];

    /// <summary>兼容读取旧 config.json 的 PluginSettings；保存时不再写回。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("PluginSettings")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, JsonElement>>? LegacyPluginSettings
    {
        get => null;
        set
        {
            if (value is not null) PluginSettings = value;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; private set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVisionModel => BotConfig.IsVisionOf(Model);

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<long> AllAdminUins => [OwnerUin, .. AdminUins.Where(x => x > 0 && x != OwnerUin).Distinct()];

    [System.Text.Json.Serialization.JsonIgnore]
    public PersonaSettings? ActivePersona => Personas.FirstOrDefault(x => x.Enabled);

    public static HostSettings Load()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
        HostSettings s = new();
        try
        {
            if (File.Exists(path))
                s = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path)) ?? new HostSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] 读取 config.json 失败，使用默认值: {ex.Message}");
            s = new HostSettings();
        }
        s.FilePath = path;
        s.NormalizeAccessSettings();
        // 同时移除旧版写入 config.json 的 PluginSettings 字段。
        s.Save();
        return s;
    }

    public void NormalizeAccessSettings()
    {
        AdminUins = AdminUins.Where(x => x > 0 && x != OwnerUin).Distinct().ToList();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, BotJson.Indented));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] 保存 config.json 失败: {ex.Message}");
            throw;
        }
    }
}

class PersonaSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新人格";
    public string Instructions { get; set; } = "";
    public bool Enabled { get; set; }
}

class FallbackModelSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}

// 把运行时设置适配为插件可见的 IBotConfig（属性穿透读取，保存后立即对新代码生效）
class HostConfig(HostSettings s) : IBotConfig
{
    public long OwnerUin => s.OwnerUin;
    public IReadOnlyCollection<long> AdminUins => s.AllAdminUins;
    public string MemoryDir => s.MemoryDir;
    /// <summary>全局命令前缀。</summary>
    public string CommandPrefix => string.IsNullOrEmpty(s.CommandPrefix) ? "!" : s.CommandPrefix;
    public string ActivePersonaInstructions => s.ActivePersona?.Instructions ?? "";
    public string ApiKey => s.ApiKey;
    public string BaseUrl => s.BaseUrl;
    public string Model => s.Model;
    public bool IsVisionModel => s.IsVisionModel;
    public IReadOnlyList<ModelEndpoint> FallbackModels =>
        s.FallbackModels.Where(x => !string.IsNullOrWhiteSpace(x.BaseUrl) && !string.IsNullOrWhiteSpace(x.Model))
            .Select(x => new ModelEndpoint(x.ApiKey, x.BaseUrl.Trim(), x.Model.Trim()))
            .ToList();
    public int MaxContextTurns => BotConfig.MaxContextTurns;
    public int MaxMsgChars => BotConfig.MaxMsgChars;
    public int MaxImageBytes => BotConfig.MaxImageBytes;
    public int GroupHistoryCount => BotConfig.GroupHistoryCount;
    public int GroupHistoryUseCount => BotConfig.GroupHistoryUseCount;
    public int GroupHistoryMaxImgs => BotConfig.GroupHistoryMaxImgs;
    public int ContextCacheSeconds => BotConfig.ContextCacheSeconds;
    public int SearchPerTurnLimit => BotConfig.SearchPerTurnLimit;
}
