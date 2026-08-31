using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;
using Microsoft.Extensions.AI;
using OneBotLib.Events;
using OneBotLib.MessageSegment;
using OneBotLib.Models;

namespace AgentPlugin;

// AI 对话插件：私聊/群聊 AI 回复（含被 @ 必回、普通消息选择性回复、图片理解）
public sealed class ChatPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;
    private BotTools _tools = null!;
    private ContextExtractor _extractor = null!;
    private AgentService _agent = null!;
    private readonly List<IDisposable> _subs = [];

    // 触发消息 → 待回复的取消令牌：消息被撤回时取消，停止后续分段发送
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _pending = [];

    public string Name => "Agent";
    public string Version => "1.0.0";
    public string Description => "AI 对话（Kobe）：私聊问答、群聊 @ 回复、图片理解、联网搜索、记忆";

    // ---- 可配置项（WebUI「插件设置」表单）：模型与人格归属 Agent 插件 ----
    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("GroupChatEnabled", "启用群聊 AI 回复", "bool", "true", "全局开关：关闭后所有群不做 AI 回复（私聊不受影响）；单群粒度用 !nollm"),
        new("ApiKey", "LLM API Key", "password", "", "OpenAI 兼容服务的密钥"),
        new("BaseUrl", "LLM Base URL", "text", "https://open.bigmodel.cn/api/paas/v4", "OpenAI 兼容地址（完整 base，含 /v1）"),
        new("Model", "模型名", "text", "glm-4-flash", "主模型；失败时按备用列表回落"),
        new("FallbackModels", "备用模型回落链", "models", "[]", "主模型失败时按顺序尝试；每行可独立测试"),
        new("Personas", "人格", "personas", "[]", "可维护多套系统提示词；必须且只能启用一个"),
        new("NameKeywords", "名字唤醒关键词", "text", "科比", "逗号分隔，消息以任一关键词开头即视为被呼叫（等同被 @）"),
        new("ImageReplyProbability", "纯图片/表情回复概率 %", "number", "20", "无文字消息触发 AI 判断的概率"),
    ];

    // 名字关键词正则（由设置构建，热更新）
    private Regex _nameRegex = new("科比", RegexOptions.Compiled);

    public Task OnSettingsChangedAsync()
    {
        RebuildNameRegex();
        _ctx?.Log.Info($"设置已热应用（关键词等）");
        return Task.CompletedTask;
    }

    private void RebuildNameRegex()
    {
        var kws = (_ctx.GetSetting("NameKeywords", "科比") ?? "科比")
            .Split([',', '，', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape)
            .Where(k => k.Length > 0)
            .ToList();
        if (kws.Count == 0) kws.Add(Regex.Escape("科比"));
        // 锚定开头：唤醒词必须出现在消息起始处才视为呼叫（避免正文提及即触发）
        _nameRegex = new Regex("^(?:" + string.Join("|", kws) + ")", RegexOptions.Compiled);
    }

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;

        _tools = new BotTools(context);
        _extractor = new ContextExtractor(context);
        _agent = new AgentService(context, _tools);
        RebuildNameRegex();

        // AI 对话优先级最低：命令插件（Admin/Fun）先处理，未命中的消息才进入 AI
        _subs.Add(context.Messages.OnPrivate(OnPrivateAsync, priority: 0, tag: Name));
        _subs.Add(context.Messages.OnGroup(OnGroupAsync, priority: 0, tag: Name));

        // 消息撤回 → 中止对该消息的回复（停止发送后续分段）
        _subs.Add(context.Events.On<GroupRecallEventArgs>(e => CancelByRecall(e.MessageId, $"group {e.GroupId}"), tag: Name));
        _subs.Add(context.Events.On<FriendRecallEventArgs>(e => CancelByRecall(e.MessageId, $"friend {e.UserId}"), tag: Name));
        return Task.CompletedTask;
    }

    private Task CancelByRecall(long messageId, string source)
    {
        if (messageId != 0 && _pending.TryRemove(messageId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            Console.WriteLine($"[agent] 触发消息 {messageId} 已被撤回（{source}），中止回复");
        }
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync()
    {
        foreach (var sub in _subs) sub.Dispose();
        _subs.Clear();
        foreach (var cts in _pending.Values) { try { cts.Cancel(); cts.Dispose(); } catch { } }
        _pending.Clear();
        _agent = null!;
        _extractor = null!;
        _tools = null!;
        _ctx = null!;
        return Task.CompletedTask;
    }

    // ---- 私聊（宿主已确保仅主人）；流式分段与群聊一致 ----
    private async Task<Handled> OnPrivateAsync(PrivateMessageEventArgs e)
    {
        var m = e.Message;
        if (!m.UserId.HasValue) return Handled.Continue;

        var quoted = await _extractor.ExtractQuotedContextAsync(m);
        var imgParts = await _extractor.ExtractImagePartsAsync(m);
        var quotedImg = await _extractor.ExtractQuotedImagePartsAsync(m);
        if (quotedImg.Count > 0) imgParts.AddRange(quotedImg);
        var faceText = ChatUtils.ExtractFaceText(m);
        var prompt = m.PlainText;
        if (faceText.Length > 0) prompt += "\n" + faceText;
        var cts = new CancellationTokenSource();
        _pending[m.MessageId] = cts;
        try
        {
            var (onStream, flush) = MakeStreamer(isGroup: false, targetId: m.UserId.Value, messageId: m.MessageId, ct: cts.Token);
            var reply = await _agent.AskAgentAsync(prompt, m.UserId.Value, m.SenderName, 0, quoted, imgParts, onStream);
            var sent = await flush();
            if (cts.IsCancellationRequested) return Handled.Continue;
            if (reply.StartsWith("[AI 错误]"))
                await _ctx.Api.SendPrivateMsgAsync(m.UserId.Value, BotHelpers.Quote(m.MessageId, reply));
            else if (!sent && !string.IsNullOrWhiteSpace(reply))
                await SendSegmentedAsync(isGroup: false, targetId: m.UserId.Value, messageId: m.MessageId, text: reply, ct: cts.Token);
        }
        finally
        {
            if (_pending.TryRemove(m.MessageId, out var done)) done.Dispose();
        }
        return Handled.Continue;
    }

    // ---- 群消息 ----
    private async Task<Handled> OnGroupAsync(GroupMessageEventArgs e)
    {
        var m = e.Message;
        if (!m.GroupId.HasValue) return Handled.Continue;
        var gid = m.GroupId.Value;
        var api = _ctx.Api;

        // 全局开关：GroupChatEnabled=false 时所有群不做 AI 处理（私聊不受影响）
        if (!_ctx.GetSetting("GroupChatEnabled", true)) return Handled.Continue;

        // 判断是否关注群（未关注群不做 AI 处理；命令插件已在前面处理过命令）
        var isWatched = _tools.IsWatched(gid);
        if (isWatched)
        {
            Console.WriteLine($"[group] {gid} {m.SenderName}: {m.PlainText}");
            // 每条关注群消息都记录：群历史缓存即时失效，确保后续 AI 调用拿到含最新消息的上下文
            _extractor.NoteGroupMessage(gid, m.MessageId);
        }
        else
        {
            Console.WriteLine($"[group] IGNORED (not watched) {gid}");
            return Handled.Continue;
        }

        // 主人禁用了该群的 LLM 回复：AI 一律跳过
        if (ChatUtils.IsNoLlm(_ctx.Config.MemoryDir, gid))
        {
            Console.WriteLine($"[group] {gid}: LLM disabled by owner, skip AI");
            return Handled.Continue;
        }

        // 精确匹配名字 科比：被 @ 或消息开头叫"科比"时回应（便捷方法来自 PluginApi.BotHelpers）
        var calledByName = _nameRegex.IsMatch(m.PlainText.TrimStart());
        var atMe = BotHelpers.IsMentionedMe(m, m.SelfId);

        // 消息 @ 了其他人/别的机器人，但既没 @ 本机器人也没叫名字 → 视为发给对方的，保持沉默
        if (!atMe && !calledByName && BotHelpers.MentionsOthers(m, m.SelfId))
        {
            Console.WriteLine($"[group] {gid}: mentions another user/bot, stay silent");
            return Handled.Continue;
        }

        if (atMe || calledByName)
        {
            // 被艾特或被叫名字：必须回复 → 并行提取引用/图片/表情（API 调用不互相依赖，并行显著提速）
            var quotedTask = _extractor.ExtractQuotedContextAsync(m);
            var imgPartsTask = _extractor.ExtractImagePartsAsync(m);
            var quotedImgTask = _extractor.ExtractQuotedImagePartsAsync(m);
            var faceText = ChatUtils.ExtractFaceText(m);
            var prompt = BotHelpers.StripMentions(m.RawMessage);
            prompt = _nameRegex.Replace(prompt, " ").Trim();
            if (prompt.Length == 0) prompt = "（用户叫了你的名字）";
            prompt = $"[呼叫人: {m.SenderName}(QQ={m.UserId})] {prompt}";
            if (faceText.Length > 0) prompt += "\n" + faceText;
            // 只有涉及"图/截图/看"等关键词才解析历史图片，普通回复跳过（大幅提速首响）；视觉能力由 AgentService 运行时探测（失败自动去图重试）
            var wantImg = ChatUtils.ImageWantRegex.IsMatch(prompt);
            var ctxTask = _extractor.GetRecentGroupContextAsync(gid, withImgs: wantImg);
            var quoted = await quotedTask;
            var imgParts = await imgPartsTask;
            var quotedImg = await quotedImgTask;
            if (quotedImg.Count > 0) imgParts.AddRange(quotedImg);
            var (recentCtx, recentImgs) = await ctxTask;
            var ctxImgs = new List<AIContent>(imgParts);
            ctxImgs.AddRange(await _extractor.LoadImgsAsync(recentImgs));
            // 流式分段：被 @ 必回路径启用（工具调用独立成条 + 分段长回复）
            var cts = new CancellationTokenSource();
            _pending[m.MessageId] = cts;
            try
            {
                var (onStream, flush) = MakeStreamer(isGroup: true, targetId: gid, messageId: m.MessageId, ct: cts.Token);
                var answer = await _agent.AskAgentAsync(prompt, m.UserId ?? 0, m.SenderName, gid,
                    string.Join("\n", new[] { quoted, recentCtx }.Where(x => !string.IsNullOrEmpty(x))), ctxImgs, onStream);
                var sent = await flush(); // 补发末尾余段
                if (cts.IsCancellationRequested) return Handled.Continue;
                if (answer.StartsWith("[AI 错误]"))
                    await api.SendGroupMsgAsync(gid, BotHelpers.Quote(m.MessageId, answer));
                else if (!sent && !string.IsNullOrWhiteSpace(answer))
                    await SendSegmentedAsync(isGroup: true, targetId: gid, messageId: m.MessageId, text: answer, ct: cts.Token);
            }
            finally
            {
                if (_pending.TryRemove(m.MessageId, out var done)) done.Dispose();
            }
        }
        else
        {
            // 非 @ 消息：先做轻量判断（是否需要 AI 处理），需要时才提取引用/图片/表情
            var hasText = BotHelpers.HasText(m);
            var faceText = ChatUtils.ExtractFaceText(m);

            // 纯图片/文件/表情消息：有图片或表情时给 AI 看一眼决定是否回（不再直接忽略）
            if (!hasText)
            {
                if (faceText.Length == 0)
                {
                    Console.WriteLine($"[group] {gid}: non-text message, stay silent");
                    return Handled.Continue;
                }

                // 普通图片/表情消息按概率尝试回复（AI 判断是否值得回；插件设置 ImageReplyProbability）
                if (Random.Shared.NextDouble() >= _ctx.GetSetting("ImageReplyProbability", 20) / 100.0)
                {
                    Console.WriteLine($"[group] {gid}: random gate passed, stay silent");
                    return Handled.Continue;
                }

                // 决定处理：此时才提取当前消息图片 + 引用（避免每条消息都调 API）
                var quoted = await _extractor.ExtractQuotedContextAsync(m);
                var imgParts = await _extractor.ExtractImagePartsAsync(m);
                var quotedImg = await _extractor.ExtractQuotedImagePartsAsync(m);
                if (quotedImg.Count > 0) imgParts.AddRange(quotedImg);
                var prompt = faceText.Length > 0
                    ? $"[呼叫人: {m.SenderName}(QQ={m.UserId})] （用户没有附带文字，发来了{faceText}）"
                    : $"[呼叫人: {m.SenderName}(QQ={m.UserId})] （用户没有附带文字，发来了一张图片）";
                // 图片消息：本身就要看图，带上最近历史图片（并行加载）；非视觉模型（deepseek）不加载图片
                var (recentCtx, recentImgs) = await _extractor.GetRecentGroupContextAsync(gid, withImgs: _ctx.Config.IsVisionModel);
                var ctxImgs = new List<AIContent>(imgParts);
                ctxImgs.AddRange(await _extractor.LoadImgsAsync(recentImgs));
                var cts1 = new CancellationTokenSource();
                _pending[m.MessageId] = cts1;
                try
                {
                    var (onStream1, flush1) = MakeStreamer(isGroup: true, targetId: gid, messageId: m.MessageId, ct: cts1.Token);
                    var answer = await _agent.AskAgentAsync(prompt, m.UserId ?? 0, m.SenderName, gid,
                        string.Join("\n", new[] { quoted, recentCtx }.Where(x => !string.IsNullOrEmpty(x)))
                        + "\n【提醒】这是一条只有图片/表情的普通群消息，你可以选择回复或保持沉默：若觉得值得回复就正常回复；若不值得或没什么可说的，只回复 SILENT。",
                        ctxImgs, onStream1);
                    var sent1 = await flush1();
                    if (string.IsNullOrWhiteSpace(answer) || answer.Trim().ToUpperInvariant().StartsWith("SILENT"))
                    {
                        Console.WriteLine($"[group] {gid}: decided to stay silent");
                    }
                    else if (!sent1 && !cts1.IsCancellationRequested)
                    {
                        await SendSegmentedAsync(isGroup: true, targetId: gid, messageId: m.MessageId, text: answer, ct: cts1.Token);
                    }
                }
                finally
                {
                    if (_pending.TryRemove(m.MessageId, out var done1)) done1.Dispose();
                }
                return Handled.Continue;
            }

            // 普通文本消息：不再做概率静默，全部交给 AI 判断（SILENT 即不回复）
            // 决定处理：此时才提取引用/图片（避免每条消息都调 API）
            var quoted2 = await _extractor.ExtractQuotedContextAsync(m);
            var imgParts2 = await _extractor.ExtractImagePartsAsync(m);
            var quotedImg2 = await _extractor.ExtractQuotedImagePartsAsync(m);
            if (quotedImg2.Count > 0) imgParts2.AddRange(quotedImg2);

            // 选择性回复：AI 判断是否值得回，返回 SILENT（或空）则保持沉默，不发送
            var prompt2 = m.PlainText;
            if (faceText.Length > 0) prompt2 += "\n" + faceText;
            // 普通消息：不解析历史图片（快），除非消息本身带图或含图关键词；是否真正支持视觉由 AgentService 运行时探测
            var wantImg2 = imgParts2.Count > 0 || ChatUtils.ImageWantRegex.IsMatch(prompt2);
            var (recentCtx2, recentImgs2) = await _extractor.GetRecentGroupContextAsync(gid, withImgs: wantImg2);
            var ctxImgs2 = new List<AIContent>(imgParts2);
            ctxImgs2.AddRange(await _extractor.LoadImgsAsync(recentImgs2));
            var cts2 = new CancellationTokenSource();
            _pending[m.MessageId] = cts2;
            try
            {
                var (onStream2, flush2) = MakeStreamer(isGroup: true, targetId: gid, messageId: m.MessageId, ct: cts2.Token);
                var answer2 = await _agent.AskAgentAsync(
                    prompt2, m.UserId ?? 0, m.SenderName, gid,
                    string.Join("\n", new[] { quoted2, recentCtx2 }.Where(x => !string.IsNullOrEmpty(x)))
                    + "\n【提醒】这是一条普通群消息，你可以选择回复或保持沉默：若觉得值得回复就正常回复；若不值得或没什么可说的，只回复 SILENT。",
                    ctxImgs2, onStream2);
                var sent2 = await flush2();
                if (string.IsNullOrWhiteSpace(answer2) || answer2.Trim().ToUpperInvariant().StartsWith("SILENT"))
                {
                    Console.WriteLine($"[group] {gid}: decided to stay silent");
                }
                else if (!sent2 && !cts2.IsCancellationRequested)
                {
                    await SendSegmentedAsync(isGroup: true, targetId: gid, messageId: m.MessageId, text: answer2, ct: cts2.Token);
                }
            }
            finally
            {
                if (_pending.TryRemove(m.MessageId, out var done2)) done2.Dispose();
            }
        }
        return Handled.Continue;
    }

    // ===================== 流式分段发送器 =====================
    // 段内上限 ~450 字；双换行/标点处断段；工具调用提示独立成条立即发送；末尾余量 flush 补发。
    // 全部发送经链式队列严格保序；flush 返回是否发送过任何内容（供回落补发完整答案判断）。

    private (AgentService.StreamEvent OnStream, Func<Task<bool>> Flush) MakeStreamer(bool isGroup, long targetId, long messageId, CancellationToken ct = default)
    {
        var gate = new object();
        var buf = new System.Text.StringBuilder();
        var sentAny = false;
        var first = true;
        Task chain = Task.CompletedTask;
        const int segMax = 450;

        async Task SendSafeAsync(List<OneBotLib.MessageSegment.MessageSegment> seg)
        {
            try
            {
                if (isGroup) await _ctx.Api.SendGroupMsgAsync(targetId, seg);
                else await _ctx.Api.SendPrivateMsgAsync(targetId, seg);
            }
            catch (Exception ex) { Console.WriteLine($"[chat] 分段发送失败: {ex.Message}"); }
        }

        // 入队一条消息（调用方持锁）：首条带引用回复，链式排队保证顺序；已取消则丢弃
        void Enqueue(string text)
        {
            if (ct.IsCancellationRequested) return;
            text = text.Trim();
            if (text.Length == 0) return;
            var seg = first
                ? BotHelpers.Quote(messageId, text)
                : new List<OneBotLib.MessageSegment.MessageSegment> { OneBotLib.MessageSegment.MessageSegment.Text(text) };
            first = false;
            sentAny = true;
            chain = chain.ContinueWith(_ => SendSafeAsync(seg), TaskContinuationOptions.ExecuteSynchronously).Unwrap();
        }

        // 尝试切一段（调用方持锁）
        void TryCutSegment(bool force)
        {
            if (buf.Length < (force ? 1 : segMax)) return;
            var s = buf.ToString();
            var cut = force ? s.Length : FindSectionBreak(s, segMax);
            if (cut <= 0) return;
            Enqueue(s[..cut]);
            buf.Remove(0, cut);
        }

        AgentService.StreamEvent OnStreamEvt = (kind, delta) =>
        {
            lock (gate)
            {
                if (ct.IsCancellationRequested) { buf.Clear(); return; } // 撤回：丢弃后续增量
                if (kind == "tool")
                {
                    Enqueue(delta); // 工具调用提示：独立成条，立即发送
                    return;
                }
                buf.Append(delta);
                TryCutSegment(force: false);
            }
        };

        async Task<bool> FlushAsync()
        {
            Task tail;
            lock (gate)
            {
                if (ct.IsCancellationRequested) buf.Clear();
                TryCutSegment(force: true);
                tail = chain;
            }
            await tail;
            return sentAny;
        }

        return (OnStreamEvt, FlushAsync);
    }

    // 非流式/流式无输出时的完整答案补发：复用分段器切分（首段带引用）
    private async Task SendSegmentedAsync(bool isGroup, long targetId, long messageId, string text, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        var (onStream, flush) = MakeStreamer(isGroup, targetId, messageId, ct);
        onStream("text", text);
        await flush();
    }

    // 在 max 附近找段落断点（双换行优先 → 单换行 → 标点 → 硬切）；返回 0 表示不切
    private static int FindSectionBreak(string s, int max)
    {
        if (s.Length <= max) return s.Length;
        var scope = s[..Math.Min(s.Length, (int)(max * 1.4))];
        var idx = scope.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (idx > 80) return idx + 2;
        idx = scope.LastIndexOf('\n');
        if (idx > 80) return idx + 1;
        foreach (var p in new[] { '。', '；', '，', ' ', '！', '？' })
        {
            idx = scope.LastIndexOf(p);
            if (idx > 80) return idx + 1;
        }
        return max;
    }
}
