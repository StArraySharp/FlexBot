using System.Text.Json;

namespace FlexBot.PluginApi;

/// <summary>一条长期记忆条目（由模型在总结时顺带抽出）。</summary>
public sealed record MemoryNote(string Kind, string Text, int Importance, string Tags);

/// <summary>
/// 结构化记忆条目的解析（P3 尾巴的纯逻辑部分）。
///
/// 背景：按天总结 / 会话总结本来就在调用模型；让它**顺带**输出一个 JSON 数组，
/// 宿主把结果落进长期记忆表，就得到"会自己积累的长期记忆"，且**零额外模型成本**。
///
/// 这里只负责"把模型吐出来的文本解析成可信的记录"，不碰存储也不发请求 —— 因此可单测。
/// 容错：允许被 ``` 包裹、允许前后有话、允许 tags 是数组或逗号串、importance 越界会被夹到 1..5；
/// 解析不出来就返回空列表（**绝不抛异常**：这里是对话链路，不能让记忆抽取搞挂回复）。
/// </summary>
public static class MemoryNoteParser
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "rule", "preference", "fact", "event", "relation",
    };

    /// <summary>解析模型输出里的记忆条目；失败返回空列表</summary>
    public static List<MemoryNote> Parse(string? modelOutput)
    {
        var list = new List<MemoryNote>();
        if (string.IsNullOrWhiteSpace(modelOutput)) return list;

        var json = ExtractArray(modelOutput);
        if (json is null) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var text = Str(item, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                var kind = Str(item, "kind").ToLowerInvariant();
                if (kind.Length == 0 || !Kinds.Contains(kind)) kind = "fact";
                var importance = Int(item, "importance");
                importance = Math.Clamp(importance <= 0 ? 3 : importance, 1, 5);
                var tags = Tags(item);
                text = text.Trim();
                if (text.Length > 500) text = text[..500];
                if (!seen.Add(kind + "|" + text)) continue;      // 同一批里重复的丢掉
                list.Add(new MemoryNote(kind, text, importance, tags));
            }
        }
        catch { return list; }
        return list;
    }

    /// <summary>从任意文本里找出第一个看起来完整的 JSON 数组（容忍 ```json 包裹与前后废话）</summary>
    public static string? ExtractArray(string text)
    {
        var start = text.IndexOf('[');
        while (start >= 0)
        {
            var depth = 0;
            var inStr = false;
            var esc = false;
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inStr)
                {
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                }
            }
            start = text.IndexOf('[', start + 1);
        }
        return null;
    }

    private static string Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static string Tags(JsonElement obj)
    {
        if (!obj.TryGetProperty(nameof(Tags).ToLowerInvariant(), out var v)) return "";
        if (v.ValueKind == JsonValueKind.Array)
            return string.Join(",", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}
