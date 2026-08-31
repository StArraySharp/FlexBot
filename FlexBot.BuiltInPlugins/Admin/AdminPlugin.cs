using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;
using OneBotLib.Events;
using OneBotLib.MessageSegment;
using OneBotLib.Models;

namespace AdminPlugin;

// 主人插件：命令、定时计划、禁用 LLM 群、关注群、戳一戳、群娱乐、群名片通知
public sealed class AdminPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;
    private CancellationTokenSource _cts = null!;
    private readonly List<IDisposable> _subs = [];
    private readonly List<IDisposable> _commandSubs = [];

    // 定时计划
    private string _schedulesFile = null!;
    private List<ScheduleEntry> _schedules = [];

    // 禁用 LLM 的群（持久化）
    private string _noLlmFile = null!;
    private HashSet<long> _noLlmGroups = [];

    // 关注群（与 Chat 插件共享 memory/watched_groups.txt）
    private string _watchedFile = null!;

    // 戳一戳上限（每群，持久化）
    private string _pokeLimitsFile = null!;
    private Dictionary<long, long> _pokeLimits = [];

    public string Name => "Admin";
    public string Version => "1.0.0";
    public string Description => "主人功能：!api/!timer/!cron/!nollm/!watch/!poke、ping/全员/随机100、群名片变更通知、定时发送";

    // ---- 可配置项（WebUI「插件设置」表单）：命令体系归属 Admin ----
    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("CommandPrefix", "全局命令前缀", "text", "!", "消息以该前缀开头才进入命令分发（如 ! 或 /）"),
        new("PokeDefaultLimit", "戳一戳默认上限", "number", "20", "每群未单独设置（!poke max）时的默认次数上限"),
        new("SchedulerIntervalSec", "调度检查间隔（秒）", "number", "15", "定时计划（!timer/!cron）的轮询间隔，最小 3"),
        new("CardBroadcast", "名片变更广播", "bool", "true", "开启后关注群内群名片变更会广播提示"),
    ];

    public Task OnSettingsChangedAsync()
    {
        _ctx?.Log.Info("设置已热应用");
        return Task.CompletedTask;
    }

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;
        var mem = context.Config.MemoryDir;
        _schedulesFile = Path.Combine(mem, "schedules.json");
        _noLlmFile = Path.Combine(mem, "no_llm_groups.txt");
        _watchedFile = Path.Combine(mem, "watched_groups.txt");
        _pokeLimitsFile = Path.Combine(mem, "poke_limits.txt");

        if (File.Exists(_schedulesFile))
        {
            try { _schedules = JsonSerializer.Deserialize<List<ScheduleEntry>>(File.ReadAllText(_schedulesFile)) ?? []; }
            catch { _schedules = []; }
        }
        if (File.Exists(_noLlmFile))
            foreach (var line in File.ReadAllLines(_noLlmFile))
                if (long.TryParse(line.Trim(), out var g)) _noLlmGroups.Add(g);
        _pokeLimits = LoadPokeLimits();

        // 命令优先级最高：先于 AI 插件处理，命中即 Stop（群命令）
        _subs.Add(context.Messages.OnGroup(OnGroupAsync, priority: 100, tag: Name));

        // 注册私聊命令（宿主统一按全局前缀分发；handler 收到去掉前缀和命令名的参数串）
        _commandSubs.Add(context.RegisterCommand("ping", "存活测试", _ => Task.FromResult("pong")));
        _commandSubs.Add(context.RegisterCommand("api", "OneBot API 透传（JSON）", args => RunOwnerApiAsync(args), "api <json>"));
        _commandSubs.Add(context.RegisterCommand("timer", "N 秒后向群发送消息", args => Task.FromResult(HandleScheduleCommand("timer", "timer " + args)), "timer <秒> <群号> <消息>"));
        _commandSubs.Add(context.RegisterCommand("cron", "每天定时向群发送消息", args => Task.FromResult(HandleScheduleCommand("cron", "cron " + args)), "cron <HH:mm> <群号> <消息>"));
        _commandSubs.Add(context.RegisterCommand("timers", "查看定时计划列表", _ => Task.FromResult(HandleScheduleCommand("timers", "timers"))));
        _commandSubs.Add(context.RegisterCommand("untimer", "删除定时计划", args => Task.FromResult(HandleScheduleCommand("untimer", "untimer " + args)), "untimer <计划号>"));
        _commandSubs.Add(context.RegisterCommand("nollm", "禁止群 LLM 回复", args => Task.FromResult(HandleNoLlmCommand("nollm " + args)), "nollm <群号>"));
        _commandSubs.Add(context.RegisterCommand("unnollm", "恢复群 LLM 回复", args => Task.FromResult(HandleNoLlmCommand("unnollm " + args)), "unnollm <群号>"));
        _commandSubs.Add(context.RegisterCommand("nollms", "查看禁用 LLM 的群列表", _ => Task.FromResult(HandleNoLlmCommand("nollms"))));
        _commandSubs.Add(context.RegisterCommand("watch", "添加关注群", args => Task.FromResult(HandleWatchCommand("watch " + args)), "watch <群号>"));
        _commandSubs.Add(context.RegisterCommand("unwatch", "取消关注群", args => Task.FromResult(HandleWatchCommand("unwatch " + args)), "unwatch <群号>"));
        _commandSubs.Add(context.RegisterCommand("watchs", "查看关注群列表", _ => Task.FromResult(HandleWatchCommand("watchs"))));

        // 监控类事件
        _subs.Add(context.Events.On<GroupPokeEventArgs>(e =>
        {
            Console.WriteLine($"[poke] group {e.GroupId}: {e.UserId} -> {e.TargetId}");
            return Task.CompletedTask;
        }, tag: Name));
        _subs.Add(context.Events.On<FriendPokeEventArgs>(e =>
        {
            Console.WriteLine($"[poke] friend: {e.UserId} -> {e.TargetId}");
            return Task.CompletedTask;
        }, tag: Name));
        _subs.Add(context.Events.On<GroupMemberChangeEventArgs>(e =>
        {
            Console.WriteLine($"[member] {e.NoticeType} group {e.GroupId}: {e.UserId} by {e.OperatorId}");
            return Task.CompletedTask;
        }, tag: Name));
        _subs.Add(context.Events.On<GroupCardChangeEventArgs>(OnCardChangeAsync, tag: Name));

        _cts = new CancellationTokenSource();
        _ = SchedulerLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task OnUnloadAsync()
    {
        foreach (var sub in _subs) sub.Dispose();
        _subs.Clear();
        foreach (var sub in _commandSubs) sub.Dispose();
        _commandSubs.Clear();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null!;
        _ctx = null!;
        await Task.CompletedTask;
    }

    // ===================== 群命令（任何群都可用，包括未关注的群） =====================

    private async Task<Handled> OnGroupAsync(GroupMessageEventArgs e)
    {
        var m = e.Message;
        if (!m.GroupId.HasValue) return Handled.Continue;
        var gid = m.GroupId.Value;
        var text = m.PlainText.Trim();

        // 群命令必须带前缀（#ping / #help / #全员 …）；前缀来自本插件设置，实时读取支持热更新
        var prefix = _ctx.GetSetting("CommandPrefix", _ctx.Config.CommandPrefix);
        if (string.IsNullOrEmpty(prefix) || !text.StartsWith(prefix, StringComparison.Ordinal))
            return Handled.Continue; // 不带前缀 → 交给后续插件（AI 等）
        text = text[prefix.Length..].TrimStart();
        if (text.Length == 0) return Handled.Continue; // 只发了前缀本身

        var isOwner = m.UserId == _ctx.Config.OwnerUin;

        if (text == "ping")
        {
            await _ctx.Api.SendGroupMsgAsync(gid, Msg.Quote(m.MessageId, "pong"));
            return Handled.Stop;
        }
        if (text == "全员" || text.Equals("atall", StringComparison.OrdinalIgnoreCase))
        {
            if (!isOwner)
            {
                await _ctx.Api.SendGroupMsgAsync(gid, Msg.Quote(m.MessageId, "仅主人可用"));
                return Handled.Stop;
            }
            await AtEveryoneAsync(gid);
            return Handled.Stop;
        }
        if (text == "随机100" || text.Equals("rand100", StringComparison.OrdinalIgnoreCase))
        {
            if (!isOwner)
            {
                await _ctx.Api.SendGroupMsgAsync(gid, Msg.Quote(m.MessageId, "仅主人可用"));
                return Handled.Stop;
            }
            await RandomAtAsync(gid, 100);
            return Handled.Stop;
        }
        if (text == "help" || text == "帮助")
        {
            var helpText = BuildGroupHelpText(prefix);
            await _ctx.Api.SendGroupMsgAsync(gid, Msg.Quote(m.MessageId, helpText));
            return Handled.Stop;
        }
        // 前缀已在方法开头剥离，这里直接匹配 poke 命令体
        if (text.StartsWith("poke", StringComparison.OrdinalIgnoreCase))
        {
            var pokeBody = text["poke".Length..].TrimStart();
            var pokeResult = await HandlePokeAsync(gid, m, pokeBody);
            if (!string.IsNullOrEmpty(pokeResult))
                await _ctx.Api.SendGroupMsgAsync(gid, Msg.Quote(m.MessageId, pokeResult));
            return Handled.Stop;
        }
        return Handled.Continue; // 非命令 → 交给 Chat 插件
    }

    // ===================== 群名片变更：记录 + 关注群广播 =====================

    private async Task OnCardChangeAsync(GroupCardChangeEventArgs e)
    {
        Console.WriteLine($"[card] group {e.GroupId} user {e.UserId}: \"{e.CardOld}\" -> \"{e.CardNew}\"");
        try
        {
            Directory.CreateDirectory(_ctx.Config.MemoryDir);
            var file = Path.Combine(_ctx.Config.MemoryDir, $"card_history_{e.GroupId}.md");
            await File.AppendAllTextAsync(file, $"\n## {DateTime.Now:yyyy-MM-dd HH:mm:ss} user={e.UserId}\n旧名片: {(string.IsNullOrEmpty(e.CardOld) ? "(无)" : e.CardOld)}\n新名片: {(string.IsNullOrEmpty(e.CardNew) ? "(已移除)" : e.CardNew)}\n");

            // 关注群内广播昵称（群名片）变更，无需鉴权（可在插件设置中关闭）
            if (_ctx.GetSetting("CardBroadcast", true) && LoadWatched().Contains(e.GroupId))
            {
                var segments = new List<MessageSegment>
                {
                    MessageSegment.Text("[昵称变更] "),
                    MessageSegment.At(e.UserId),
                    MessageSegment.Text($" 的群名片从「{(string.IsNullOrEmpty(e.CardOld) ? "无" : e.CardOld)}」改成了「{(string.IsNullOrEmpty(e.CardNew) ? "无" : e.CardNew)}」")
                };
                var r = await _ctx.Api.SendGroupMsgAsync(e.GroupId, segments);
                Console.WriteLine($"[card] broadcast: {(r.Success ? "ok" : "fail: " + r.ErrorMessage)}");
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("card change", ex);
        }
    }

    // ===================== 定时计划 =====================

    private string HandleScheduleCommand(string kind, string text)
    {
        var parts = text.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        switch (kind)
        {
            case "timer":
                if (parts.Length >= 4 && int.TryParse(parts[1], out var sec) && long.TryParse(parts[2], out var gid1) && sec > 0)
                {
                    var entry = new ScheduleEntry { Type = "once", FireAt = DateTimeOffset.Now.ToUnixTimeSeconds() + sec, GroupId = gid1, Message = parts[3] };
                    _schedules.Add(entry);
                    SaveSchedules();
                    return $"已安排 {sec} 秒后发送到群 {gid1}，计划号 {entry.Id}";
                }
                return "格式: !timer <秒> <群号> <消息>";
            case "cron":
                if (parts.Length >= 4 && TimeSpan.TryParse(parts[1], out var ts) && long.TryParse(parts[2], out var gid2))
                {
                    var entry = new ScheduleEntry { Type = "daily", Time = parts[1], GroupId = gid2, Message = parts[3] };
                    _schedules.Add(entry);
                    SaveSchedules();
                    return $"已安排每天 {parts[1]} 发送到群 {gid2}，计划号 {entry.Id}";
                }
                return "格式: !cron <HH:mm> <群号> <消息>";
            case "timers":
                return _schedules.Count == 0
                    ? "暂无定时计划。"
                    : string.Join("\n", _schedules.Select(s => $"{s.Id} [{s.Type}] 群{s.GroupId} {(s.Type == "daily" ? "每天" + s.Time : "将于" + DateTimeOffset.FromUnixTimeSeconds(s.FireAt).ToLocalTime().ToString("MM-dd HH:mm"))}: {s.Message}"));
            case "untimer":
                if (parts.Length >= 2)
                {
                    var removed = _schedules.RemoveAll(x => x.Id == parts[1]);
                    if (removed > 0) { SaveSchedules(); return $"已删除计划 {parts[1]}。"; }
                    return $"未找到计划 {parts[1]}。";
                }
                return "格式: !untimer <计划号>";
            default:
                return "未知命令。";
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken ct)
    {
        _ctx.Log.Info($"scheduler loop started, {_schedules.Count} schedule(s)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                var today = DateTime.Now.ToString("yyyyMMdd");
                var changed = false;

                var onceDue = _schedules.Where(x => x.Type == "once" && x.FireAt <= now).ToList();
                foreach (var s in onceDue)
                {
                    _schedules.Remove(s);
                    Console.WriteLine($"[sched] sending once to {s.GroupId} ...");
                    var r = await _ctx.Api.SendGroupMsgAsync(s.GroupId, s.Message);
                    Console.WriteLine($"[sched] once send result: {(r.Success ? "ok" : r.ErrorMessage)}");
                    Console.WriteLine($"[sched] once fired: group={s.GroupId} msg={s.Message}");
                    changed = true;
                }

                var nowTime = DateTime.Now.ToString("HH:mm");
                var dailyDue = _schedules.Where(x => x.Type == "daily" && x.Time == nowTime && x.LastFireDate != today).ToList();
                foreach (var s in dailyDue)
                {
                    s.LastFireDate = today;
                    await _ctx.Api.SendGroupMsgAsync(s.GroupId, s.Message);
                    Console.WriteLine($"[sched] daily fired: group={s.GroupId} msg={s.Message}");
                    changed = true;
                }

                if (changed) SaveSchedules();
            }
            catch (Exception ex) { Console.WriteLine($"[sched] error: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, _ctx.GetSetting("SchedulerIntervalSec", 15))), ct); }
            catch (OperationCanceledException) { break; }
        }
        _ctx.Log.Info("scheduler loop stopped");
    }

    private void SaveSchedules()
    {
        Directory.CreateDirectory(_ctx.Config.MemoryDir);
        File.WriteAllText(_schedulesFile, JsonSerializer.Serialize(_schedules, BotJson.Indented));
    }

    // ===================== 禁用 LLM / 关注群 =====================

    private string HandleNoLlmCommand(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "nollm":
                if (parts.Length >= 2 && long.TryParse(parts[1], out var gid))
                {
                    _noLlmGroups.Add(gid);
                    SaveNoLlm();
                    return $"已禁止群 {gid} 的 LLM 回复。";
                }
                return "格式: <前缀>nollm <群号>";
            case "unnollm":
                if (parts.Length >= 2 && long.TryParse(parts[1], out var gid2))
                {
                    if (_noLlmGroups.Remove(gid2)) { SaveNoLlm(); return $"已恢复群 {gid2} 的 LLM 回复。"; }
                    return $"群 {gid2} 不在禁用列表。";
                }
                return "格式: <前缀>unnollm <群号>";
            case "nollms":
                return _noLlmGroups.Count == 0 ? "暂无禁用 LLM 的群。" : string.Join("\n", _noLlmGroups.OrderBy(x => x).Select(x => x.ToString()));
            default:
                return "未知命令。";
        }
    }

    private void SaveNoLlm() =>
        File.WriteAllLines(_noLlmFile, _noLlmGroups.OrderBy(x => x).Select(x => x.ToString()));

    private string HandleWatchCommand(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var watched = LoadWatched();
        switch (parts[0])
        {
            case "watch":
                if (parts.Length >= 2 && long.TryParse(parts[1], out var gid))
                {
                    if (watched.Add(gid)) { SaveWatched(watched); return $"已关注群 {gid}。"; }
                    return $"群 {gid} 已在关注列表中。";
                }
                return "格式: <前缀>watch <群号>";
            case "unwatch":
                if (parts.Length >= 2 && long.TryParse(parts[1], out var gid2))
                {
                    if (watched.Remove(gid2)) { SaveWatched(watched); return $"已取消关注群 {gid2}。"; }
                    return $"群 {gid2} 不在关注列表中。";
                }
                return "格式: <前缀>unwatch <群号>";
            case "watchs":
                return watched.Count == 0 ? "尚未关注任何群。" : "关注群: " + string.Join(", ", watched.OrderBy(x => x));
            default:
                return "未知命令。";
        }
    }

    private HashSet<long> LoadWatched()
    {
        var set = new HashSet<long>();
        try
        {
            if (File.Exists(_watchedFile))
                foreach (var line in File.ReadAllLines(_watchedFile))
                    if (long.TryParse(line.Trim(), out var g) && g > 0) set.Add(g);
        }
        catch (Exception ex) { _ctx.Log.Error("load watched groups", ex); }
        return set;
    }

    private void SaveWatched(IEnumerable<long> list)
    {
        try
        {
            Directory.CreateDirectory(_ctx.Config.MemoryDir);
            File.WriteAllLines(_watchedFile, list.Select(x => x.ToString()));
        }
        catch (Exception ex) { _ctx.Log.Error("save watched groups", ex); }
    }

    // ===================== !api 透传 =====================

    private async Task<string> RunOwnerApiAsync(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null || root["action"] is null)
                return "格式错误，示例: !api {\"action\":\"get_group_list\",\"params\":{}}";
            var action = root["action"]!.GetValue<string>();
            var dict = new Dictionary<string, object>();
            if (root["params"] is JsonObject po)
            {
                foreach (var kv in po) dict[kv.Key] = kv.Value?.ToJsonString(BotJson.Compact) ?? "null";
            }
            var resp = await _ctx.Api.CallApiAsync(action, dict);
            return resp.Success
                ? $"ok: {resp.Data.GetRawText()}"
                : $"fail: {resp.ErrorMessage}";
        }
        catch (Exception ex) { return "解析/调用失败: " + ex.Message; }
    }

    // ===================== 戳一戳 =====================

    // poke 命令处理（text 已去前缀）："max N" 由管理员调上限；普通 "@目标 [次数]"
    private async Task<string?> HandlePokeAsync(long groupId, MessageObject m, string text)
    {
        text = text.Trim();
        var maxMatch = Regex.Match(text, @"^max\s*(\d*)\s*$", RegexOptions.IgnoreCase);
        if (maxMatch.Success)
        {
            if (!m.UserId.HasValue || !_ctx.Config.AdminUins.Contains(m.UserId.Value)) return "仅管理员可设置戳一戳上限";
            if (maxMatch.Groups[1].Value.Length == 0)
                return $"本群戳一戳上限: {_pokeLimits.GetValueOrDefault(groupId, DefPokeLimit())}";
            if (!long.TryParse(maxMatch.Groups[1].Value, out var newLimit) || newLimit < 1)
                return "上限必须是正整数（最大 long.MaxValue）";
            _pokeLimits[groupId] = newLimit;
            SavePokeLimits();
            Console.WriteLine($"[pokelimit] group={groupId} limit={newLimit} by owner");
            return $"本群戳一戳上限已设为 {newLimit}";
        }

        var targets = new List<long>();
        var selfId = _ctx.Api.SelfId;
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "at") continue;
            if (!seg.Data.TryGetValue("qq", out var qq)) continue;
            var q = qq?.ToString()?.Trim().Trim('"');
            if (q == "all" || q == selfId.ToString()) continue;
            if (long.TryParse(q, out var uid)) targets.Add(uid);
        }
        var limit = _pokeLimits.GetValueOrDefault(groupId, DefPokeLimit());
        if (targets.Count == 0)
        {
            var prefix = _ctx.GetSetting("CommandPrefix", _ctx.Config.CommandPrefix);
            return $"格式: {prefix}poke @目标 [次数]（戳目标，次数默认 1，上限 {limit}；{prefix}poke max N 调上限）";
        }

        long count = 1;
        var countMatch = Regex.Match(text, @"(\d+)\s*$");
        if (countMatch.Success && long.TryParse(countMatch.Groups[1].Value, out var c))
            count = Math.Clamp(c, 1, limit);

        // 并发执行（每批 32 个请求，防止一次挂起过多任务）
        const int Batch = 32;
        var tasks = new List<Task>();
        foreach (var uid in targets)
            for (var i = 0; i < count; i++)
                tasks.Add(PokeOnceAsync(groupId, uid, i + 1));
        for (var i = 0; i < tasks.Count; i += Batch)
            await Task.WhenAll(tasks.Skip(i).Take(Batch));
        return null;
    }

    private async Task PokeOnceAsync(long groupId, long uid, long round)
    {
        var r = await _ctx.Api.GroupPokeAsync(groupId, uid);
        Console.WriteLine($"[poke] group={groupId} target={uid} round={round}: {(r.Success ? "ok" : "fail: " + r.ErrorMessage)}");
    }

    private long DefPokeLimit() => _ctx.GetSetting("PokeDefaultLimit", 20);

    private Dictionary<long, long> LoadPokeLimits()
    {
        var dict = new Dictionary<long, long>();
        try
        {
            if (File.Exists(_pokeLimitsFile))
            {
                foreach (var line in File.ReadAllLines(_pokeLimitsFile))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2 && long.TryParse(parts[0], out var gid) && long.TryParse(parts[1], out var lim) && lim > 0)
                        dict[gid] = lim;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[pokelimit] load failed: {ex.Message}"); }
        return dict;
    }

    private void SavePokeLimits()
    {
        try
        {
            Directory.CreateDirectory(_ctx.Config.MemoryDir);
            File.WriteAllLines(_pokeLimitsFile, _pokeLimits.Select(kv => $"{kv.Key}:{kv.Value}"));
        }
        catch (Exception ex) { Console.WriteLine($"[pokelimit] save failed: {ex.Message}"); }
    }

    // ===================== 群娱乐 =====================

    // 随机艾特 N 人（单条消息）
    private async Task RandomAtAsync(long groupId, int count)
    {
        var members = await _ctx.Api.GetGroupMemberListAsync(groupId);
        if (!members.Success || members.Data is null)
        {
            Console.WriteLine($"[rand] get members failed: {members.ErrorMessage}");
            return;
        }

        var selfId = _ctx.Api.SelfId;
        var pool = members.Data.Where(x => x.UserId != selfId).Select(x => x.UserId).ToList();
        if (pool.Count == 0)
        {
            Console.WriteLine("[rand] no members to pick.");
            return;
        }

        var chosen = pool.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(count, pool.Count)).ToList();
        var segments = new List<MessageSegment> { MessageSegment.Text($"[随机艾特 {chosen.Count} 人]") };
        foreach (var uid in chosen)
        {
            segments.Add(MessageSegment.At(uid));
            segments.Add(MessageSegment.Text(" "));
        }
        var result = await _ctx.Api.SendGroupMsgAsync(groupId, segments);
        Console.WriteLine($"[rand] sent {chosen.Count}: {(result.Success ? "ok" : "fail: " + result.ErrorMessage)}");
    }

    // @全体成员（真 AtAll，一条消息稳定发送）
    private async Task AtEveryoneAsync(long groupId)
    {
        var segments = new List<MessageSegment>
        {
            MessageSegment.AtAll(),
            MessageSegment.Text(" 全体成员提醒")
        };
        var result = await _ctx.Api.SendGroupMsgAsync(groupId, segments);
        Console.WriteLine($"[atall] one-shot: {(result.Success ? "ok" : "fail: " + result.ErrorMessage)}");
    }

    // 构建群聊帮助文本
    private string BuildGroupHelpText(string prefix)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("可用群命令：");
        sb.AppendLine($"{prefix}help / 帮助 - 显示本帮助");
        sb.AppendLine($"{prefix}ping - 存活测试");
        sb.AppendLine($"{prefix}全员 / atall - @全体成员（仅主人）");
        sb.AppendLine($"{prefix}随机100 / rand100 - 随机艾特 100 人（仅主人）");
        sb.AppendLine($"{prefix}poke @目标 [次数] - 戳一戳（{prefix}poke max N 设置上限）");
        return sb.ToString().TrimEnd();
    }
}

// 定时计划条目
class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Type { get; set; } = "once"; // once | daily
    public long FireAt { get; set; }           // once: 触发时间（Unix 秒）
    public string Time { get; set; } = "";     // daily: "HH:mm"
    public string LastFireDate { get; set; } = "";
    public long GroupId { get; set; }
    public string Message { get; set; } = "";
}
