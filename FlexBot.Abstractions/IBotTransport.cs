using OneBotLib.Api;
using OneBotLib.Events;

namespace FlexBot.PluginApi;

/// <summary>
/// 机器人传输通道：宿主（事件派发、命令门禁、WebUI）与插件（出站调用）都只看这一个面。
/// 两套实现：
///   · OneBot（LLBot / NapCat，WebSocket）—— 现有链路，功能最全；
///   · QQ 官方机器人开放平台（AppID + AppSecret）—— 合规链路，能力是前者的子集。
/// 由 HostSettings.Transport 选择，同一时刻只启用一个（不做双通道并行，避免跨通道去重与一致性）。
/// </summary>
/// <remarks>
/// 事件面刻意与 OneBotLib.Api.OneBotClient 保持一致：宿主的事件接线、路由、权限、记忆等逻辑
/// 因此对"换通道"完全无感；官方实现只触发它真能提供的那部分事件，其余永不触发。
/// </remarks>
public interface IBotTransport : IBotApi, IAsyncDisposable
{
    /// <summary>通道名（"onebot" / "official"），用于日志与状态展示</summary>
    string TransportName { get; }

    // SelfId 已由 IBotApi 提供（本体账号），这里不重复声明

    /// <summary>连接。OneBot 用 url+token；官方实现忽略这两个参数（凭配置里的 AppID/AppSecret 自行取 token）</summary>
    Task ConnectAsync(string url, string token);

    /// <summary>主动断开（供 WebUI 的"断开"按钮使用）</summary>
    Task DisconnectAsync();

    // ---------------- 入站事件（与 OneBotClient 同名同型） ----------------

    event EventHandler<MessageEventArgs>? OnMessage;
    event EventHandler<PrivateMessageEventArgs>? OnPrivateMessage;
    event EventHandler<GroupMessageEventArgs>? OnGroupMessage;
    event EventHandler<MessageSentEventArgs>? OnMessageSent;
    event EventHandler<PrivateMessageSentEventArgs>? OnPrivateMessageSent;
    event EventHandler<LifecycleEventArgs>? OnLifecycle;
    event EventHandler<HeartbeatEventArgs>? OnHeartbeat;
    event EventHandler<ConnectionStateChangedEventArgs>? OnConnectionStateChanged;
    event EventHandler<GroupMemberChangeEventArgs>? OnGroupMemberChange;
    event EventHandler<GroupAdminEventArgs>? OnGroupAdmin;
    event EventHandler<GroupBanEventArgs>? OnGroupBan;
    event EventHandler<GroupUploadEventArgs>? OnGroupUpload;
    event EventHandler<GroupRecallEventArgs>? OnGroupRecall;
    event EventHandler<GroupPokeEventArgs>? OnGroupPoke;
    event EventHandler<GroupLuckyKingEventArgs>? OnGroupLuckyKing;
    event EventHandler<GroupHonorEventArgs>? OnGroupHonor;
    event EventHandler<GroupCardChangeEventArgs>? OnGroupCardChange;
    event EventHandler<FriendAddEventArgs>? OnFriendAdd;
    event EventHandler<FriendRecallEventArgs>? OnFriendRecall;
    event EventHandler<FriendPokeEventArgs>? OnFriendPoke;
    event EventHandler<ClientStatusEventArgs>? OnClientStatus;
    event EventHandler<FriendRequestEventArgs>? OnFriendRequest;
    event EventHandler<GroupRequestEventArgs>? OnGroupRequest;
}
