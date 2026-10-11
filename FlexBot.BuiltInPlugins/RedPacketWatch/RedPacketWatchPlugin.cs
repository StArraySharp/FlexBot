using System.Collections.Concurrent;
using System.Text;
using FlexBot.PluginApi;
using OneBotLib.Models;

namespace RedPacketWatchPlugin;

/// <summary>
/// 红包监控插件：检测到消息中包含 "[QQ红包]请使用新版手机QQ查收红包。" 时，
/// 自动私聊通知主人(附带群号/群名/发送者/时间)，不拦截原消息传播。
/// 前置条件(RequireOwnerInGroup)：群内必须同时存在"通知对象"本人才发送通知。
/// </summary>
public sealed class RedPacketWatchPlugin : IBotPlugin
{
    /// <summary>默认匹配关键词(消息纯文本包含该串即视为红包)。</summary>
    private const string DefaultKeyword = "[QQ红包]请使用新版手机QQ查收红包。";

    private sealed record MemberCache(HashSet<long> Members, DateTime FetchedAt);

    private IBotContext _ctx = null!;
    private readonly List<IDisposable> _subs = [];
    private readonly ConcurrentDictionary<long, MemberCache> _memberCache = new();

    public string Name => "RedPacketWatch";
    public string Version => "1.1.0";
    public string Description => "检测到 QQ 红包消息时私聊通知主人(附群号/群名/发送者/时间；要求通知对象本人也在该群)";

    // ---- 插件设置(WebUI「插件设置」表单自动渲染) ----
    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("Enabled", "启用监控", "bool", "true", "关闭后不再检测红包消息"),
        new("Keyword", "匹配关键词", "text", DefaultKeyword, "消息纯文本包含该字符串即视为红包(多个关键词用换行分隔，任一命中即触发)"),
        new("RequireOwnerInGroup", "要求本人在群", "bool", "true", "开启后：仅当被通知对象本人也在该群时才发送通知(主人与额外通知对象各自判断)"),
        new("NotifyOwner", "通知主人", "bool", "true", "检测到红包时私聊通知 Config.OwnerUin"),
        new("ExtraNotifyUins", "额外通知 QQ 号", "text", "", "每行一个 QQ 号，除主人外一并通知(可留空)"),
        new("MemberCacheSeconds", "成员缓存秒数", "number", "120", "群成员列表缓存时长，避免频繁调用 API；最小 10"),
        new("NotifyContent", "通知内容模板", "text", "", "自定义通知正文；留空使用默认模板。可用占位符: {group_id} {group_name} {user_id} {sender} {time} {message}"),
    ];

    public Task OnSettingsChangedAsync()
    {
        _ctx?.Log.Info("设置已热应用");
        return Task.CompletedTask;
    }

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;

        // 优先级 50：低于 Admin(100) 的命令分发，高于 AI(0)，只在旁路观察
        _subs.Add(context.Messages.OnGroup(OnGroupAsync, priority: 50, tag: Name));

        _ctx.Log.Info("红包监控已加载");
        return Task.CompletedTask;
    }

    public async Task OnUnloadAsync()
    {
        foreach (var sub in _subs) sub.Dispose();
        _subs.Clear();
        _memberCache.Clear();
        _ctx = null!;
        await Task.CompletedTask;
    }

    // ===================== 消息处理 =====================

    private async Task<Handled> OnGroupAsync(OneBotLib.Events.GroupMessageEventArgs e)
    {
        if (!_ctx.GetSetting("Enabled", true)) return Handled.Continue;

        var m = e.Message;
        if (!m.GroupId.HasValue) return Handled.Continue;

        // 纯文本匹配(忽略大小写差异由关键词本身决定，此处按序比较)
        var text = BotHelpers.PlainText(m);
        if (text.Length == 0) text = m.PlainText ?? "";
        if (string.IsNullOrWhiteSpace(text)) return Handled.Continue;

        var keywords = (_ctx.GetSetting("Keyword", DefaultKeyword) ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hit = keywords.Length == 0
            ? text.Contains(DefaultKeyword, StringComparison.Ordinal)
            : keywords.Any(k => text.Contains(k, StringComparison.Ordinal));
        if (!hit) return Handled.Continue;

        var gid = m.GroupId.Value;
        var uid = m.UserId ?? 0;

        // 收集候选通知目标：主人 + 额外名单(去重、排除机器人自己)
        var candidates = new HashSet<long>();
        if (_ctx.GetSetting("NotifyOwner", true))
        {
            var owner = _ctx.Config.OwnerUin;
            if (owner > 0) candidates.Add(owner);
        }
        foreach (var line in (_ctx.GetSetting("ExtraNotifyUins", "") ?? "")
                     .Split(['\r', '\n', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(line, out var id) && id > 0) candidates.Add(id);
        }
        candidates.Remove(_ctx.Api.SelfId);
        if (candidates.Count == 0) return Handled.Continue;

        // 前置条件：通知对象本人必须在该群(各自判断，共享一次成员列表查询)
        if (_ctx.GetSetting("RequireOwnerInGroup", true))
        {
            var members = await GetGroupMembersAsync(gid);
            if (members is null)
            {
                _ctx.Log.Warn($"群 {gid} 成员列表获取失败，跳过本次通知");
                return Handled.Continue;
            }
            candidates.IntersectWith(members);
            if (candidates.Count == 0)
            {
                _ctx.Log.Info($"红包 group={gid}：通知对象均不在群内，跳过");
                return Handled.Continue;
            }
        }

        // 群名：优先事件自带，其次 API 查询
        var groupName = m.GroupName;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            try
            {
                var r = await _ctx.Api.GetGroupInfoAsync(gid);
                if (r.Success && r.Data is not null) groupName = r.Data.GroupName;
            }
            catch { /* 群名获取失败不影响通知 */ }
        }
        if (string.IsNullOrWhiteSpace(groupName)) groupName = "(未知群名)";

        var body = BuildNotice(gid, groupName, uid, m);
        _ctx.Log.Info($"检测到红包 group={gid} user={uid} → 通知 {string.Join(',', candidates)}");

        foreach (var target in candidates)
        {
            try
            {
                var r = await _ctx.Api.SendPrivateMsgAsync(target, body);
                if (!r.Success) _ctx.Log.Warn($"通知 {target} 失败: {r.ErrorMessage}");
            }
            catch (Exception ex)
            {
                _ctx.Log.Error($"通知 {target} 异常", ex);
            }
        }

        // 只做提醒，不拦截消息(继续传播给后续插件)
        return Handled.Continue;
    }

    /// <summary>取群成员 QQ 集合(带 TTL 缓存)；失败返回 null(区别于"空群")。</summary>
    private async Task<HashSet<long>?> GetGroupMembersAsync(long groupId)
    {
        var ttlSec = Math.Max(10, _ctx.GetSetting("MemberCacheSeconds", 120));
        if (_memberCache.TryGetValue(groupId, out var cached) &&
            DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromSeconds(ttlSec))
            return cached.Members;

        try
        {
            var r = await _ctx.Api.GetGroupMemberListAsync(groupId);
            if (!r.Success || r.Data is null) return null;

            var set = new HashSet<long>(r.Data.Select(x => x.UserId));
            _memberCache[groupId] = new MemberCache(set, DateTime.UtcNow);
            return set;
        }
        catch (Exception ex)
        {
            _ctx.Log.Error($"获取群 {groupId} 成员列表异常", ex);
            return null;
        }
    }

    private string BuildNotice(long groupId, string groupName, long userId, MessageObject m)
    {
        var sender = m.SenderName;
        var time = DateTimeOffset.FromUnixTimeSeconds(m.Time > 0 ? m.Time : DateTimeOffset.Now.ToUnixTimeSeconds())
            .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        var template = _ctx.GetSetting("NotifyContent", "") ?? "";
        if (!string.IsNullOrWhiteSpace(template))
        {
            return template
                .Replace("{group_id}", groupId.ToString())
                .Replace("{group_name}", groupName)
                .Replace("{user_id}", userId.ToString())
                .Replace("{sender}", sender)
                .Replace("{time}", time)
                .Replace("{message}", BotHelpers.PlainText(m));
        }

        var sb = new StringBuilder();
        sb.AppendLine("🧧 检测到红包!");
        sb.AppendLine($"群号：{groupId}");
        sb.AppendLine($"群名：{groupName}");
        sb.AppendLine($"发送者：{sender} ({userId})");
        sb.AppendLine($"时间：{time}");
        sb.Append($"内容：{BotHelpers.PlainText(m)}");
        return sb.ToString();
    }
}
