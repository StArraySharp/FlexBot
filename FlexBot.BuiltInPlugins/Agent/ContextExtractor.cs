using System.Text.Json;
using FlexBot.PluginApi;
using Microsoft.Extensions.AI;
using OneBotLib.Models;

namespace AgentPlugin;

// 上下文提取：引用消息、图片、表情、群历史（带缓存）
class ContextExtractor(IBotContext ctx)
{
    private readonly IBotApi _api = ctx.Api;
    private readonly IBotConfig _cfg = ctx.Config;

    // get_image 结果缓存：file 键 → 本地绝对路径（避免重复调 API，历史图片解析会大量用到）
    private readonly Dictionary<string, string> _imagePathCache = new(StringComparer.OrdinalIgnoreCase);
    // 群历史缓存：group → (时间, 带图标记, 文本上下文, 图片路径, 最新消息ID)
    // LatestMsgId 用于缓存失效判断：群里来了新消息即使未过期也重新拉取，避免用陈旧上下文回复
    private readonly Dictionary<long, (DateTime Time, int Key, string Ctx, List<string> Imgs, long LatestMsgId)> _recentCtxCache = [];

    // 各群最新消息 ID（每条群消息到来时记录，供缓存失效判断）
    private readonly Dictionary<long, long> _latestMsgIdByGroup = [];

    // 供 ChatPlugin 在收到群消息时调用：记录群内最新消息 ID
    public void NoteGroupMessage(long groupId, long messageId)
    {
        _latestMsgIdByGroup[groupId] = messageId;
        // 若缓存中不含这条新消息，立即失效（下次拉取会包含它）
        if (_recentCtxCache.TryGetValue(groupId, out var c) && c.LatestMsgId != messageId)
            _recentCtxCache.Remove(groupId);
    }

    // 提取用户引用的消息内容，作为上下文给 Agent（用于"艾特他"这类指代）
    public async Task<string> ExtractQuotedContextAsync(MessageObject m)
    {
        try
        {
            var replySeg = m.MessageSegments.FirstOrDefault(s => s.Type == "reply");
            if (replySeg is null) return "";
            if (!replySeg.Data.TryGetValue("id", out var idObj)) return "";
            if (!long.TryParse(idObj?.ToString()?.Trim().Trim('"'), out var id)) return "";
            var r = await _api.GetMsgAsync(id);
            if (!r.Success || r.Data is null) return "";
            var text = ChatUtils.MsgToText(r.Data.Message);
            if (string.IsNullOrWhiteSpace(text)) return "";
            return $"【用户引用的消息】(message_id={id}) {text}";
        }
        catch (Exception ex) { Console.WriteLine($"[quoted] extract text failed: {ex.Message}"); return ""; }
    }

    // 提取用户引用消息里的图片，解析成可给模型看的图像内容（AI 才能"看到"被引用消息的图）
    public async Task<List<AIContent>> ExtractQuotedImagePartsAsync(MessageObject m)
    {
        var parts = new List<AIContent>();
        try
        {
            var replySeg = m.MessageSegments.FirstOrDefault(s => s.Type == "reply");
            if (replySeg is null) return parts;
            if (!replySeg.Data.TryGetValue("id", out var idObj)) return parts;
            if (!long.TryParse(idObj?.ToString()?.Trim().Trim('"'), out var id)) return parts;
            var r = await _api.GetMsgAsync(id);
            if (!r.Success || r.Data is null) return parts;
            var segments = ChatUtils.JsonToSegments(r.Data.Message);
            foreach (var seg in segments)
            {
                if (seg.Type != "image") continue;
                var path = await ResolveImagePathAsync(seg.Data);
                if (path is null)
                {
                    parts.Add(new TextContent("[被引用消息中有一张图片（无法读取内容）]"));
                    continue;
                }
                var dc = ChatUtils.LoadImageAsDataContent(path, _cfg.MaxImageBytes);
                if (dc is null)
                {
                    parts.Add(new TextContent("[被引用消息中有一张图片（过大未加载）]"));
                    continue;
                }
                parts.Add(dc);
                Console.WriteLine($"[img] quoted loaded {path}");
            }
            return parts;
        }
        catch (Exception ex) { Console.WriteLine($"[quoted] extract image failed: {ex.Message}"); return parts; }
    }

    // 当前消息图片 → 图像内容
    public async Task<List<AIContent>> ExtractImagePartsAsync(MessageObject m)
    {
        var parts = new List<AIContent>();
        foreach (var seg in m.MessageSegments)
        {
            if (seg.Type != "image") continue;
            var path = await ResolveImagePathAsync(seg.Data);
            if (path is null)
            {
                parts.Add(new TextContent("[用户发来一张图片（无法读取内容）]"));
                continue;
            }
            var dc = ChatUtils.LoadImageAsDataContent(path, _cfg.MaxImageBytes);
            if (dc is null)
            {
                parts.Add(new TextContent($"[用户发来一张图片（过大未加载）]"));
                continue;
            }
            parts.Add(dc);
            Console.WriteLine($"[img] loaded {path}");
        }
        return parts;
    }

    // 把历史图片路径并行加载为 DataContent（避免串行读文件拖慢首响）
    public async Task<List<AIContent>> LoadImgsAsync(List<string> paths)
    {
        var result = new List<AIContent>();
        if (paths.Count == 0) return result;
        var tasks = paths.Select(async p =>
        {
            try
            {
                var dc = ChatUtils.LoadImageAsDataContent(p, _cfg.MaxImageBytes);
                if (dc is not null)
                    Console.WriteLine($"[img] history loaded {p} mime={(dc as DataContent)?.MediaType}");
                return dc; // null = 文件不存在/过大，跳过
            }
            catch { return null; }
        }).ToList();
        var contents = await Task.WhenAll(tasks);
        result.AddRange(contents.Where(c => c is not null)!);
        return result;
    }

    // 发言前查看最近的群聊记录（带缓存，避免频繁拉取）
    // 返回 (文本上下文, 最近图片路径列表)，图片路径可供 AI 本轮看图
    public async Task<(string Ctx, List<string> Imgs)> GetRecentGroupContextAsync(long groupId, bool withImgs = true, int count = 0)
    {
        count = count <= 0 ? _cfg.GroupHistoryCount : count;
        // 缓存键区分是否带图（带图和不带图的上下文不同）
        var cacheKey = withImgs ? 1 : 0;
        // 缓存有效性：时间未过期 + 模式一致 + 群里没有新消息（_latestMsgIdByGroup 未超前于缓存）
        var fresh = _recentCtxCache.TryGetValue(groupId, out var cached)
            && (DateTime.Now - cached.Time).TotalSeconds < _cfg.ContextCacheSeconds
            && cached.Key == cacheKey
            && (!_latestMsgIdByGroup.TryGetValue(groupId, out var latest) || latest == cached.LatestMsgId);
        if (fresh) return (cached.Ctx, cached.Imgs);
        var imgs = new List<string>();
        try
        {
            var r = await _api.GetGroupMsgHistoryAsync(groupId, count: count);
            if (!r.Success)
            {
                Console.WriteLine($"[ctx] group {groupId} 拉取群历史失败: {r.ErrorMessage}");
                return ("", imgs);
            }
            if (r.Data is null || r.Data.Count == 0)
            {
                Console.WriteLine($"[ctx] group {groupId} 群历史为空（无消息可收集）");
                return ("", imgs);
            }
            var lines = new List<string>();
            var selfId = _api.SelfId;
            // 只解析最近 N 条消息的图片，避免逐张调 get_image API 导致每次 @ 都卡几十秒
            var recentMsgs = r.Data.Where(x => x.MessageType == "group").TakeLast(_cfg.GroupHistoryUseCount).ToList();
            var latestId = recentMsgs.Count > 0 ? recentMsgs[^1].MessageId : 0L;
            Console.WriteLine($"[ctx] group {groupId} 拉到 {r.Data.Count} 条，取最近 {recentMsgs.Count} 条");

            // 第一遍：文本行保持时间正序（旧→新），同时标记哪些消息带图；附相对时间让 AI 感知新鲜度
            var imgMsgIdx = new List<int>();
            for (int i = 0; i < recentMsgs.Count; i++)
            {
                var info = recentMsgs[i];
                var ago = TimestampToRelative(info.Time);
                var text = ChatUtils.MsgToText(info.Message);
                var name = info.Sender?.DisplayName;
                if (string.IsNullOrWhiteSpace(name)) name = info.UserId.ToString();
                if (info.UserId == selfId) name = "我";
                var hasImg = ChatUtils.JsonToSegments(info.Message).Any(s => s.Type == "image");
                if (hasImg) imgMsgIdx.Add(i);
                if (!string.IsNullOrWhiteSpace(text) || hasImg)
                    lines.Add($"[{ago}] {name}: {text.Trim()}{(hasImg ? "[图片]" : "")}");
            }

            // 第二遍：仅当需要图片时才解析（涉及"图"关键词的请求），从最新消息开始（倒序）确保刚发的图优先
            var imgsParsed = 0;
            if (withImgs)
            {
                foreach (var idx in Enumerable.Reverse(imgMsgIdx))
                {
                    if (imgsParsed >= _cfg.GroupHistoryMaxImgs) break;
                    var info = recentMsgs[idx];
                    foreach (var seg in ChatUtils.JsonToSegments(info.Message))
                    {
                        if (seg.Type != "image") continue;
                        if (imgsParsed >= _cfg.GroupHistoryMaxImgs) break;
                        var path = await ResolveImagePathAsync(seg.Data);
                        if (path is not null)
                        {
                            imgs.Add(path);
                            imgsParsed++;
                        }
                    }
                }
            }
            if (lines.Count == 0)
            {
                Console.WriteLine($"[ctx] group {groupId} 最近 {recentMsgs.Count} 条消息均无文字/图片，无法收集");
                return ("", imgs);
            }
            var ctx = "【最近的群聊记录（从旧到新，含每条距现在的时长；最后一行之后就是你当前要回应的最新消息）】\n" + string.Join("\n", lines);
            _recentCtxCache[groupId] = (DateTime.Now, cacheKey, ctx, imgs, latestId);
            Console.WriteLine($"[ctx] group {groupId} 收集到 {lines.Count} 条群聊消息 (imgs={imgs.Count}, withImgs={withImgs}):");
            foreach (var line in lines)
                Console.WriteLine($"[ctx]   {line}");
            return (ctx, imgs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ctx] error: {ex.Message}");
            return ("", imgs);
        }
    }
    // Unix 时间戳 → 相对时间描述（让 AI 感知消息新鲜度；异常/未来时间返回空）
    private static string TimestampToRelative(long unixSec)
    {
        try
        {
            var span = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeSeconds(unixSec);
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}小时前";
            return $"{(int)span.TotalDays}天前";
        }
        catch { }
        return "";
    }
    // 解析图片路径：1) 本地绝对路径 2) get_image API（带缓存）3) url 下载兜底
    private async Task<string?> ResolveImagePathAsync(Dictionary<string, object> data)
    {
        var fileKey = data.TryGetValue("file", out var f) ? f?.ToString()?.Trim().Trim('"') ?? "" : "";
        if (fileKey.Length > 0)
        {
            // 缓存命中直接返回
            if (_imagePathCache.TryGetValue(fileKey, out var cached) && File.Exists(cached)) return cached;
            // 1) 直接是本地绝对路径
            if (Path.IsPathRooted(fileKey) && File.Exists(fileKey)) { _imagePathCache[fileKey] = fileKey; return fileKey; }
            // 2) 通过 get_image API 解析为本地绝对路径
            try
            {
                var r = await _api.CallApiAsync("get_image", new Dictionary<string, object> { { "file", fileKey } });
                if (r.Success && r.Data.ValueKind == JsonValueKind.Object && r.Data.TryGetProperty("file", out var p))
                {
                    var abs = p.GetString() ?? "";
                    if (File.Exists(abs)) { _imagePathCache[fileKey] = abs; return abs; }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[img] get_image failed: {ex.Message}"); }
        }
        // 3) 兜底：url 字段下载
        if (data.TryGetValue("url", out var u))
        {
            var url = u?.ToString()?.Trim().Trim('"') ?? "";
            if (url.StartsWith("http"))
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    var bytes = await http.GetByteArrayAsync(url);
                    if (bytes.Length <= _cfg.MaxImageBytes)
                    {
                        var tmp = Path.Combine(Path.GetTempPath(), "qqbot_img_" + DateTime.Now.Ticks + ".img");
                        await File.WriteAllBytesAsync(tmp, bytes);
                        return tmp;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[img] url download failed: {ex.Message}"); }
            }
        }
        return null;
    }
}
