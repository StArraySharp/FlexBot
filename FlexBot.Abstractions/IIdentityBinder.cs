namespace FlexBot.PluginApi;

/// <summary>
/// 身份绑定（只有官方通道需要）：官方只给 openid，宿主全链路却按 QQ 号判权限/统计。
/// 实现者把"openid 映射出的合成身份"绑到真实 QQ 号上，之后该身份的权限、每人每天统计等立即按 QQ 号生效。
/// </summary>
/// <remarks>
/// 安全：绑定等同于提权（绑到主人的 QQ 号即拿到主人权限），因此必须由实现者校验口令——
/// 口令在启动时随机生成、只打印在真实控制台（不进日志、不进 WebUI 日志缓冲）。
/// </remarks>
public interface IIdentityBinder
{
    /// <summary>把身份绑到 QQ 号。<paramref name="idOrQq"/> 可以是合成身份，也可以是其已绑定的 QQ 号。
    /// 成功返回 null，失败返回可读原因。</summary>
    string? TryBind(long idOrQq, long qq, string? code);

    /// <summary>启用了身份绑定（官方通道）时为 true，宿主据此决定是否接受 bind 命令</summary>
    bool BindingRequired { get; }
}
