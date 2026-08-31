namespace FlexBot.PluginApi;

/// <summary>已注册命令的元数据。</summary>
public sealed record CommandInfo(
    string Name,              // 命令名（不含前缀，如 "ping"）
    string Description,       // 一行说明
    string PluginName,        // 注册者插件名
    string Usage = "");       // 用法示例（可选，如 "ping [次数]"）

/// <summary>命令处理器：接收去掉前缀与命令名后的原始参数串。</summary>
public delegate Task<string> CommandHandler(string args);

/// <summary>宿主提供给插件的运行环境。Dispose 时自动退订该插件注册的全部消息/事件/命令（卸载兜底）。</summary>
public interface IBotContext : IDisposable
{
    IBotApi Api { get; }
    IBotConfig Config { get; }

    /// <summary>插件 DLL 所在目录（只读资源、说明文件放这里）</summary>
    string PluginDir { get; }

    /// <summary>插件专属数据目录（持久化数据放这里）</summary>
    string DataDir { get; }

    IMessageHub Messages { get; }
    IEventHub Events { get; }
    ILog Log { get; }

    /// <summary>共享定时调度器（周期/每日/延迟任务；插件卸载时自动取消其全部任务）</summary>
    IBotScheduler Scheduler { get; }

    /// <summary>插件 KV 存储（DataDir/kv.json 持久化，防抖落盘）</summary>
    PluginKeyValueStore KV { get; }

    /// <summary>共享 HTTP 客户端（统一 UA/超时/解压）</summary>
    SharedHttp Http { get; }

    /// <summary>读取本插件设置（未设置时返回 defaultValue；WebUI 保存后对新读取立即生效）</summary>
    T GetSetting<T>(string key, T? defaultValue = default);

    /// <summary>本插件当前全部设置（key → 值）</summary>
    IReadOnlyDictionary<string, object?> GetAllSettings();

    /// <summary>注册命令：消息以「前缀+命令名」开头且发送者是管理员时，宿主调用 handler(args) 并回复其返回值。
    /// 卸载插件时自动注销。返回 IDisposable 用于手动注销。</summary>
    IDisposable RegisterCommand(string name, string description, CommandHandler handler, string usage = "");

    /// <summary>调用已注册命令（跨插件协作入口）。不存在返回 null；存在则执行并返回其结果文本。</summary>
    Task<string?> TryInvokeCommandAsync(string name, string args = "");

    /// <summary>当前全部已注册命令（供 AI/插件发现可用能力）。</summary>
    IReadOnlyList<CommandInfo> ListCommands();
}

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}
