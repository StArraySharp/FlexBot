namespace FlexBot.PluginApi;

/// <summary>一个可用的 LLM 端点（API Key + 地址 + 模型名）。</summary>
public sealed record ModelEndpoint(string ApiKey, string BaseUrl, string Model);

/// <summary>全局配置快照（由宿主实现，值来自宿主 Config）。</summary>
public interface IBotConfig
{
    long OwnerUin { get; }
    IReadOnlyCollection<long> AdminUins { get; }
    string MemoryDir { get; }

    /// <summary>全局命令前缀（默认 "!"）。</summary>
    string CommandPrefix { get; }

    /// <summary>当前启用人格的系统提示词。</summary>
    string ActivePersonaInstructions { get; }

    // ---- LLM ----
    string ApiKey { get; }
    string BaseUrl { get; }
    string Model { get; }
    /// <summary>模型是否支持视觉（deepseek 系列不支持）</summary>
    bool IsVisionModel { get; }

    /// <summary>备用模型列表（按顺序回落；主模型失败时依次尝试）。</summary>
    IReadOnlyList<ModelEndpoint> FallbackModels { get; }

    // ---- 上下文 / 性能参数 ----
    int MaxContextTurns { get; }
    int MaxMsgChars { get; }
    int MaxImageBytes { get; }
    int GroupHistoryCount { get; }
    int GroupHistoryUseCount { get; }
    int GroupHistoryMaxImgs { get; }
    int ContextCacheSeconds { get; }
    int SearchPerTurnLimit { get; }
}
