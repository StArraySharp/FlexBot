using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
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
public sealed class OfficialTransport : IBotTransport, IIdentityBinder
{
    private readonly OfficialTransportOptions _opt;
    private readonly IdMap _ids;
    /// <summary>身份绑定口令：启动时随机生成，只打印在真实控制台（不进日志/不进 WebUI 日志缓冲）</summary>
    private readonly string _bindCode = NewBindCode();
    private static readonly Stream RawStdout = Console.OpenStandardOutput();
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
        "GetGroupMsgHistoryAsync", "GroupPokeAsync", "CallApiAsync"
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
            // 群@消息 + 单聊消息（1<<25 GroupAndC2CEvent）+ 互动事件（1<<26 INTERACTION，指令/按钮回调用）
            GatewayIntents = GatewayIntents.GroupAndC2CEvent | GatewayIntents.Interaction,
            AccessEnvironment = _opt.Sandbox ? AccessEnvironment.Sandbox : AccessEnvironment.Production,
            LogLevel = LogSeverity.Warning,
            // 互动响应由我们自己发（官方要求 3 秒内响应一次且只能响应一次），故关掉库的自动 ack
            AutoAcknowledgeInteractions = false,
        };
        var c = new QQBotSocketClient(cfg);
        c.Log += OnLogAsync;
        c.MessageReceived += OnMessageReceivedAsync;
        c.InteractionCreated += OnInteractionAsync;
        c.ButtonExecuted += OnInteractionAsync;
        c.JoinedGroup += (g, _) =>
        {
            // 群通道的构造函数是 internal，只能从事件里拿到；这里缓存住，主动发消息才有通道可用
            if (g is not null) _channels[_ids.ToLong("g:" + g.Id)] = g;
            Log($"已加入群：{g?.Id}");
            return Task.CompletedTask;
        };
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
        WriteConsoleOnly($"\n[official] 身份绑定口令：{_bindCode}\n"
                       + "           在 QQ 里给机器人发：!bind qq <你的QQ号> <上面的口令>\n"
                       + "           绑定后该身份的权限/统计按 QQ 号生效；口令只在控制台打印这一次、每次启动都会变\n\n");
        Log("已生成身份绑定口令（明文仅控制台可见），等待绑定：!bind qq <QQ号> <口令>");
    }

    // ===================== 身份绑定（主人在官方通道下没有身份，必须先绑） =====================

    public bool BindingRequired => true;

    public string? TryBind(long idOrQq, long qq, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim() != _bindCode)
            return "口令不正确（口令在宿主控制台窗口里，只打印一次）";
        if (qq < 10000) return "QQ 号看起来不合法";
        if (!_ids.TrySyntheticOf(idOrQq, out var synthetic))
            return "找不到该身份（发送者身份还没记录，先随便发条消息）";
        _ids.Bind(synthetic, qq);
        Log($"身份已绑定：{synthetic} → QQ {qq}");
        return null;
    }

    /// <summary>随机口令：去掉易混字符（0/O/1/l/I），加密强随机</summary>
    private static string NewBindCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var buf = new char[8];
        for (var i = 0; i < buf.Length; i++) buf[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(buf);
    }

    /// <summary>只写到真实控制台，绕过宿主 Console.SetOut 的日志接管（口令不该进日志）</summary>
    private static void WriteConsoleOnly(string text)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            RawStdout.Write(bytes, 0, bytes.Length);
            RawStdout.Flush();
        }
        catch { }
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

            // 通道缓存同时登记"解析后的身份"（绑过 QQ 号就是 QQ 号），与插件调用 SendXxxAsync 传的 id 对齐
            var resolvedKey = isGroup ? (mo.GroupId ?? 0) : userId;
            if (resolvedKey != 0 && resolvedKey != chanId)
            {
                _channels[resolvedKey] = m.Channel!;
                if (m is IUserMessage um2) _lastInbound[resolvedKey] = um2;
            }
            Log($"[{(isGroup ? "群" : "私聊")}←{chanId}] {userId}: {Shorten(text)}");

            OnMessage?.Invoke(this, new MessageEventArgs { Message = mo });
            if (isGroup) OnGroupMessage?.Invoke(this, new GroupMessageEventArgs { Message = mo });
            else OnPrivateMessage?.Invoke(this, new PrivateMessageEventArgs { Message = mo });
        }
        catch (Exception ex) { Log($"处理官方消息失败: {ex.Message}"); }
        return Task.CompletedTask;
    }

    // ===================== 指令面板 + 互动回调 =====================

    public event Func<BotInteraction, Task>? OnInteraction;

    /// <summary>把命令注册成官方「指令面板」。官方对条目数有上限（超了报 30013 超出数量限制），
    /// 所以这里：先查平台已有面板（有则 Modify 而非 Create）→ 上限未知时减半重试实测出来 → 记住达标条数供下次直接用。</summary>
    public async Task<int> SyncCommandPanelsAsync(IReadOnlyList<BotCommandSpec> commands, CancellationToken ct = default)
    {
        if (_client is null) return -1;
        var all = commands
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => CommandPanelItem.CreateCommand(
                c.Name.Length > 20 ? c.Name[..20] : c.Name,
                string.IsNullOrWhiteSpace(c.Description) ? (c.Usage ?? "") : c.Description,
                c.OnlyAdmin))
            .ToList();

        if (all.Count == 0)
        {
            // 传空集合 = 清空既有指令面板（关闭注册时用，避免客户端里留着过期指令）
            var st = LoadPanelState();
            foreach (var scope in new[] { CommandPanelScope.C2C, CommandPanelScope.Group })
            {
                var key = scope.ToString();
                try
                {
                    ICommandPanel? panel = null;
                    try
                    {
                        await foreach (var page in _client.GetCommandPanelsAsync(scope, options: null))
                        {
                            foreach (var pl in page) { panel = pl; break; }
                            if (panel is not null) break;
                        }
                    }
                    catch (Exception ex) { Log($"指令面板 {key}：列举失败 {ex.Message}，改按记录 id 取"); }
                    if (panel is null && st.TryGetValue(key, out var s0) && s0.Id is { Length: > 0 } id0)
                    {
                        try { panel = await _client.GetCommandPanelAsync(id0, options: null); } catch { }
                    }
                    if (panel is null) { Log($"指令面板 {key}：平台上没有可清空的面板"); continue; }
                    await panel.ModifyAsync([], _ => { }, options: null);
                    st.Remove(key);
                    Log($"指令面板 {key}：已清空");
                }
                catch (Exception ex) { Log($"指令面板 {key}：清空失败 {ex.Message}（可在开放平台管理端手动删除）"); }
            }
            SavePanelState(st);
            return 0;
        }

        var state = LoadPanelState();
        var total = 0;

        foreach (var scope in new[] { CommandPanelScope.C2C, CommandPanelScope.Group })
        {
            var key = scope.ToString();

            // 平台侧已有面板？（能 Modify 就不新建，避免版本堆积）
            ICommandPanel? existing = null;
            try
            {
                await foreach (var page in _client.GetCommandPanelsAsync(scope, options: null))
                {
                    foreach (var pl in page)
                    {
                        existing = pl;
                        Log($"指令面板 {key}：平台已有 v{pl.Version}（{pl.Items.Count} 条），走更新路径");
                        break;
                    }
                    if (existing is not null) break;
                }
                if (existing is null) Log($"指令面板 {key}：平台侧暂无面板，走创建路径");
            }
            catch (Exception ex) { Log($"指令面板 {key}：列举失败 {ex.Message}（库的列举接口与当前 API 不匹配，改用记录的 id 单取）"); }

            // 列举接口不可用时的绕行：用上次记录的 id 单取面板，能拿到就 Modify 而不是反复 Create
            if (existing is null && state.TryGetValue(key, out var st1) && st1.Id is { Length: > 0 } knownId)
            {
                try
                {
                    existing = await _client.GetCommandPanelAsync(knownId, options: null);
                    if (existing is not null) Log($"指令面板 {key}：按 id 取到 v{existing.Version}（{existing.Items.Count} 条），走更新路径");
                }
                catch (Exception ex) { Log($"指令面板 {key}：按 id 单取失败 {ex.Message}，将尝试创建"); }
            }

            var cap = state.TryGetValue(key, out var st0) && st0.Cap > 0 ? st0.Cap : Math.Min(20, all.Count);
            var take = Math.Min(cap, all.Count);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var items = all.Take(take).ToList();
                try
                {
                    if (existing is not null)
                    {
                        await existing.ModifyAsync(items, _ => { }, options: null);
                        Log($"指令面板 {key} 已更新：{items.Count} 条（仅管理员 {items.Count(i => i.OnlyAdmin)} 条）");
                    }
                    else
                    {
                        var created = await _client.CreateCommandPanelAsync(scope, items, _ => { }, options: null);
                        Log($"指令面板 {key} 已创建：v{created.Version}，{items.Count} 条（仅管理员 {items.Count(i => i.OnlyAdmin)} 条）");
                        existing = created;
                    }
                    state[key] = new PanelState { Id = existing.Id, Cap = take };
                    total += items.Count;
                    if (take < all.Count)
                        Log($"指令面板 {key}：受平台上限所限只放 {take}/{all.Count} 条（其余命令仍可用「前缀+命令名」直接发）");
                    break;
                }
                catch (Exception ex)
                {
                    if (take > 1 && ex.Message.Contains("30013"))
                    {
                        take = Math.Max(1, take / 2);
                        Log($"指令面板 {key}：超限（30013），减半重试 → {take} 条");
                        continue;
                    }
                    Log($"指令面板 {key} 注册失败: {ex.Message}");
                    break;
                }
            }
        }
        SavePanelState(state);
        return total;
    }

    private sealed class PanelState { public string Id { get; set; } = ""; public int Cap { get; set; } }
    private string PanelStatePath => Path.Combine(_opt.MemoryDir, "official", "panels.json");

    private Dictionary<string, PanelState> LoadPanelState()
    {
        try
        {
            if (File.Exists(PanelStatePath))
                return JsonSerializer.Deserialize<Dictionary<string, PanelState>>(File.ReadAllText(PanelStatePath)) ?? [];
        }
        catch { }
        return [];
    }

    private void SavePanelState(Dictionary<string, PanelState> s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PanelStatePath)!);
            File.WriteAllText(PanelStatePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private async Task OnInteractionAsync(SocketInteraction i)
    {
        try
        {
            if (i is null) return;
            // InteractionCreated 与 ButtonExecuted 可能对同一次互动都触发：按互动 id 去重，避免重复派发/回复
            if (!string.IsNullOrEmpty(i.Id) && !_seenInteractions.TryAdd(i.Id, 0)) return;

            // 官方要求：type=11 消息按钮 / type=12 快捷菜单 必须在 3 秒内响应，且一个 id 只能响应一次
            if (!i.HasResponded)
            {
                try { await i.AcknowledgeAsync(InteractionResponseCode.Success, options: null); }
                catch (Exception ex) { Log($"互动响应失败: {ex.Message}"); }
            }

            var isGroup = i.ChatType == InteractionChatType.Group || i.GroupOpenId is not null;
            var openId = (isGroup ? i.GroupMemberOpenId : i.UserOpenId) ?? i.UserOpenId ?? i.GroupMemberOpenId;
            var userId = openId is { } oid ? _ids.Resolve(_ids.ToLong("u:" + oid.ToString("N"))) : 0;
            long? groupId = isGroup && i.GroupOpenId is { } go ? _ids.Resolve(_ids.ToLong("g:" + go.ToString("N"))) : null;

            // 缓存通道与这次互动（按"解析后的身份"为键，和插件调用 SendXxxAsync 时传的 id 一致）
            if (i.Channel is not null)
            {
                if (groupId is { } gid) { _channels[gid] = i.Channel; _lastInteraction[gid] = i; }
                if (userId > 0 && !isGroup) { _channels[userId] = i.Channel; _lastInteraction[userId] = i; }
            }

            var text = (i.ButtonData ?? i.ButtonId ?? "").Trim();
            Log($"[互动] 类型={i.Type} 场景={(isGroup ? "群" : "单聊")} 用户={userId} data=\"{Shorten(text)}\"");
            if (text.Length == 0) return;
            if (OnInteraction is not null) await OnInteraction(new BotInteraction(text, userId, groupId, isGroup, i.Id));
        }
        catch (Exception ex) { Log($"处理互动失败: {ex.Message}"); }
    }

    /// <summary>最近的互动（按会话缓存）：回复时优先用它，等于走被动回复窗口</summary>
    private readonly ConcurrentDictionary<long, SocketInteraction> _lastInteraction = new();
    /// <summary>已处理过的互动 id（两个事件可能对同一次互动都触发）</summary>
    private readonly ConcurrentDictionary<string, byte> _seenInteractions = new();

    public Task<ApiResult<long>> SendGroupMsgAsync(long groupId, object message) => SendAsync(groupId, message, isGroup: true);

    public Task<ApiResult<long>> SendPrivateMsgAsync(long userId, object message) => SendAsync(userId, message, isGroup: false);

    private async Task<ApiResult<long>> SendAsync(long targetId, object message, bool isGroup)
    {
        try
        {
            if (_client is null) return ApiResult<long>.Fail("官方通道未连接");
            var ch = ResolveChannel(targetId, isGroup);
            if (ch is null)
                return ApiResult<long>.Fail(isGroup
                    ? $"官方通道：群 {targetId} 尚无通道（官方没有「按 id 取群通道」的接口，需该群有过入站消息或机器人入群事件）"
                    : $"官方通道：找不到会话 {targetId}（openid 无法解析成 Guid？）");

            var (content, dropped) = RenderMessage(message);
            if (string.IsNullOrWhiteSpace(content)) return ApiResult<long>.Fail("官方通道：消息内容为空");
            if (dropped.Count > 0) Log($"官方通道：已丢弃不支持的段 [{string.Join(", ", dropped)}]（一期只发纯文本）");

            // 若这条会话刚发生过互动（点按钮/快捷菜单），优先用互动接口回复 = 走被动回复窗口，不吃主动消息额度
            if (_lastInteraction.TryGetValue(targetId, out var inter))
            {
                try
                {
                    var sentViaInteraction = await inter.SendMessageAsync(
                        content, markdown: null, attachment: null, embed: null, ark: null, keyboard: null,
                        messageReference: null, options: null);
                    Log($"[{(isGroup ? "群" : "私聊")}→{targetId}·互动回复] {Shorten(content)}");
                    return ApiResult<long>.Ok(SyntheticMsgId(sentViaInteraction?.Id));
                }
                catch (Exception ex) { Log($"互动回复失败，回退普通发送: {ex.Message}"); }
            }

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
    public Task<ApiResult<List<GroupMemberInfo>>> GetGroupMemberListAsync(long groupId)
    {
        return Impl();
        async Task<ApiResult<List<GroupMemberInfo>>> Impl()
        {
            try
            {
                if (!_channels.TryGetValue(groupId, out var ch) || ch is not SocketGroupChannel g)
                    return ApiResult<List<GroupMemberInfo>>.Fail("官方通道：该群还没有入站消息，暂无法取成员列表");
                var list = new List<GroupMemberInfo>();
                await foreach (var page in g.GetMembersAsync(options: null))      // 官方接口分页返回，每页一个集合
                    foreach (var m in page)
                        list.Add(new GroupMemberInfo
                        {
                            GroupId = groupId,
                            UserId = _ids.Resolve(_ids.ToLong("u:" + m.Id)),
                            Nickname = m.Username ?? "",
                            Role = m.Role.ToString().ToLowerInvariant(),
                            JoinTime = m.JoinedAt.ToUnixTimeSeconds(),
                            IsRobot = m.IsBot,
                        });
                Log($"取群成员列表 {groupId}: {list.Count} 人");
                return ApiResult<List<GroupMemberInfo>>.Ok(list);
            }
            catch (Exception ex) { return ApiResult<List<GroupMemberInfo>>.Fail(ex); }
        }
    }
    public Task<ApiResult> GroupPokeAsync(long groupId, long userId) => Fail("戳一戳（官方 API 无此能力）");
    public Task<ApiResult<AccountInfo>> GetLoginInfoAsync()
    {
        try
        {
            var u = _client?.Rest?.CurrentUser;
            return Task.FromResult(ApiResult<AccountInfo>.Ok(new AccountInfo
            {
                UserId = SelfId,
                Nickname = u?.Username ?? "官方机器人",
            }));
        }
        catch (Exception ex) { return Task.FromResult(ApiResult<AccountInfo>.Fail(ex)); }
    }
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

    /// <summary>
    /// 取会话通道：优先用缓存（入站消息/入群事件里缓存的）。
    /// 官方 openid 就是 Guid，但库把通道构造函数设为 internal、且没有"按 id 取会话"的公开接口
    /// （GetUserChannel 只认本地缓存），而主动发消息必须有通道对象 ⇒ 兜底用反射构造。
    /// 反射失败不影响被动回复（那条路径本来就有真实通道对象）。
    /// </summary>
    private ISocketMessageChannel? ResolveChannel(long targetId, bool isGroup)
    {
        if (_channels.TryGetValue(targetId, out var cached)) return cached;
        if (_client is null) return null;
        if (!_ids.TryOpenId(targetId, out var key)) return null;

        var hex = key.StartsWith("u:", StringComparison.Ordinal) || key.StartsWith("g:", StringComparison.Ordinal) ? key[2..] : key;
        if (!Guid.TryParse(hex, out var guid)) return null;

        // ① 公开路径：先把用户塞进库的缓存，再取私聊通道
        if (!isGroup)
        {
            try { _client.GetUser(guid.ToString("N")); } catch { }
            try
            {
                var viaApi = _client.GetUserChannel(guid);
                if (viaApi is not null) { _channels[targetId] = viaApi; return viaApi; }
            }
            catch { }
        }

        // ② 兜底：反射构造 internal ctor（SocketGroupChannel(client, Guid) / SocketUserChannel(client, Guid, user)）
        try
        {
            var type = isGroup ? typeof(SocketGroupChannel) : typeof(SocketUserChannel);
            var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                           .OrderBy(c => c.GetParameters().Length).FirstOrDefault();
            if (ctor is null) return null;
            var args = ctor.GetParameters().Select(p =>
                p.ParameterType == typeof(QQBotSocketClient) ? (object)_client
                : p.ParameterType == typeof(Guid) ? guid
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)!
                : null!).ToArray();
            if (ctor.Invoke(args) is ISocketMessageChannel made)
            {
                _channels[targetId] = made;
                Log($"按需构造{(isGroup ? "群" : "私聊")}通道成功：{type.Name} {guid:N}");
                return made;
            }
        }
        catch (Exception ex) { Log($"按需构造通道失败（{type0(isGroup)}）: {ex.Message}"); }
        return null;

        static string type0(bool g) => g ? "群" : "私聊";
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
