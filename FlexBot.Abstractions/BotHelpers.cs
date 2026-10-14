using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneBotLib.Api;
using OneBotLib.MessageSegment;
using OneBotLib.Models;

namespace FlexBot.PluginApi;

/// <summary>
/// 插件共享便捷方法：消息解析、@互转、引用消息、群信息、回复构造等常用操作的静态工具集。
/// 全部无状态，任意插件可安全使用。
/// </summary>
public static class BotHelpers
{
    // ============ 消息文本 ============

    /// <summary>消息纯文本（仅 text 段，去掉 @/图片等标记）。</summary>
    public static string PlainText(MessageObject m)
    {
        var sb = new StringBuilder();
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "text") continue;
            if (seg.Data.TryGetValue("text", out var t))
                sb.Append(t?.ToString()?.Trim().Trim('"'));
        }
        return sb.ToString();
    }

    /// <summary>去掉消息里的所有 @ 标记（CQ 码形式）。</summary>
    public static string StripMentions(string raw) =>
        new Regex(@"\[CQ:at[^\]]*\]").Replace(raw, " ").Trim();

    /// <summary>消息是否包含有意义的文字文本（纯图片/文件/表情视为无文字）。</summary>
    public static bool HasText(MessageObject m)
    {
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "text") continue;
            if (!seg.Data.TryGetValue("text", out var t)) continue;
            if (!string.IsNullOrWhiteSpace(t?.ToString()?.Trim().Trim('"'))) return true;
        }
        return false;
    }

    // ============ @ 与 QQ 号互转 ============

    /// <summary>提取消息里被 @ 的全部 QQ 号（排除 @全体；无则空列表）。</summary>
    public static List<long> GetAtTargets(MessageObject m)
    {
        var list = new List<long>();
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "at") continue;
            if (!seg.Data.TryGetValue("qq", out var qq)) continue;
            var q = qq?.ToString()?.Trim().Trim('"');
            if (q == "all") continue;
            if (long.TryParse(q, out var id) && id > 0) list.Add(id);
        }
        return list;
    }

    /// <summary>消息是否 @ 了机器人（含 @全体）。</summary>
    public static bool IsMentionedMe(MessageObject m, long selfId) =>
        IsAtMe(m, selfId) || IsAtAll(m);

    /// <summary>消息是否 @ 了机器人本人（不含 @全体）。</summary>
    public static bool IsAtMe(MessageObject m, long selfId)
    {
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "at") continue;
            if (!seg.Data.TryGetValue("qq", out var qq)) continue;
            if (qq?.ToString()?.Trim().Trim('"') == selfId.ToString()) return true;
        }
        return false;
    }

    /// <summary>消息是否 @全体。</summary>
    public static bool IsAtAll(MessageObject m)
    {
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "at") continue;
            if (!seg.Data.TryGetValue("qq", out var qq)) continue;
            if (qq?.ToString()?.Trim().Trim('"') == "all") return true;
        }
        return false;
    }

    /// <summary>消息是否 @ 了除机器人外的其他人（用于判断"在跟别人说话"）。</summary>
    public static bool MentionsOthers(MessageObject m, long selfId) =>
        GetAtTargets(m).Any(id => id != selfId);

    /// <summary>QQ 号 → @ 消息段。</summary>
    public static MessageSegment At(long userId) => MessageSegment.At(userId);

    /// <summary>文本 + @多人的混合段列表。</summary>
    public static List<MessageSegment> TextWithAt(string text, params long[] userIds)
    {
        var list = new List<MessageSegment>();
        if (!string.IsNullOrEmpty(text)) list.Add(MessageSegment.Text(text));
        foreach (var id in userIds) list.Add(MessageSegment.At(id));
        return list;
    }

    // ============ 引用消息 ============

    /// <summary>消息中 reply 段的 message_id（未引用返回 null）。</summary>
    public static long? GetReplyToId(MessageObject m)
    {
        var seg = m.MessageSegments.FirstOrDefault(s => s.Type == "reply");
        if (seg is null) return null;
        if (!seg.Data.TryGetValue("id", out var idObj)) return null;
        return long.TryParse(idObj?.ToString()?.Trim().Trim('"'), out var id) ? id : null;
    }

    /// <summary>拉取被引用消息（未引用或拉取失败返回 null）。</summary>
    public static async Task<MsgInfo?> GetQuotedMessageAsync(IBotApi api, MessageObject m)
    {
        var id = GetReplyToId(m);
        if (id is null) return null;
        var r = await api.GetMsgAsync(id.Value);
        return r.Success ? r.Data : null;
    }

    /// <summary>拉取被引用消息的纯文本（失败返回空串）。</summary>
    public static async Task<string> GetQuotedTextAsync(IBotApi api, MessageObject m)
    {
        var info = await GetQuotedMessageAsync(api, m);
        return info is null ? "" : MsgToText(info.Message);
    }

    /// <summary>回复时引用消息源。</summary>
    public static List<MessageSegment> Quote(long messageId, string text) =>
    [
        MessageSegment.Reply(messageId),
        MessageSegment.Text(text)
    ];

    // ============ 群信息 ============

    /// <summary>群成员查找（昵称/群名片模糊匹配，不区分大小写；limit 上限）。</summary>
    public static async Task<List<GroupMemberInfo>> FindMembersAsync(IBotApi api, long groupId, string keyword, int limit = 20)
    {
        var r = await api.GetGroupMemberListAsync(groupId);
        if (!r.Success || r.Data is null) return [];
        return r.Data
            .Where(x => x.CardOrNickname.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        x.Nickname.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    /// <summary>QQ 号 → 当前群名片/昵称（不在群内返回 null）。</summary>
    public static async Task<string?> GetMemberNameAsync(IBotApi api, long groupId, long userId)
    {
        var r = await api.GetGroupMemberListAsync(groupId);
        if (!r.Success || r.Data is null) return null;
        return r.Data.FirstOrDefault(x => x.UserId == userId)?.CardOrNickname;
    }

    /// <summary>整群成员名单文本（每行 "QQ号 名字"，max 限制行数）。</summary>
    public static async Task<string> GetMemberListTextAsync(IBotApi api, long groupId, int max = 200)
    {
        var r = await api.GetGroupMemberListAsync(groupId);
        if (!r.Success || r.Data is null) return "获取失败: " + r.ErrorMessage;
        return string.Join("\n", r.Data.Take(max).Select(x => $"{x.UserId} {x.CardOrNickname}"));
    }

    /// <summary>最近群聊记录文本（每行 "名字: 内容"）。</summary>
    public static async Task<string> GetRecentGroupTextAsync(IBotApi api, long groupId, int count = 20)
    {
        var r = await api.GetGroupMsgHistoryAsync(groupId, null, count);
        if (!r.Success || r.Data is null) return "";
        var sb = new StringBuilder();
        foreach (var msg in r.Data)
            sb.AppendLine($"{NameOf(msg.Sender)}: {MsgToText(msg.Message)}");
        return sb.ToString().TrimEnd();
    }

    // 发送者优先显示群名片，无名片用昵称
    private static string NameOf(SenderInfo s) =>
        string.IsNullOrWhiteSpace(s.Card) ? s.Nickname : s.Card;

    // ============ JSON 消息解析 ============

    /// <summary>
    /// 消息（object）→ 可读纯文本。用于出站记录、历史消息展示、日志。
    ///
    /// 必须覆盖所有实际会被传进来的形态：插件发送时给的是 List&lt;MessageSegment&gt;、
    /// GetMsgAsync 返回的是 JsonElement、宿主自己可能直接传 string。
    /// 早期版本只处理 JsonElement，其余走 ToString()，于是出站记录里会出现
    /// "System.Collections.Generic.List`1[OneBotLib.MessageSegment.MessageSegment]" 这种类型名。
    /// </summary>
    public static string MsgToText(object msg)
    {
        switch (msg)
        {
            case null:
                return "";
            case string s:
                return s;
            case MessageSegment seg:
                return SegToText(seg);
            case JsonElement je:
                return JsonToText(je);
            case IEnumerable seq:                       // List<MessageSegment> / 数组 / 其它集合
                {
                    var sb = new StringBuilder();
                    foreach (var item in seq) sb.Append(MsgToText(item));
                    return sb.ToString();
                }
            default:
                return msg.ToString() ?? "";
        }
    }

    /// <summary>单个消息段 → 文本（文本段出原文，其余给可读占位）</summary>
    private static string SegToText(MessageSegment seg)
    {
        var type = seg.Type ?? "";
        switch (type)
        {
            case "text":
                return DataText(seg.Data, "text");
            case "at":
                {
                    var qq = DataText(seg.Data, "qq");
                    return qq == "all" ? "@全体成员" : "@" + qq;
                }
            case "face": return "[表情]";
            case "image": return "[图片]";
            case "record": return "[语音]";
            case "video": return "[视频]";
            case "file": return "[文件]";
            case "forward": return "[合并转发]";
            case "poke": return "[戳一戳]";
            case "json": return "[卡片]";
            case "xml": return "[XML卡片]";
            case "reply": return "";                    // 回复引用不占正文
            default: return type.Length > 0 ? $"[{type}]" : "";
        }
    }

    private static string DataText(Dictionary<string, object>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var v) || v is null) return "";
        return v switch
        {
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString(),
            _ => v.ToString() ?? "",
        };
    }

    /// <summary>JsonElement 形态（GetMsgAsync 等）：数组按段渲染，字符串原样，其它给原始 JSON</summary>
    private static string JsonToText(JsonElement je)
    {
        if (je.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var seg in je.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object) continue;
                var type = seg.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                JsonElement data = seg.TryGetProperty("data", out var d) ? d : default;
                sb.Append(type switch
                {
                    "text" => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("text", out var tx)
                              ? tx.GetString() ?? "" : "",
                    "at" => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("qq", out var qq)
                            ? (qq.ValueKind == JsonValueKind.String && qq.GetString() == "all" ? "@全体成员" : "@" + qq.ToString())
                            : "@某人",
                    "face" => "[表情]",
                    "image" => "[图片]",
                    "record" => "[语音]",
                    "video" => "[视频]",
                    "file" => "[文件]",
                    "forward" => "[合并转发]",
                    "poke" => "[戳一戳]",
                    "json" => "[卡片]",
                    "xml" => "[XML卡片]",
                    "reply" => "",
                    _ => type.Length > 0 ? $"[{type}]" : "",
                });
            }
            return sb.ToString();
        }
        return je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.GetRawText();
    }

    /// <summary>GetMsgAsync 等返回的 message 字段（object）→ 消息段列表。</summary>
    public static List<MessageSegmentData> MsgToSegments(object message)
    {
        var list = new List<MessageSegmentData>();
        if (message is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in je.EnumerateArray())
            {
                var seg = new MessageSegmentData
                {
                    Type = segment.TryGetProperty("type", out var t) ? t.GetString() ?? "" : ""
                };
                if (segment.TryGetProperty("data", out var d))
                {
                    foreach (var prop in d.EnumerateObject())
                        seg.Data[prop.Name] = prop.Value;
                }
                list.Add(seg);
            }
        }
        return list;
    }
}
