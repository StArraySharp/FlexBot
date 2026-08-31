using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OneBotLib.MessageSegment;
using OneBotLib.Models;

namespace AgentPlugin;

// 纯静态工具函数：无状态、可安全全局共享
static class ChatUtils
{
    // QQ 表情（face 段）→ 名称文本，让 AI 感知用户发的表情
    private static readonly Dictionary<int, string> FaceNames = new()
    {
        { 0, "惊讶" }, { 1, "撇嘴" }, { 2, "色" }, { 3, "发呆" }, { 4, "得意" }, { 5, "流泪" }, { 6, "害羞" }, { 7, "闭嘴" },
        { 8, "睡" }, { 9, "大哭" }, { 10, "尴尬" }, { 11, "发怒" }, { 12, "调皮" }, { 13, "呲牙" }, { 14, "微笑" }, { 15, "难过" },
        { 16, "酷" }, { 17, "冷汗" }, { 18, "抓狂" }, { 19, "吐" }, { 20, "偷笑" }, { 21, "愉快" }, { 22, "白眼" }, { 23, "傲慢" },
        { 24, "饥饿" }, { 25, "困" }, { 26, "惊恐" }, { 27, "流汗" }, { 28, "憨笑" }, { 29, "悠闲" }, { 30, "奋斗" }, { 31, "咒骂" },
        { 32, "疑问" }, { 33, "嘘" }, { 34, "晕" }, { 35, "衰" }, { 36, "骷髅" }, { 37, "敲打" }, { 38, "再见" }, { 39, "擦汗" },
        { 40, "抠鼻" }, { 41, "鼓掌" }, { 42, "糗大了" }, { 43, "坏笑" }, { 44, "左哼哼" }, { 45, "右哼哼" }, { 46, "哈欠" }, { 47, "鄙视" },
        { 48, "委屈" }, { 49, "快哭了" }, { 50, "阴险" }, { 51, "亲亲" }, { 52, "吓" }, { 53, "可怜" }, { 54, "菜刀" }, { 55, "西瓜" },
        { 56, "啤酒" }, { 57, "篮球" }, { 58, "乒乓" }, { 59, "咖啡" }, { 60, "饭" }, { 61, "猪头" }, { 62, "玫瑰" }, { 63, "凋谢" },
        { 64, "嘴唇" }, { 65, "爱心" }, { 66, "心碎" }, { 67, "蛋糕" }, { 68, "闪电" }, { 69, "炸弹" }, { 70, "刀" }, { 71, "足球" },
        { 72, "便便" }, { 73, "月亮" }, { 74, "太阳" }, { 75, "礼物" }, { 76, "拥抱" }, { 77, "强" }, { 78, "弱" }, { 79, "握手" },
        { 80, "胜利" }, { 81, "抱拳" }, { 82, "勾引" }, { 83, "拳头" }, { 84, "差劲" }, { 85, "爱你" }, { 86, "NO" }, { 87, "OK" },
        { 88, "爱情" }, { 89, "飞吻" }, { 90, "跳跳" }, { 91, "发抖" }, { 92, "怄火" }, { 93, "转圈" }, { 94, "磕头" }, { 95, "回头" },
        { 96, "跳绳" }, { 97, "挥手" }, { 98, "激动" }, { 99, "街舞" }, { 100, "献吻" }, { 101, "左太极" }, { 102, "右太极" }, { 103, "双喜" },
        { 104, "鞭炮" }, { 105, "灯笼" }, { 106, "发财" }, { 107, "K歌" }, { 108, "购物" }, { 109, "邮件" }, { 110, "帅" }, { 111, "喝彩" },
        { 112, "祈祷" }, { 113, "爆筋" }, { 114, "棒棒糖" }, { 115, "喝奶" }, { 116, "下面" }, { 117, "香蕉" }, { 118, "飞机" }, { 119, "开车" },
        { 120, "左车头" }, { 121, "车厢" }, { 122, "右车头" }, { 123, "多云" }, { 124, "下雨" }, { 125, "钞票" }, { 126, "熊猫" }, { 127, "灯泡" },
        { 128, "风车" }, { 129, "闹钟" }, { 130, "打伞" }, { 131, "彩球" }, { 132, "钻戒" }, { 133, "沙发" }, { 134, "纸巾" }, { 135, "药" },
        { 136, "手枪" }, { 137, "茶" }, { 138, "眨眼睛" }, { 139, "泪奔" }, { 140, "无奈" }, { 141, "卖萌" }, { 142, "小纠结" }, { 143, "喷血" },
        { 144, "斜眼笑" }, { 145, "失望" }, { 146, "吐血" }, { 147, "严肃" }, { 148, "无语" }, { 149, "托腮" }, { 150, "摊手" }, { 151, "伸懒腰" },
        { 152, "凶" }, { 153, "疑似" }, { 154, "我错了" }, { 155, "喷水" }, { 156, "抠鼻屎" }, { 157, "鼓掌" }, { 158, "赞" }, { 159, "讽刺" }
    };

    // 预编译常用正则（避免热路径每次 new Regex）
    public static readonly Regex AtRegex = new(@"\[CQ:at[^\]]*\]", RegexOptions.Compiled);
    public static readonly Regex KobeNameRegex = new(@"科比", RegexOptions.Compiled);
    public static readonly Regex ImageWantRegex = new(@"(图|图片|截图|屏幕|看)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 强制限制输出长度：超过 max 字时截断到最后一个标点，找不到则硬截并加省略号
    public static string TrimToMaxChars(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        var cut = s.Substring(0, max);
        var idx = -1;
        foreach (var c in new[] { '。', '！', '？', '；', '，', '.', '!', '?', ',', '…' })
        {
            var i = cut.LastIndexOf(c);
            if (i > max / 2) idx = Math.Max(idx, i);
        }
        return idx > 0 ? cut.Substring(0, idx + 1) : cut + "…";
    }

    // QQ 表情（face 段）→ 名称文本
    public static string ExtractFaceText(MessageObject m)
    {
        var sb = new StringBuilder();
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "face") continue;
            if (!seg.Data.TryGetValue("id", out var id)) { sb.Append("[QQ表情] "); continue; }
            var s = id?.ToString()?.Trim().Trim('"');
            if (int.TryParse(s, out var n) && FaceNames.TryGetValue(n, out var name)) sb.Append($"[QQ表情:{name}] ");
            else sb.Append($"[QQ表情#{s}] ");
        }
        return sb.ToString().Trim();
    }

    // 消息是否包含有意义的文字（纯图片/文件/表情视为无文字）
    public static bool HasText(MessageObject m)
    {
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "text") continue;
            if (!seg.Data.TryGetValue("text", out var t)) continue;
            var s = t?.ToString()?.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(s)) return true;
        }
        return false;
    }

    // 消息文本（仅 text 段）
    public static string MsgToText(object msg)
    {
        if (msg is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var seg in je.EnumerateArray())
                {
                    if (seg.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        seg.TryGetProperty("data", out var d) && d.TryGetProperty("text", out var txt))
                        sb.Append(txt.GetString());
                }
                return sb.ToString();
            }
            return je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.GetRawText();
        }
        return msg.ToString() ?? "";
    }

    // 把 GetMsgAsync 返回的 object（JsonElement 消息数组）转成 MessageSegmentData 列表
    public static List<MessageSegmentData> JsonToSegments(object message)
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
                        seg.Data[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString() ?? "",
                            JsonValueKind.Number => prop.Value.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => prop.Value.GetRawText()
                        };
                }
                list.Add(seg);
            }
        }
        return list;
    }

    // 是否 @ 了机器人（含 @全体）
    public static bool IsMentioned(MessageObject m, long selfId)
    {
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "at") continue;
            if (!seg.Data.TryGetValue("qq", out var qq)) continue;
            var q = qq?.ToString()?.Trim().Trim('"');
            if (q == selfId.ToString() || q == "all") return true;
        }
        return false;
    }

    // 是否 @ 了其他人（另一台机器人/他人），排除 @全体
    public static bool MentionsOthers(MessageObject m, long selfId)
    {
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "at") continue;
            if (!seg.Data.TryGetValue("qq", out var qq)) continue;
            var q = qq?.ToString()?.Trim().Trim('"');
            if (q == "all") continue;
            if (q != selfId.ToString()) return true;
        }
        return false;
    }

    public static string StripMentions(string raw) =>
        AtRegex.Replace(raw, " ").Trim();

    // 回复时引用消息源
    public static List<MessageSegment> Quote(long messageId, string text) =>
    [
        MessageSegment.Reply(messageId),
        MessageSegment.Text(text)
    ];

    // 图片路径 → DataContent（先按扩展名判断 mime，缺失/未知的用文件头魔数嗅探兜底）
    public static AIContent? LoadImageAsDataContent(string path, int maxImageBytes)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length > maxImageBytes) return null;
        var bytes = File.ReadAllBytes(path);
        var mime = fi.Extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".png" when bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 => "image/png",
            _ => SniffImageMime(bytes) ?? "image/jpeg"
        };
        return new DataContent(new ReadOnlyMemory<byte>(bytes), mime);
    }

    // 文件头魔数嗅探图片类型（无法识别返回 null）
    private static string? SniffImageMime(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif"; // GIF8
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp"; // RIFF....WEBP
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp"; // BM
        return null;
    }

    // 主人是否对该群禁用了 LLM（文件与 Admin 插件共享：memory/no_llm_groups.txt）
    public static bool IsNoLlm(string memoryDir, long groupId)
    {
        try
        {
            var path = Path.Combine(memoryDir, "no_llm_groups.txt");
            if (!File.Exists(path)) return false;
            foreach (var line in File.ReadAllLines(path))
                if (long.TryParse(line.Trim(), out var g) && g == groupId) return true;
            return false;
        }
        catch { return false; }
    }
}
