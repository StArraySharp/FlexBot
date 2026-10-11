using System.Text.Json;
using FlexBot.PluginApi;

namespace FlexBot;

// 默认配置（config.json 缺失/缺项时兜底；本地部署，不公开）
static class BotConfig
{
    public const string WsUrl = "ws://127.0.0.1:3001";
    public const string Token = "";

    public const string ApiKey = "";
    public const string BaseUrl = "";
    public const string Model = "";
    // 主人 QQ 的**编译期默认值故意留空**：主人身份只应来自 config.json / WebUI，
    // 不要把个人 QQ 写进源码（换人不必改代码，也不会随仓库泄露）。未配置时宿主按"无管理员"运行：
    // 私聊与所有插件命令都会被拒绝（fail-closed），WebUI 也会要求先填 OwnerUin。
    public const long OwnerUin = 0;
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

    /// <summary>机器人通道："onebot"（LLBot/NapCat，默认；功能最全）或 "official"（QQ 官方机器人开放平台，合规但能力是子集）。同一时刻只启用一个。</summary>
    public string Transport { get; set; } = "onebot";

    /// <summary>官方通道：开放平台的 AppID（机器人身份）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("OfficialAppId")]
    public string OfficialAppId { get; set; } = "";
    /// <summary>官方通道：开放平台的 AppSecret（等同机器人密码，勿泄露）。token 由库里自行获取与刷新。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("OfficialAppSecret")]
    public string OfficialAppSecret { get; set; } = "";
    /// <summary>官方通道：true=沙箱环境，false=正式环境</summary>
    [System.Text.Json.Serialization.JsonPropertyName("OfficialSandbox")]
    public bool OfficialSandbox { get; set; } = true;
    /// <summary>官方通道：是否把命令注册成客户端可见的「指令面板」。
    /// 默认 false —— 平台对指令条目有硬上限（实测本 bot 为 10 条/场景，超出报 30013），
    /// 命令多的时候装不下、还容易被截断误导，故不注册；命令照旧用「前缀+命令名」直接发。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("OfficialRegisterCommands")]
    public bool OfficialRegisterCommands { get; set; } = false;
    /// <summary>全局命令前缀（消息以该前缀开头才会进入命令分发；默认 "!"）。</summary>
    public string CommandPrefix { get; set; } = "!";

    public string ApiKey { get; set; } = BotConfig.ApiKey;
    public string BaseUrl { get; set; } = BotConfig.BaseUrl;
    public string Model { get; set; } = BotConfig.Model;
    /// <summary>备用模型列表（按顺序回落；主模型失败时依次尝试）。</summary>
    public List<FallbackModelSettings> FallbackModels { get; set; } = [];
    public long OwnerUin { get; set; } = BotConfig.OwnerUin;
    /// <summary>额外管理员（**不含主人**）；OwnerUin 始终自动具有管理员权限。
    /// 属性名刻意与 <c>HostConfig.AdminUins</c>（含主人的有效列表）区分，避免同名不同义。
    /// JSON 键仍是 "AdminUins"，兼容既有 config.json。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("AdminUins")]
    public List<long> ExtraAdminUins { get; set; } = [];
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

    // ---- 插件 NuGet 依赖解析 ----
    /// <summary>插件的 NuGet 依赖源（flat-container 格式）；插件在 plugin.json 的 NuGet 字段声明包，
    /// 加载时自动下载并铺入该插件的 libs/。可指向私有源/镜像。</summary>
    public string NuGetFeed { get; set; } = "https://api.nuget.org/v3-flatcontainer";

    // ---- WebUI 端口配置 ----
    /// <summary>WebUI 监听端口（1024-65535；不允许常见端口如 80/443/3000 等）；0 表示自动分配</summary>
    public int WebUiPort { get; set; } = 0;
    /// <summary>WebUI 登录密码（SHA256 十六进制；手写明文会在启动时自动转为 SHA256）。
    /// 留空则首次启动**自动生成随机 16 位密码**并打印在控制台（明文只打印一次、不写日志），因此不存在"无密码开放"的窗口。
    /// 想换密码：WebUI → 设置 → 登录密码。WebUI 绑定 0.0.0.0，不要把端口暴露到公网。</summary>
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
    public IReadOnlyCollection<long> AllAdminUins => [OwnerUin, .. ExtraAdminUins.Where(x => x > 0 && x != OwnerUin).Distinct()];

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
        ExtraAdminUins = ExtraAdminUins.Where(x => x > 0 && x != OwnerUin).Distinct().ToList();
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
    /// <summary>**有效管理员列表**：已含主人（= HostSettings.AllAdminUins）。插件与宿主门禁都用这个。</summary>
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
