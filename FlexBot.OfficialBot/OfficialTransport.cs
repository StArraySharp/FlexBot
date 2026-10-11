using System.Collections.Concurrent;
using System.Text.Json;
using FlexBot.PluginApi;
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Events;
using OneBotLib.Models;
using QQBot;
using QQBot.WebSocket;
// QQBot.* 与 OneBotLib.* 有同名类型（GroupInfo / ConnectionState），显式起别名避免歧义
using OneBotConnectionState = OneBotLib.Api.ConnectionState;
using OneBotGroupInfo = OneBotLib.Models.GroupInfo;

namespace FlexBot.OfficialBot;

public sealed class OfficialTransportOptions
{
    public string AppId { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public string MemoryDir { get; init; } = "memory";
    public bool Sandbox { get; init; } = true;
}

/// <summary>
/// QQ 官方机器人开放平台的传输通道实现（一期）。
///
/// 协议层全交给 QQBot.Net：token 获取与刷新、网关、intents、心跳/断线 resume、msg_seq 递增、
/// 发送限流队列、富媒体上传都由库负责；本类只做两件事：
///   1) 把官方事件（群 @ / 单聊）翻译成 OneBot 形状的 MessageObject，喂给宿主既有的事件派发；
///   2) 把插件的 IBotApi 调用翻译成官方 REST 调用，能力不足的直接返回失败并说明原因。
///
/// 一期的能力边界（官方 API 本身也不提供或需要额外链路，故先明确失败而不是静默失效）：
///   支持：群/单聊文本收发（含被动回复）、撤回自己发的消息
///   未支持：成员列表、历史消息、合并转发、群名片/头衔、禁言、poke、文件、好友列表、@ 与图片段渲染
/// 身份：官方只给 openid，统一经 IdMap 映射成合成 long；绑定真实 QQ 号后 OwnerUin 那套权限/统计照常工作。
/// </summary>
public sealed class OfficialTransport : IBotTransport
{
    private readonly OfficialTransportOptions _opt;
    private readonly IdMap _ids;
    private readonly ConcurrentDictionary<long, ISocketMessageChannel> _channels = new();   // 合成 id → 会话通道
    private readonly ConcurrentDictionary<long, IUserMessage> _lastInbound = new();         // 合成 id → 最近入站消息（被动回复源）
    private readonly ConcurrentDictionary<string, long> _msgIdFwd = new();                  // 官方 msg_id → 合成 long
    private readonly ConcurrentDictionary<long, string> _msgIdBack = new();
    private QQBotSocketClient? _client;
    private long _msgSeq;
    private static readonly string[] Unsupported =
    [
        "SendGroupForwardMsgAsync", "SendPrivateForwardMsgAsync", "SetMsgEmojiLikeAsync", "SetGroupBanAsync",
        "SetGroupWholeBanAsync", "SetGroupKickAsync", "SetGroupCardAsync", "SetGroupSpecialTitleAsync",
        "SendGroupNoticeAsync", "SetGroupLeaveAsync", "GetGroupInfoAsync", "GetFriendListAsync",
        "UploadGroupFileAsync", "SetFriendAddRequestAsync", "SetGroupAddRequestAsync", "GetMsgAsync",
        "GetGroupMsgHistoryAsync", "GetGroupMemberListAsync", "GroupPokeAsync", "GetLoginInfoAsync", "CallApiAsync"
    ];

    public OfficialTransport(OfficialTransportOptions opt)
    {
        _opt = opt;
        _ids = IdMap.Load(Path.Combine(opt.MemoryDir, "official", "idmap.json"));
    }

    // ===================== 基本属性 =====================

    public string TransportName => "official";
    public long SelfId { get; private set; }
    public string? LastError { get; private set; }

    // ===================== 生命周期 =====================

    public async Task ConnectAsync(string url, string token)
    {
        if (_client is not null) return;
        if (string.IsNullOrWhiteSpace(_opt.AppId) || string.IsNullOrWhiteSpace(_opt.AppSecret))
            throw new InvalidOperationException("官方通道未配置 AppId/AppSecret（config.json 的 OfficialAppId / OfficialAppSecret）");

        var cfg = new QQBotSocketConfig
        {
            // 群@消息 + 单聊消息（1<<25 GroupAndC2CEvent）
            GatewayIntents = GatewayIntents.GroupAndC2CEvent,
            AccessEnvironment = _opt.Sandbox ? AccessEnvironment.Sandbox : AccessEnvironment.Production,
            LogLevel = LogSeverity.Warning,
        };
        var c = new QQBotSocketClient(cfg);
        c.Log += OnLogAsync;
        c.MessageReceived += OnMessageReceivedAsync;
        c.JoinedGroup += (g, _) => { Log($"已加入群：{g?.Id}"); return Task.CompletedTask; };
        c.LeftGroup += (g, _) => { Log($"已退出群：{g?.Id}"); return Task.CompletedTask; };
        c.MessageDeleted += (_, _, id) => { Log($"消息被撤回：{id}"); return Task.CompletedTask; };
        c.GroupActiveMessageRejected += (g, _) => { Log($"群 {g?.Id} 主动消息被拒（群管理关闭了通知）"); return Task.CompletedTask; };
        c.UserActiveMessageRejected += (u) => { Log($"用户 {u?.Id} 主动消息被拒"); return Task.CompletedTask; };

        // AppID + AppSecret：库自己取/刷 access_token（TokenType.AppSecret）
        if (!int.TryParse(_opt.AppId, out var appId))
            throw new InvalidOperationException($"OfficialAppId 不是合法数字：{_opt.AppId}");
        await c.LoginAsync(appId, TokenType.AppSecret, _opt.AppSecret, validateToken: true);
        await c.StartAsync();
        _client = c;

        try { SelfId = _ids.ToLong("u:" + (c.Rest?.CurrentUser?.Id.ToString() ?? "bot")); }
        catch { SelfId = _ids.ToLong("u:bot"); }
        RaiseState(OneBotConnectionState.Connected, $"官方通道已连接（{( _opt.Sandbox ? "沙箱" : "正式")}环境），本体 id={SelfId}");
    }

    public async Task DisconnectAsync()
    {
        var c = _client;
        _client = null;
        if (c is null) return;
        try { await c.StopAsync(); } catch { }
        try { await c.LogoutAsync(); } catch { }
        RaiseState(OneBotConnectionState.Disconnected, "官方通道已断开");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        try { _client?.Dispose(); } catch { }
    }

    // ===================== 入站：官方事件 → OneBot 形状 =====================

    private Task OnLogAsync(LogMessage msg)
    {
        if (msg.Severity >= LogSeverity.Warning)
            Log($"[qqbot:{msg.Severity}] {msg.Message}{(msg.Exception is null ? "" : " · " + msg.Exception.Message)}");
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage m)
    {
        try
        {
            if (m is null || m.Author is null) return Task.CompletedTask;
            var isGroup = m.Channel is SocketGroupChannel;
            var chanId = _ids.ToLong((isGroup ? "g:" : "u:") + (m.Channel?.Id ?? ""));   // Channel.Id 即 openid
            var userKey = "u:" + m.Author.Id;
            var userSynthetic = _ids.ToLong(userKey);
            var userId = _ids.Resolve(userSynthetic);       // 绑定过 QQ 号就用 QQ 号，宿主权限/统计据此判定

            _channels[chanId] = m.Channel!;
            if (m is IUserMessage um) _lastInbound[chanId] = um;

            var text = m.Content ?? "";
            var mid = SyntheticMsgId(m.Id);

            var mo = new MessageObject
            {
                SelfId = SelfId,
                MessageId = mid,
                RealId = mid,
                Time = m.Timestamp.ToUnixTimeSeconds(),
                MessageType = isGroup ? "group" : "private",
                SubType = "normal",
                UserId = userId,
                GroupId = isGroup ? chanId : null,
                RawMessage = text,
                PlainText = text,
                MessageFormat = "array",
                Sender = new SenderInfo { UserId = userId, Nickname = "", Card = "" },
                MessageSegments = [new MessageSegmentData { Type = "text", Data = { ["text"] = text } }],
            };

            if (!isGroup) mo.MessageSegments.Add(new MessageSegmentData { Type = "text", Data = { ["text"] = "" } });   // 与 OneBot 形状保持一段文本
            Log($"[{(isGroup ? "群" : "私聊")}←{chanId}] {userId}: {Shorten(text)}");

            OnMessage?.Invoke(this, new MessageEventArgs { Message = mo });
            if (isGroup) OnGroupMessage?.Invoke(this, new GroupMessageEventArgs { Message = mo });
            else OnPrivateMessage?.Invoke(this, new PrivateMessageEventArgs { Message = mo });
        }
        catch (Exception ex) { Log($"处理官方消息失败: {ex.Message}"); }
        return Task.CompletedTask;
    }

    // ===================== 出站：IBotApi =====================

    public Task<ApiResult<long>> SendGroupMsgAsync(long groupId, object message) => SendAsync(groupId, message, isGroup: true);

    public Task<ApiResult<long>> SendPrivateMsgAsync(long userId, object message) => SendAsync(userId, message, isGroup: false);

    private async Task<ApiResult<long>> SendAsync(long targetId, object message, bool isGroup)
    {
        try
        {
            if (_client is null) return ApiResult<long>.Fail("官方通道未连接");
            if (!_channels.TryGetValue(targetId, out var ch))
                return ApiResult<long>.Fail($"官方通道：{targetId} 还没有入站消息，暂不支持对该会话主动发送（一期限制）");

            var (content, dropped) = RenderMessage(message);
            if (string.IsNullOrWhiteSpace(content)) return ApiResult<long>.Fail("官方通道：消息内容为空");
            if (dropped.Count > 0) Log($"官方通道：已丢弃不支持的段 [{string.Join(", ", dropped)}]（一期只发纯文本）");

            var passive = _lastInbound.TryGetValue(targetId, out var src) ? src : null;
            var sent = await ch.SendMessageAsync(
                content, markdown: null, attachment: null, embed: null, ark: null, keyboard: null,
                messageReference: null, passiveSource: passive, options: null);

            var mid = SyntheticMsgId(sent?.Id);
            if (isGroup) Log($"[群→{targetId}] {Shorten(content)}");
            else Log($"[私聊→{targetId}] {Shorten(content)}");
            return ApiResult<long>.Ok(mid);
        }
        catch (Exception ex) { return ApiResult<long>.Fail(ex); }
    }

    public async Task<ApiResult> DeleteMsgAsync(long messageId)
    {
        try
        {
            if (_client is null) return ApiResult.Fail("官方通道未连接");
            if (!_msgIdBack.TryGetValue(messageId, out var officialId))
                return ApiResult.Fail("官方通道：只能撤回本进程内收到/发出的消息");
            foreach (var ch in _channels.Values.OfType<SocketGroupChannel>())
                try { await ch.DeleteMessageAsync(officialId, options: null); return ApiResult.Ok(); } catch { }
            return ApiResult.Fail("官方通道：撤回失败（超过 2 分钟或权限不足）");
        }
        catch (Exception ex) { return ApiResult.Fail(ex); }
    }

    // ---- 一期未支持：明确失败并留下原因（不静默失效）----

    public Task<ApiResult<long>> SendGroupForwardMsgAsync(long groupId, List<ForwardNode> messages) => Fail<long>("合并转发");
    public Task<ApiResult<long>> SendPrivateForwardMsgAsync(long userId, List<ForwardNode> messages) => Fail<long>("合并转发");
    public Task<ApiResult> SetMsgEmojiLikeAsync(long messageId, string emojiId, bool set = true) => Fail("表情表态");
    public Task<ApiResult> SetGroupBanAsync(long groupId, long userId, long duration = 0) => Fail("禁言（一期未接）");
    public Task<ApiResult> SetGroupWholeBanAsync(long groupId, long duration = 0) => Fail("全员禁言");
    public Task<ApiResult> SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest = false) => Fail("踢人");
    public Task<ApiResult> SetGroupCardAsync(long groupId, long userId, string? card = null) => Fail("群名片");
    public Task<ApiResult> SetGroupSpecialTitleAsync(long groupId, long userId, string? title = null) => Fail("专属头衔");
    public Task<ApiResult> SendGroupNoticeAsync(long groupId, string content, string? image = null) => Fail("群公告");
    public Task<ApiResult> SetGroupLeaveAsync(long groupId, bool isDismiss = false) => Fail("退群");
    public Task<ApiResult<OneBotGroupInfo>> GetGroupInfoAsync(long groupId) => Fail<OneBotGroupInfo>("群信息");
    public Task<ApiResult<List<FriendInfo>>> GetFriendListAsync() => Fail<List<FriendInfo>>("好友列表");
    public Task<ApiResult> UploadGroupFileAsync(long groupId, string file, string name, string? folder = null) => Fail("群文件");
    public Task<ApiResult> SetFriendAddRequestAsync(string flag, bool approve = true, string? remark = null) => Fail("好友请求");
    public Task<ApiResult> SetGroupAddRequestAsync(string flag, string subType, bool approve = true, string? reason = null) => Fail("加群请求");
    public Task<ApiResult<MsgInfo>> GetMsgAsync(long messageId) => Fail<MsgInfo>("取消息");
    public Task<ApiResult<List<MsgInfo>>> GetGroupMsgHistoryAsync(long groupId, long? messageId = null, int count = 20) => Fail<List<MsgInfo>>("历史消息");
    public Task<ApiResult<List<GroupMemberInfo>>> GetGroupMemberListAsync(long groupId) => Fail<List<GroupMemberInfo>>("群成员列表");
    public Task<ApiResult> GroupPokeAsync(long groupId, long userId) => Fail("戳一戳（官方 API 无此能力）");
    public Task<ApiResult<AccountInfo>> GetLoginInfoAsync() => Fail<AccountInfo>("取登录信息");
    public Task<ApiResult<JsonElement>> CallApiAsync(string action, Dictionary<string, object>? parameters = null) => Fail<JsonElement>("任意 API 透传");

    private Task<ApiResult<T>> Fail<T>(string what)
    {
        var r = ApiResult<T>.Fail($"官方通道不支持：{what}（{nameof(Unsupported)} 一期边界，切回 OneBot 通道可用）");
        Log($"调用被拒：{what}");
        return Task.FromResult(r);
    }

    private Task<ApiResult> Fail(string what)
    {
        var r = ApiResult.Fail($"官方通道不支持：{what}（一期边界，切回 OneBot 通道可用）");
        Log($"调用被拒：{what}");
        return Task.FromResult(r);
    }

    // ===================== 辅助 =====================

    private long SyntheticMsgId(string? officialId)
    {
        if (string.IsNullOrEmpty(officialId)) return ++_msgSeq;
        if (_msgIdFwd.TryGetValue(officialId, out var id)) return id;
        id = _ids.ToLong("m:" + officialId);
        _msgIdFwd[officialId] = id;
        _msgIdBack[id] = officialId;
        return id;
    }

    /// <summary>OneBot 消息对象 → 官方纯文本。支持 string / MessageSegmentData / MessageSegment 及其列表</summary>
    private static (string Content, List<string> Dropped) RenderMessage(object? message)
    {
        var dropped = new List<string>();
        var sb = new System.Text.StringBuilder();

        void Walk(object? o)
        {
            switch (o)
            {
                case null: return;
                case string s: sb.Append(s); return;
                case MessageSegmentData d: Segment(d.Type, d.Data); return;
                case OneBotLib.MessageSegment.MessageSegment seg: Segment(seg.Type, seg.Data); return;
                case System.Collections.IEnumerable list:
                    foreach (var item in list) Walk(item);
                    return;
                case long l: sb.Append(l); return;
                default: sb.Append(o.ToString()); return;
            }
        }

        void Segment(string type, Dictionary<string, object> data)
        {
            switch (type)
            {
                case "text":
                    sb.Append(data.TryGetValue("text", out var t) ? t?.ToString() : "");
                    return;
                case "at":
                    var qq = data.TryGetValue("qq", out var q) ? q?.ToString() : "";
                    sb.Append(qq == "all" ? "@全体成员" : $"@{qq}");
                    dropped.Add("at");
                    return;
                case "face": sb.Append("[表情]"); dropped.Add("face"); return;
                case "image": sb.Append("[图片]"); dropped.Add("image"); return;
                default: dropped.Add(type); return;
            }
        }

        Walk(message);
        return (sb.ToString().Trim(), dropped);
    }

    private void RaiseState(OneBotConnectionState state, string message)
    {
        try { OnConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs { NewState = state, Message = message }); }
        catch { }
    }

    private static void Log(string msg) => Console.WriteLine($"[official] {msg}");

    private static string Shorten(string s)
    {
        s = s.Replace("\n", " ⏎ ");
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    // ===================== 事件声明（官方通道提供不了的永不触发） =====================

    public event EventHandler<MessageEventArgs>? OnMessage;
    public event EventHandler<PrivateMessageEventArgs>? OnPrivateMessage;
    public event EventHandler<GroupMessageEventArgs>? OnGroupMessage;
    public event EventHandler<MessageSentEventArgs>? OnMessageSent;
    public event EventHandler<PrivateMessageSentEventArgs>? OnPrivateMessageSent;
    public event EventHandler<LifecycleEventArgs>? OnLifecycle;
    public event EventHandler<HeartbeatEventArgs>? OnHeartbeat;
    public event EventHandler<ConnectionStateChangedEventArgs>? OnConnectionStateChanged;
    public event EventHandler<GroupMemberChangeEventArgs>? OnGroupMemberChange;
    public event EventHandler<GroupAdminEventArgs>? OnGroupAdmin;
    public event EventHandler<GroupBanEventArgs>? OnGroupBan;
    public event EventHandler<GroupUploadEventArgs>? OnGroupUpload;
    public event EventHandler<GroupRecallEventArgs>? OnGroupRecall;
    public event EventHandler<GroupPokeEventArgs>? OnGroupPoke;
    public event EventHandler<GroupLuckyKingEventArgs>? OnGroupLuckyKing;
    public event EventHandler<GroupHonorEventArgs>? OnGroupHonor;
    public event EventHandler<GroupCardChangeEventArgs>? OnGroupCardChange;
    public event EventHandler<FriendAddEventArgs>? OnFriendAdd;
    public event EventHandler<FriendRecallEventArgs>? OnFriendRecall;
    public event EventHandler<FriendPokeEventArgs>? OnFriendPoke;
    public event EventHandler<ClientStatusEventArgs>? OnClientStatus;
    public event EventHandler<FriendRequestEventArgs>? OnFriendRequest;
    public event EventHandler<GroupRequestEventArgs>? OnGroupRequest;
}
