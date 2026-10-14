using System.Text;
using System.Runtime.InteropServices;
using FlexBot;
using FlexBot.PluginApi;
using FlexBot.WebUi;
using FlexBotWebUi = FlexBot.WebUi.WebUi;   // 类名与命名空间同名，这里消歧
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Events;

namespace FlexBot;

/// <summary>Host runtime state object (split step 2): HandleGroupAsync + TryHandleBindAsync.
/// They used to be local functions inside Program capturing a dozen locals.</summary>
internal sealed class BotRuntime
{
    private readonly IBotTransport _client;
    private readonly MessageStore _msgStore;
    private readonly PluginManager _plugins;
    private readonly CommandDispatcher _dispatcher;
    private readonly HostConfig _config;
    private readonly RecordingBotApi _api;
    /// <summary>类内自引用：搬进来的函数原先通过 Program 的 runtime 局部变量互相调用。</summary>
    private BotRuntime runtime => this;
    private readonly MessageRouter _messages;

    public BotRuntime(IBotTransport client, MessageStore msgStore, PluginManager plugins, CommandDispatcher dispatcher, HostConfig config, RecordingBotApi api, MessageRouter messages)
    {
        _client = client;
        _msgStore = msgStore;
        _plugins = plugins;
        _dispatcher = dispatcher;
        _config = config;
        _api = api;
        _messages = messages;
    }

        public async Task HandleGroupAsync(GroupMessageEventArgs e)
        {
            // 去重放在最前：重连重放/重复投递的同一条消息只处理一次（含绑定的重复请求）
            if (MessageDedup.IsDuplicate($"{_client.TransportName}:g:{e.Message.MessageId}")) { Stats.IncDedupDropped(); return; }
            // 落库：只存关注群与私聊，未关注群直接丢弃。放在去重之后、任何分支之前 ——
            // 存储不能依赖命令匹配或管理员判断，否则命令消息与非管理员私聊会漏记。
            var sm = e.Message;
            // 机器人自己消息的回显：出站路径已记为 dir=out ⇒ 这里跳过，避免重复
            if (sm.SelfId != 0 && sm.UserId == sm.SelfId) return;
            var smScope = _msgStore.ScopeFor(sm.GroupId, sm.UserId ?? 0);
            if (smScope is not null) SessionRegistry.Current?.Ensure(smScope);   // P4：新会话自动登记
            if (smScope is not null)
                _msgStore.RecordIn(_client.TransportName, smScope, sm.MessageId,
                    sm.Time > 0 ? sm.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    sm.UserId ?? 0, sm.SenderName, sm.PlainText, System.Text.Json.JsonSerializer.Serialize(sm));

            Stats.IncIn();
            Stats.Recent.Add("in", "group", e.Message.GroupId ?? 0, e.Message.PlainText);

            // 官方通道的身份绑定要在权限门禁之前处理（此时主人还没有身份），用控制台一次性口令保护
            if (await TryHandleBindAsync(e.Message,
                    r => _client.SendGroupMsgAsync(e.Message.GroupId ?? 0, Msg.Quote(e.Message.MessageId, r))))
                return;

            // 带前缀才进命令分发（未带前缀直接给插件，AI 才有机会看到普通聊天）
            var prefix = Program.CurrentPrefix(_plugins, _config);
            // 群内禁用仅私聊命令（fs_* / pb_*）：群里内容不可信，按来源直接切断
            if (!string.IsNullOrEmpty(prefix) && e.Message.PlainText.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            {
                var cmdName = e.Message.PlainText.Trim()[prefix.Length..].Trim().Split(' ', 2)[0];
                if (FlexBot.PluginApi.CommandPolicy.IsPrivateOnly(cmdName))
                {
                    Console.WriteLine($"[route] 群 {e.Message.GroupId} 内拒绝仅私聊命令: {cmdName}");
                    await _api.SendGroupMsgAsync(e.Message.GroupId ?? 0,
                        Msg.Quote(e.Message.MessageId, FlexBot.PluginApi.CommandPolicy.GroupDenyMessage));
                    return;
                }
            }
            if (!string.IsNullOrEmpty(prefix) && e.Message.PlainText.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            {
                var isGroupAdmin = e.Message.UserId.HasValue && _config.AdminUins.Contains(e.Message.UserId.Value);
                // 命令处理器拿不到"来源"：用户只打 reset 时，宿主把当前会话补进参数
                var cmdText = e.Message.PlainText.Trim();
                if (cmdText.Equals(prefix + "reset", StringComparison.OrdinalIgnoreCase) && e.Message.GroupId is > 0)
                    cmdText = $"{prefix}reset group:{e.Message.GroupId}";

                var handled = await _dispatcher.DispatchAsync(cmdText, isAdmin: isGroupAdmin,
                    r => _api.SendGroupMsgAsync(e.Message.GroupId ?? 0, Msg.Quote(e.Message.MessageId, r)));
                if (handled) return; // 命中命令：回复已发，不再给插件（AI）
            }
            await _messages.DispatchGroupAsync(e);
        }

        public async Task<bool> TryHandleBindAsync(OneBotLib.Models.MessageObject m, Func<string, Task> reply)
        {
            if (_client is not IIdentityBinder binder || !binder.BindingRequired) return false;
            if (m.UserId is not { } uid) return false;

            var prefix = Program.CurrentPrefix(_plugins, _config);
            var raw = (m.PlainText ?? "").Trim();
            if (prefix.Length == 0 || !raw.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var parts = raw[prefix.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts[0].Equals("bind", StringComparison.OrdinalIgnoreCase)) return false;

            if (parts.Length < 4 || !parts[1].Equals("qq", StringComparison.OrdinalIgnoreCase) || !long.TryParse(parts[2], out var qq))
            {
                await reply($"用法：{prefix}bind qq <你的QQ号> <宿主控制台里的口令>");
                return true;
            }

            var err = binder.TryBind(uid, qq, parts[3]);
            Console.WriteLine(err is null ? $"[bind] 身份 {uid} → QQ {qq} 绑定成功" : $"[bind] 绑定失败: {err}");
            await reply(err is null
                ? $"已绑定：当前会话身份 → QQ {qq}。管理员/主人权限与统计即刻按该号生效。"
                : $"绑定失败：{err}");
            return true;
        }


        public async Task HandlePrivateAsync(PrivateMessageEventArgs e)
        {
            // 去重：与群消息同理，重放/重复投递只处理一次
            if (MessageDedup.IsDuplicate($"{_client.TransportName}:p:{e.Message.MessageId}")) { Stats.IncDedupDropped(); return; }
            // 落库：只存关注群与私聊，未关注群直接丢弃。放在去重之后、任何分支之前 ——
            // 存储不能依赖命令匹配或管理员判断，否则命令消息与非管理员私聊会漏记。
            var sm = e.Message;
            // 机器人自己消息的回显：出站路径已记为 dir=out ⇒ 这里跳过，避免重复
            if (sm.SelfId != 0 && sm.UserId == sm.SelfId) return;
            var smScope = _msgStore.ScopeFor(sm.GroupId, sm.UserId ?? 0);
            if (smScope is not null) SessionRegistry.Current?.Ensure(smScope);   // P4：新会话自动登记
            if (smScope is not null)
                _msgStore.RecordIn(_client.TransportName, smScope, sm.MessageId,
                    sm.Time > 0 ? sm.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    sm.UserId ?? 0, sm.SenderName, sm.PlainText, System.Text.Json.JsonSerializer.Serialize(sm));

            Stats.IncIn();
            Stats.Recent.Add("in", "private", e.Message.UserId ?? 0, e.Message.PlainText);

            var m = e.Message;
            if (!m.UserId.HasValue) return;

            // 同上：绑定要在非管理员拦截之前
            if (await runtime.TryHandleBindAsync(m, r => _client.SendPrivateMsgAsync(m.UserId!.Value, Msg.Quote(m.MessageId, r))))
                return;

            if (!_config.AdminUins.Contains(m.UserId.Value))
            {
                Console.WriteLine($"[private] BLOCKED non-admin {m.UserId}: {m.PlainText}");
                return;
            }

            Console.WriteLine($"[private] {m.UserId}: {m.PlainText}");
            var text = m.PlainText.Trim();

            // 宿主级命令（内置 + 插件注册的），命中即由宿主回复，不进插件
            // 同上：私聊裸 reset 补上来源
            if (text.Equals(Program.CurrentPrefix(_plugins, _config) + "reset", StringComparison.OrdinalIgnoreCase))
                text = $"{Program.CurrentPrefix(_plugins, _config)}reset user:{m.UserId.Value}";

            if (await _dispatcher.DispatchAsync(text, isAdmin: true,
                    r =>
                    {
                        Console.WriteLine($"[private→] {m.UserId}: {r.Replace("\n", " ⏎ ")[..Math.Min(200, r.Length)]}");
                        return _client.SendPrivateMsgAsync(m.UserId.Value, Msg.Quote(m.MessageId, r));
                    }))
                return;

            await _messages.DispatchPrivateAsync(e);
        }
}
