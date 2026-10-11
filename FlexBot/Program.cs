using System.Text;
using FlexBot;
using FlexBot.PluginApi;
using FlexBot.WebUi;
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Events;

// ===================== 初始化 =====================

// 全局日志：每行自动带时间戳（HH:mm:ss.fff）+ 进入环形缓冲（WebUI 用）
// 必须在捕获 Console.Out 之前切 UTF-8：writer 的编码在创建时固定，
// 之后再改 OutputEncoding 不会影响已捕获的 writer（中文 Windows 默认 GBK 会导致乱码）
try { Console.OutputEncoding = Encoding.UTF8; } catch { }
var _origOut = Console.Out;
Console.SetOut(new TimestampWriter(_origOut));

var settings = HostSettings.Load();
var config = new HostConfig(settings);
// 通道选择：默认 onebot（LLBot/NapCat）；配 Transport=official 走 QQ 官方机器人开放平台。
// 两条通道实现同一个界面（事件形状 + IBotApi），下面的接线、路由、权限、插件派发全部无感。
IBotTransport client = settings.Transport.Equals("official", StringComparison.OrdinalIgnoreCase)
    ? new FlexBot.OfficialBot.OfficialTransport(new FlexBot.OfficialBot.OfficialTransportOptions
    {
        AppId = settings.OfficialAppId,
        AppSecret = settings.OfficialAppSecret,
        Sandbox = settings.OfficialSandbox,
        MemoryDir = settings.MemoryDir,
    })
    : new BotClient();
Console.WriteLine($"[boot] 机器人通道: {client.TransportName}");
var messages = new MessageRouter();
var events = new EventRouter();
var plugins = new PluginManager(client, config, messages, events, settings);
BotState.WsUrl = settings.WsUrl;

// ===================== 事件接线（宿主 → 插件路由） =====================

var reconnecting = 0;
var everConnected = false;
client.OnConnectionStateChanged += (s, e) =>
{
    Console.WriteLine($"[state] {e.OldState} -> {e.NewState} ({e.Message})");
    if (e.NewState == ConnectionState.Connected)
    {
        everConnected = true; BotState.Connected = true;

        // 官方通道：连上后把命令同步成客户端可见的「指令」（OnlyAdmin 区分管理员）。
        // 延迟几秒等网关 READY/会话就绪，失败只记日志不影响其它功能。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            try
            {
                var specs = new List<BotCommandSpec>
                {
                    new("plugin", "插件管理", "plugin [list|load|unload|reload] <名称>", true),
                    new("help", "显示帮助", "help", false),
                    new("bind", "官方通道身份绑定", "bind qq <QQ号> <控制台口令>", false),
                };
                foreach (var c in plugins.GetCommands())
                    specs.Add(new BotCommandSpec(c.Name, c.Description, string.IsNullOrWhiteSpace(c.Usage) ? c.Name : c.Usage, true));
                var n = await client.SyncCommandPanelsAsync(specs);
                if (n >= 0) Console.WriteLine($"[official] 指令同步完成：{n} 条（其中仅管理员 {specs.Count(s => s.OnlyAdmin)} 条）");
            }
            catch (Exception ex) { Console.WriteLine($"[official] 指令同步失败: {ex.Message}"); }
        });
    }
    if (e.NewState == ConnectionState.Disconnected) BotState.Connected = false;
    // 断线自动重连（NapCat 重启等场景；仅首次连接成功后介入，避免与启动重试循环叠加）
    if (e.NewState == ConnectionState.Disconnected && everConnected
        && Interlocked.CompareExchange(ref reconnecting, 1, 0) == 0)
        _ = Task.Run((Func<Task?>)(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    try
                    {
                        await client.ConnectAsync(settings.WsUrl, settings.Token);
                        return;
                    }
                    catch (Exception ex) { Console.WriteLine($"[!] 重连失败: {ex.Message}，5 秒后重试..."); }
                }
            }
            finally { Volatile.Write(ref reconnecting, 0); }
        }));
};

client.OnGroupPoke += (s, e) => _ = events.RaiseAsync<GroupPokeEventArgs>(e);
client.OnFriendPoke += (s, e) => _ = events.RaiseAsync<FriendPokeEventArgs>(e);
client.OnGroupMemberChange += (s, e) => _ = events.RaiseAsync<GroupMemberChangeEventArgs>(e);
client.OnGroupCardChange += (s, e) => _ = events.RaiseAsync<GroupCardChangeEventArgs>(e);

// 补全事件面：撤回/请求/群管/心跳/连接状态
client.OnGroupRecall += (s, e) => _ = events.RaiseAsync<GroupRecallEventArgs>(e);
client.OnFriendRecall += (s, e) => _ = events.RaiseAsync<FriendRecallEventArgs>(e);
client.OnFriendRequest += (s, e) => _ = events.RaiseAsync<FriendRequestEventArgs>(e);
client.OnGroupRequest += (s, e) => _ = events.RaiseAsync<GroupRequestEventArgs>(e);
client.OnGroupBan += (s, e) => _ = events.RaiseAsync<GroupBanEventArgs>(e);
client.OnGroupAdmin += (s, e) => _ = events.RaiseAsync<GroupAdminEventArgs>(e);
client.OnGroupUpload += (s, e) => _ = events.RaiseAsync<GroupUploadEventArgs>(e);
client.OnGroupLuckyKing += (s, e) => _ = events.RaiseAsync<GroupLuckyKingEventArgs>(e);
client.OnGroupHonor += (s, e) => _ = events.RaiseAsync<GroupHonorEventArgs>(e);
client.OnHeartbeat += (s, e) => _ = events.RaiseAsync<HeartbeatEventArgs>(e);
client.OnConnectionStateChanged += (s, e) => _ = events.RaiseAsync<ConnectionStateChangedEventArgs>(e);

// ---- 官方通道：客户端「指令/按钮」互动 → 复用同一套命令派发（含管理员判定） ----
client.OnInteraction += async i =>
{
    try
    {
        var prefix = CurrentPrefix();
        var text = (i.Text ?? "").Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) text = prefix + text;   // 按钮 data 可能不带前缀
        var isAdmin = i.UserId > 0 && config.AdminUins.Contains(i.UserId);
        Console.WriteLine($"[互动] {(i.IsGroup ? "群" + i.GroupId : "私聊")} {i.UserId} 指令 \"{text}\" 管理员={isAdmin}");
        await TryDispatchCommandAsync(text, isAdmin,
            r => i.IsGroup ? client.SendGroupMsgAsync(i.GroupId ?? 0, r) : client.SendPrivateMsgAsync(i.UserId, r));
    }
    catch (Exception ex) { Console.WriteLine($"[互动] 处理失败: {ex.Message}"); }
};

// ---- 私聊（仅管理员）：宿主级 !plugin 命令 + 注册命令分发，其余分发给插件 ----
client.OnPrivateMessage += (s, e) => _ = HandlePrivateAsync(e);

// ---- 群消息：直接分发给插件（命令插件优先级高，AI 插件最后） ----
client.OnGroupMessage += (s, e) => _ = HandleGroupAsync(e);

// 群消息：先做宿主命令分发（带前缀的命令对所有人开放，安全命令内部鉴权），未命中交给插件
async Task HandleGroupAsync(GroupMessageEventArgs e)
{
    // 官方通道的身份绑定要在权限门禁之前处理（此时主人还没有身份），用控制台一次性口令保护
    if (await TryHandleBindAsync(e.Message,
            r => client.SendGroupMsgAsync(e.Message.GroupId ?? 0, Msg.Quote(e.Message.MessageId, r))))
        return;

    // 带前缀才进命令分发（未带前缀直接给插件，AI 才有机会看到普通聊天）
    var prefix = CurrentPrefix();
    if (!string.IsNullOrEmpty(prefix) && e.Message.PlainText.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
    {
        var isGroupAdmin = e.Message.UserId.HasValue && config.AdminUins.Contains(e.Message.UserId.Value);
        var handled = await TryDispatchCommandAsync(e.Message.PlainText.Trim(), isAdmin: isGroupAdmin,
            r => client.SendGroupMsgAsync(e.Message.GroupId ?? 0, Msg.Quote(e.Message.MessageId, r)));
        if (handled) return; // 命中命令：回复已发，不再给插件（AI）
    }
    await messages.DispatchGroupAsync(e);
}

// 当前生效命令前缀：优先 Admin 插件设置，缺失回落宿主配置（热更新，每条消息实时读取）
string CurrentPrefix() =>
    plugins.GetPluginSettingString("Admin", "CommandPrefix") is { Length: > 0 } p ? p
        : string.IsNullOrEmpty(config.CommandPrefix) ? "!" : config.CommandPrefix;

// 统一命令分发：text 已去除前缀，返回 true 表示命中并由宿主回复
async Task<bool> TryDispatchCommandAsync(string text, bool isAdmin, Func<string, Task> reply)
{
    var prefix = CurrentPrefix();
    if (string.IsNullOrEmpty(prefix) || !text.StartsWith(prefix, StringComparison.Ordinal))
        return false;

    var body = text[prefix.Length..].TrimStart();
    if (body.Length == 0) return false;
    var sp = body.IndexOf(' ');
    var cmdName = (sp < 0 ? body : body[..sp]).ToLowerInvariant();
    var args = sp < 0 ? "" : body[(sp + 1)..].Trim();

    // 宿主内置命令：plugin
    if (cmdName == "plugin")
    {
        if (!isAdmin) { await reply("仅管理员可用"); return true; }
        await reply(await plugins.HandleCommandAsync(text));
        return true;
    }
    // 宿主内置命令：help / 帮助
    if (cmdName is "help" or "帮助")
    {
        await reply(BuildHelpText(prefix));
        return true;
    }

    // 插件注册的命令
    var reg = plugins.FindCommand(cmdName);
    if (reg is null) return false;
    if (!isAdmin) { await reply($"命令 {prefix}{cmdName} 仅管理员可用"); return true; }
    try
    {
        var result = await reg.Value.Handler(args);
        if (!string.IsNullOrEmpty(result)) await reply(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[cmd] {prefix}{cmdName} 执行异常: {ex.Message}");
        await reply($"命令执行出错: {ex.Message}");
    }
    return true;
}

// 帮助文本：内置命令 + 全部插件注册命令（带说明与用法）
string BuildHelpText(string prefix)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"可用命令（前缀 {prefix}）：");
    sb.AppendLine($"{prefix}help - 显示本帮助");
    sb.AppendLine($"{prefix}plugin [list|load|unload|reload] - 插件管理");
        sb.AppendLine($"{prefix}bind qq <QQ号> <口令> - 官方通道：把当前身份绑定到 QQ 号（口令在宿主控制台）");
    foreach (var c in plugins.GetCommands())
    {
        var usage = string.IsNullOrEmpty(c.Usage) ? c.Name : c.Usage;
        sb.AppendLine($"{prefix}{usage} - {c.Description}（{c.PluginName}）");
    }
    return sb.ToString().TrimEnd();
}

// 官方通道身份绑定：!bind qq <QQ号> <口令>。必须放在非管理员拦截之前 ——
// 官方通道只给 openid，主人绑之前身份对不上 OwnerUin，会被下面那道门禁直接拦掉。
// 绑定等同提权，故必须带控制台一次性口令。
async Task<bool> TryHandleBindAsync(OneBotLib.Models.MessageObject m, Func<string, Task> reply)
{
    if (client is not IIdentityBinder binder || !binder.BindingRequired) return false;
    if (m.UserId is not { } uid) return false;

    var prefix = CurrentPrefix();
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

async Task HandlePrivateAsync(PrivateMessageEventArgs e)
{
    var m = e.Message;
    if (!m.UserId.HasValue) return;

    // 同上：绑定要在非管理员拦截之前
    if (await TryHandleBindAsync(m, r => client.SendPrivateMsgAsync(m.UserId!.Value, Msg.Quote(m.MessageId, r))))
        return;

    if (!config.AdminUins.Contains(m.UserId.Value))
    {
        Console.WriteLine($"[private] BLOCKED non-admin {m.UserId}: {m.PlainText}");
        return;
    }

    Console.WriteLine($"[private] {m.UserId}: {m.PlainText}");
    var text = m.PlainText.Trim();

    // 宿主级命令（内置 + 插件注册的），命中即由宿主回复，不进插件
    if (await TryDispatchCommandAsync(text, isAdmin: true,
            r =>
            {
                Console.WriteLine($"[private→] {m.UserId}: {r.Replace("\n", " ⏎ ")[..Math.Min(200, r.Length)]}");
                return client.SendPrivateMsgAsync(m.UserId.Value, Msg.Quote(m.MessageId, r));
            }))
        return;

    await messages.DispatchPrivateAsync(e);
}

// ===================== 启动 =====================

// 日志目录
var logDir = string.IsNullOrWhiteSpace(settings.LogDir)
    ? Path.Combine(settings.MemoryDir, "logs")
    : settings.LogDir;
Directory.CreateDirectory(settings.MemoryDir); // 记忆/插件数据根目录缺失时兜底创建
Directory.CreateDirectory(logDir);

// 启动外部依赖（NapCat、LLBot）
var procMgr = new ProcessManager(logDir);
if (settings.IsolateDependencyLogs && (!string.IsNullOrWhiteSpace(settings.NapCatCmd) || !string.IsNullOrWhiteSpace(settings.LLBotCmd)))
{
    try
    {
        await procMgr.StartDependenciesAsync(settings.NapCatCmd, settings.LLBotCmd);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[!] 启动依赖进程失败: {ex.Message}");
    }
}

// Bot 核心（后台运行：连接 NapCat + 加载插件 + 保活）
var coreTask = Task.Run(RunBotAsync);

// WebUI（默认开启，浏览器可直接访问；--no-web 关闭）
string? uiUrl = null;
if (!args.Contains("--no-web"))
{
    uiUrl = WebUi.Start(plugins, settings, client, procMgr);
    Console.WriteLine($"[webui] 控制台已就绪: {uiUrl}");
}

// Photino 桌面窗口（--gui）：同一页面以本地窗口展示，关窗即退出
if (args.Contains("--gui") && uiUrl is not null)
{
    PhotinoGui.Run(uiUrl); // 阻塞至窗口关闭
}
else
{
    await coreTask;
}

async Task RunBotAsync()
{
    // 连接（后台重试，NapCat 未就绪也不退出；指数退避策略）
    Console.WriteLine($"[+] Connecting to {settings.WsUrl} ...");
    var connectTask = Task.Run((Func<Task?>)(async () =>
    {
        const double BackoffMultiplier = 1.5;
        const int MaxBackoffSec = 120;
        int attempt = 0;

        while (true)
        {
            try
            {
                await client.ConnectAsync(settings.WsUrl, settings.Token);
                Console.WriteLine($"[+] 连接成功");
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                var delaySec = Math.Min((int)(5 * Math.Pow(BackoffMultiplier, attempt - 1)), MaxBackoffSec);
                Console.WriteLine($"[!] 连接失败({attempt}): {ex.Message}，{delaySec} 秒后重试...");
                await Task.Delay(TimeSpan.FromSeconds(delaySec));
            }
        }
    }));

    // 插件加载不依赖连接，与连接并行
    await plugins.LoadAllAsync();

    await connectTask;

    var login = await client.GetLoginInfoAsync();
    Console.WriteLine(login.Success && login.Data is not null
        ? $"[+] Login: user_id={login.Data.UserId} nickname={login.Data.Nickname}"
        : $"[!] get_login_info failed: {login.ErrorMessage}");
    if (login.Success && login.Data is not null) BotState.SelfId = login.Data.UserId;

    Console.WriteLine($"[+] Ready: admins={string.Join(',', settings.AllAdminUins)}，已启用插件 {plugins.BuildList().Replace("\n", " | ")}");
    Console.WriteLine("[+] 私聊仅管理员可用；群里 @ 我/叫科比提问；管理员可用 !api / !timer / !cron / !watch / !plugin 等命令。按 Ctrl+C 或关闭窗口退出。");

    // 保活：stdin 关闭/被重定向(EOF)时 Console.ReadLine() 立即返回 null，若直接退出会导致后台任务被中断
    while (Console.ReadLine() is not null) { }
    await Task.Delay(Timeout.InfiniteTimeSpan);

    await client.DisconnectAsync();
    Console.WriteLine("[+] Closed.");
}

// 给每行输出加时间戳（HH:mm:ss.fff）并写入环形缓冲（WebUI 用）
namespace FlexBot
{
    class TimestampWriter(TextWriter inner) : TextWriter
    {
        private bool _atLineStart = true;
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (_atLineStart)
            {
                var prefix = $"[{DateTime.Now:HH:mm:ss.fff}] ";
                inner.Write(prefix);
                _line.Append(prefix);
                _atLineStart = false;
            }
            inner.Write(value);
            if (value == '\n')
            {
                var line = _line.ToString().TrimEnd('\r', '\n');
                LogStore.AppendLine(line);
                _line.Clear();
                _atLineStart = true;
            }
            else
            {
                _line.Append(value);
            }
        }
    }
}
