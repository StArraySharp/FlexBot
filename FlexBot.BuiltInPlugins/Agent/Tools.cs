using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;
using Microsoft.Extensions.AI;

namespace AgentPlugin;

// Agent 工具集：所有可被 AI 调用的函数集中于此
class BotTools(IBotContext ctx)
{
    private readonly IBotApi _api = ctx.Api;
    private readonly IBotConfig _cfg = ctx.Config;
    private readonly string _memoryDir = ctx.Config.MemoryDir;

    // 当前对话环境（由外部在每次调用前设置）
    public long CurrentCallerUin;
    public string CurrentCallerName = "";
    public long CurrentGroupId;

    // 最近一次截图保存的路径（AI 调 computer_take_screenshot 时由 LoggingFunction 更新）
    public string? LastScreenshotPath;

    // 本轮对话内 search_web 调用次数（AskAgentAsync 每轮开始重置为 0，上限 3 次防傻搜）
    public int SearchCount;

    private bool IsOwner => _cfg.AdminUins.Contains(CurrentCallerUin);

    public List<AITool> CreateAll()
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(SearchWeb, name: "search_web", description: "使用 DuckDuckGo 联网搜索，返回标题、摘要和链接"),
            AIFunctionFactory.Create(FetchUrl, name: "fetch_url", description: "抓取网页并返回正文文本"),
            AIFunctionFactory.Create(GetCurrentTime, name: "get_current_time", description: "获取当前日期时间"),
            AIFunctionFactory.Create(GetWeather, name: "get_weather", description: "查询指定城市的实时天气（温度、体感、天气状况、湿度、风速），查天气直接用这个，不要用搜索"),
            AIFunctionFactory.Create(GetCurrentGroup, name: "get_current_group", description: "获取当前对话所在群号"),
            AIFunctionFactory.Create(GetGroupMembers, name: "get_group_members", description: "获取当前群成员列表"),
            AIFunctionFactory.Create(FindMemberByName, name: "find_member", description: "按昵称/群名片查找当前群成员（不区分大小写）"),
            AIFunctionFactory.Create(SaveMemory, name: "save_memory", description: "保存长期记忆到本地 Markdown 文件"),
            AIFunctionFactory.Create(ReadMemory, name: "read_memory", description: "读取已保存的记忆"),
            AIFunctionFactory.Create(ListMemories, name: "list_memories", description: "列出所有记忆主题名"),
            AIFunctionFactory.Create(DeleteMemory, name: "delete_memory", description: "删除一条记忆"),
            AIFunctionFactory.Create(SendGroupMsg, name: "send_group_msg", description: "在指定QQ群发送消息，可@某人（仅主人可用，群号可省略默认当前群）"),
            AIFunctionFactory.Create(SendPrivateMsg, name: "send_private_msg", description: "给指定QQ发私聊消息（仅主人可用）"),
            AIFunctionFactory.Create(SendImage, name: "send_image", description: "把本地图片文件发送给指定QQ（私聊）或QQ群（仅主人可用）"),
            AIFunctionFactory.Create(SendGroupPoke, name: "send_poke", description: "在QQ群戳指定用户，可指定次数并发执行（任何人可用，群号可省略默认当前群）"),
            AIFunctionFactory.Create(DownloadAndSendFile, name: "download_and_send_file", description: "从 URL 下载文件并发送到群（仅主人可用，群号可省略默认当前群）"),
            AIFunctionFactory.Create(AddWatchedGroup, name: "add_watched_group", description: "把QQ群加入关注列表（仅主人可用）"),
            AIFunctionFactory.Create(RemoveWatchedGroup, name: "remove_watched_group", description: "把QQ群移出关注列表（仅主人可用）"),
            AIFunctionFactory.Create(ListWatchedGroups, name: "list_watched_groups", description: "列出已关注的QQ群（仅主人可用）"),
            AIFunctionFactory.Create(RunCommand, name: "run_command", description: "执行宿主或插件注册的命令并返回结果（仅管理员可用，如 run_command(command=\"timer\", args=\"60 123456 你好\")）"),
            AIFunctionFactory.Create(ListCommands, name: "list_commands", description: "列出全部可执行命令（名称+说明+来源插件），配合 run_command 使用"),
            AIFunctionFactory.Create(FsRead, name: "fs_read", description: "读自己文件工作区的文本文件（分页返回，约 3000 字符/页）"),
            AIFunctionFactory.Create(FsWrite, name: "fs_write", description: "把内容写入/覆盖自己文件工作区的文件（保存笔记、清单、长期数据）"),
            AIFunctionFactory.Create(FsAppend, name: "fs_append", description: "向自己文件工作区的文件末尾追加内容（写日志、增量记录）"),
            AIFunctionFactory.Create(FsList, name: "fs_list", description: "列出自己文件工作区的目录内容"),
            AIFunctionFactory.Create(FsDelete, name: "fs_delete", description: "删除自己文件工作区内的文件（不可恢复，谨慎）"),
            AIFunctionFactory.Create(FsDownload, name: "fs_download", description: "下载 URL 内容保存到自己文件工作区"),
            AIFunctionFactory.Create(FsInfo, name: "fs_info", description: "查看自己文件工作区内文件/目录的信息（大小/时间/子项）"),
            AIFunctionFactory.Create(FsSearch, name: "fs_search", description: "在自己文件工作区内用正则搜索文件内容（返回 文件:行号: 内容，支持目录递归）"),
            AIFunctionFactory.Create(FsReplace, name: "fs_replace", description: "用正则替换自己文件工作区中单文件的文本（支持 $1 分组引用；改代码局部用，避免重写全文）"),
        ];
        var all = tools.Select(t => t is AIFunction f ? new LoggingFunction(f, _memoryDir, p => LastScreenshotPath = p) : t).ToList();
        // PluginBuilder 工具（独立桥接类，命令通道跨 ALC 调用）
        var pbBridge = new PluginBuilderTools(ctx) { CurrentCallerUin = CurrentCallerUin };
        foreach (var extra in pbBridge.Create())
            all.Add(new LoggingFunction(extra, _memoryDir, _ => { }));
        return all;
    }

    // ---- 宿主/插件命令桥（跨插件协作） ----
    [Description("执行宿主或插件注册的命令并返回结果文本。command 是命令名（不含前缀），args 是参数串。仅管理员对话可用。")]
    async Task<string> RunCommand([Description("命令名，如 timer / watchs / plugin")] string command, [Description("参数串，可空")] string? args = null)
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以通过我执行命令。";
        var result = await ctx.TryInvokeCommandAsync(command, args ?? "");
        return result ?? $"未找到命令「{command}」。可先调用 list_commands 查看全部可用命令。";
    }

    [Description("列出全部可执行命令（名称 | 说明 | 来源插件）。")]
    Task<string> ListCommands()
    {
        var cmds = ctx.ListCommands();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"共 {cmds.Count} 条命令（不含前缀，配合 run_command 使用）：");
        foreach (var c in cmds)
            sb.AppendLine($"{c.Name} | {c.Description} | {c.PluginName}{(string.IsNullOrEmpty(c.Usage) ? "" : " | 用法: " + c.Usage)}");
        sb.AppendLine("plugin | 插件管理（宿主内置，仅支持 list）");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    // ---- 文件工作区（FileSystem 插件提供的沙箱，专门给 AI 用的持久化存储） ----
    // 页数限制：读大文件分页，防止单次撑爆上下文
    private const int FsPageChars = 3000;

    private string FsErr(string result) =>
        result.StartsWith("拒绝") || result.StartsWith("操作失败") || result.StartsWith("参数不足")
            ? result + "。注意：只能操作文件工作区内的相对路径。"
            : result;

    private async Task<string> FsInvoke(string cmd, string args)
    {
        var r = await ctx.TryInvokeCommandAsync(cmd, args);
        return r is null ? "文件工作区不可用（FileSystem 插件未加载）。" : r;
    }

    [Description("读文件工作区的文本文件。path 是相对路径；offset 起始字符位置（默认 0），返回约 3000 字符一页并在末尾标注是否还有下页。用于查看笔记/清单/数据文件。")]
    async Task<string> FsRead([Description("相对路径，如 notes/todo.md")] string path, [Description("起始字符位置，默认 0")] int offset = 0)
    {
        var page = Math.Max(0, offset);
        var r = await FsInvoke("fs_read", $"{path} 999999");
        if (r.StartsWith("文件不存在") || r.StartsWith("拒绝") || r.StartsWith("操作失败")) return FsErr(r);
        // 截掉工具追加的总长标注，拿纯文本再分页
        var text = r;
        var idx = text.LastIndexOf("…（共 ");
        if (idx > 0) text = text[..idx];
        if (page >= text.Length) return $"offset {page} 超出文件长度 {text.Length}。";
        var chunk = text.Substring(page, Math.Min(FsPageChars, text.Length - page));
        var more = page + chunk.Length < text.Length ? $"\n[还有下页：下一页 offset={page + chunk.Length}，总长 {text.Length}]" : $"\n[已到末尾，总长 {text.Length}]";
        return chunk + more;
    }

    [Description("把文本写入/覆盖文件工作区的文件（UTF-8）。适合保存笔记、清单、长期任务数据、用户委托保管的资料。")]
    async Task<string> FsWrite([Description("相对路径，如 notes/xxx.md")] string path, [Description("完整文件内容")] string content) =>
        FsErr(await FsInvoke("fs_write", $"{path} {content.Replace("\n", "\\n")}"));

    [Description("向文件工作区的文件末尾追加一行/一段内容（不覆盖原有内容）。适合写日志、增量记录。")]
    async Task<string> FsAppend([Description("相对路径")] string path, [Description("追加的内容")] string content) =>
        FsErr(await FsInvoke("fs_append", $"{path} {content.Replace("\n", "\\n")}"));

    [Description("列出文件工作区的目录内容（文件名+大小+修改时间）。")]
    async Task<string> FsList([Description("相对目录路径，空 = 根目录")] string path = "") =>
        await FsInvoke("fs_list", path);

    [Description("删除文件工作区内的文件（目录须为空才能删）。谨慎使用，删除不可恢复。")]
    async Task<string> FsDelete([Description("要删除的文件相对路径")] string path) =>
        FsErr(await FsInvoke("fs_delete", path));

    [Description("下载网页/文件到文件工作区。url 是 http(s) 链接，path 是保存的相对路径。")]
    async Task<string> FsDownload([Description("下载地址")] string url, [Description("保存的相对路径")] string path) =>
        FsErr(await FsInvoke("fs_download", $"{url} {path}"));

    [Description("查看文件/目录信息（大小、时间、子项数）。")]
    async Task<string> FsInfo([Description("相对路径")] string path) =>
        FsErr(await FsInvoke("fs_info", path));

    [Description("用正则在文件工作区搜索：单文件或目录递搜（.cs/.json/.md/.txt），返回 文件:行号: 内容。改代码前先搜定位目标。")]
    async Task<string> FsSearch([Description("文件或目录的相对路径")] string path, [Description("正则表达式")] string pattern, [Description("最大返回行数，默认 50")] int limit = 50) =>
        FsErr(await FsInvoke("fs_search", $"{path} {pattern} {limit}"));

    [Description("用正则替换单文件文本：pattern 命中处换成 replacement（支持 $1 等分组引用，\\n 表示换行）。局部修改代码用它，别整文件重写。")]
    async Task<string> FsReplace([Description("单文件相对路径")] string path, [Description("正则表达式")] string pattern, [Description("替换串，支持 $1 与 \\n")] string replacement) =>
        FsErr(await FsInvoke("fs_replace", $"{path} {pattern} {replacement.Replace(" ", "\\s")}"));

    // ---- 联网搜索 ----
    [Description("联网搜索，返回多条搜索结果（标题+摘要+链接）。必须提供 query 搜索关键词；engine 参数可指定搜索引擎：baidu（默认，中文好）、bing（直连快）、duckduckgo（隐私好），可省略。")]
    async Task<string> SearchWeb([Description("搜索关键词（必填，例如：2026年奥运会举办地）")] string query, [Description("搜索引擎：baidu(默认)/bing/duckduckgo，可省略")] string? engine = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "搜索失败：缺少搜索关键词（query 参数为空）。请根据用户消息提炼出明确的关键词后重新调用 search_web，例如 search_web(query=\"具体的关键词\")。";
        if (SearchCount >= _cfg.SearchPerTurnLimit)
            return $"本轮对话已搜索 {_cfg.SearchPerTurnLimit} 次，搜索频率受限。请基于已有信息回答，或建议用户换个问法。";
        SearchCount++;
        var e = engine?.Trim().ToLowerInvariant();
        try
        {
            return e switch
            {
                "bing" => await SearchBing(query),
                "duckduckgo" or "ddg" => await SearchDdgChain(query),
                // 默认百度（走本地代理，反爬时自动降级 Bing）
                _ => await SearchBaiduOrFallback(query)
            };
        }
        catch (Exception ex) { return $"搜索失败: {ex.Message}"; }
    }

    // 百度优先，失败自动降级 Bing
    async Task<string> SearchBaiduOrFallback(string query)
    {
        try { return await SearchBaidu(query); }
        catch (Exception baiduEx)
        {
            try { return await SearchBing(query); }
            catch (Exception bingEx) { return $"搜索失败: 百度({baiduEx.Message}); Bing({bingEx.Message})"; }
        }
    }

    // 百度搜索（默认）：走本地代理，解析自然结果
    async Task<string> SearchBaidu(string query)
    {
        var http = HttpProxy;
        http.DefaultRequestHeaders.Referrer = new Uri("https://www.baidu.com/");
        var url = "https://www.baidu.com/s?wd=" + Uri.EscapeDataString(query) + "&rn=10";
        var html = await http.GetStringAsync(url);
        // 反爬/空页检测：页面过短或没有 h3 标题
        if (html.Length < 20000 || (!html.Contains("<h3") && !html.Contains("c-container")))
            throw new Exception("百度返回安全验证页或空页");

        var results = new List<string>();
        // 解析每个 h3 标题块（含链接），摘要取标题后同块内文本
        var blockPattern = new Regex(@"<h3[^>]*>\s*<a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>\s*</h3>([\s\S]{0,600}?)(?=<h3|</div>|$)", RegexOptions.IgnoreCase);
        foreach (Match m in blockPattern.Matches(html))
        {
            var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            var title = StripTags(m.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(title)) continue;
            // 过滤百度推广广告块
            if (href.Contains("baidu.com/link?url=") && m.Value.Contains("ad")) continue;
            var tail = StripTags(m.Groups[3].Value);
            var snip = tail.Length > 200 ? tail[..200] : tail;
            results.Add($"{title}\n  {snip}\n  {href}");
            if (results.Count >= 5) break;
        }
        // 降级：宽松再扫一次 h3（无摘要）
        if (results.Count == 0)
        {
            var loosePattern = new Regex(@"<h3[^>]*>\s*<a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.IgnoreCase);
            foreach (Match m in loosePattern.Matches(html))
            {
                var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                var title = StripTags(m.Groups[2].Value);
                if (string.IsNullOrWhiteSpace(title)) continue;
                results.Add($"{title}\n  {href}");
                if (results.Count >= 5) break;
            }
        }
        return results.Count > 0 ? string.Join("\n\n", results) : "没有搜索结果（可能被百度拦截）。";
    }

    async Task<string> SearchDdgChain(string query)
    {
        try { return await SearchDdgLite(query); }
        catch (Exception liteEx)
        {
            try { return await SearchDuckDuckGo(query); }
            catch (Exception htmlEx) { return $"DuckDuckGo 搜索失败: Lite: {liteEx.Message}; html: {htmlEx.Message}"; }
        }
    }

    // 复用的 HttpClient（避免每个搜索请求都重建 TCP 连接，显著提速）
    private static readonly HttpClient HttpDirect = BuildHttp(useProxy: false);
    private static readonly HttpClient HttpProxy = BuildHttp(useProxy: true);

    static HttpClient BuildHttp(bool useProxy = false)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        if (useProxy)
        {
            // 本地代理（系统代理 7897），用于访问被反爬的站点
            handler.Proxy = new System.Net.WebProxy("http://127.0.0.1:7897");
            handler.UseProxy = true;
        }
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return http;
    }

    async Task<string> SearchDdgLite(string query)
    {
        var http = HttpDirect;
        http.DefaultRequestHeaders.Referrer = new Uri("https://duckduckgo.com/");
        var url = "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query);
        var html = await http.GetStringAsync(url);
        if (html.Contains("anomaly") || html.Contains("challenge-form")) throw new Exception("DuckDuckGo 要求人机验证");
        var links = Regex.Matches(html, @"<a rel=""nofollow"" href=""([^""]+)"">(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var snippets = Regex.Matches(html, @"<td class=['""]result-snippet['""]>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var results = new List<string>();
        for (int i = 0; i < links.Count && results.Count < 5; i++)
        {
            var href = System.Net.WebUtility.HtmlDecode(links[i].Groups[1].Value);
            if (!href.StartsWith("http")) continue;
            var snip = i < snippets.Count ? StripTags(snippets[i].Groups[1].Value) : "";
            results.Add($"{StripTags(links[i].Groups[2].Value)}\n  {snip}\n  {href}");
        }
        if (results.Count == 0) throw new Exception("DuckDuckGo 未返回结果");
        return string.Join("\n\n", results);
    }

    async Task<string> SearchBing(string query)
    {
        var http = HttpDirect;
        var url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query);
        var html = await http.GetStringAsync(url);
        var results = new List<string>();
        foreach (Match b in Regex.Matches(html, @"<li class=""b_algo""[\s\S]*?</li>", RegexOptions.IgnoreCase))
        {
            var link = Regex.Match(b.Value, @"<a[^>]+href=""([^""]+)""", RegexOptions.IgnoreCase);
            var title = Regex.Match(b.Value, @"<a[^>]+>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippet = Regex.Match(b.Value, @"<p[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase);
            var href = System.Net.WebUtility.HtmlDecode(link.Groups[1].Value);
            if (!href.StartsWith("http") || href.Contains("bing.com/search")) continue;
            results.Add($"{StripTags(title.Groups[1].Value)}\n  {StripTags(snippet.Groups[1].Value)}\n  {href}");
            if (results.Count >= 5) break;
        }
        return results.Count > 0 ? string.Join("\n\n", results) : "没有搜索结果。";
    }

    async Task<string> SearchDuckDuckGo(string query)
    {
        var http = HttpDirect;
        http.DefaultRequestHeaders.Referrer = new Uri("https://duckduckgo.com/");
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        var html = await http.GetStringAsync(url);
        if (html.Contains("anomaly") || html.Contains("challenge-form")) throw new Exception("DuckDuckGo 要求人机验证");
        var results = new List<string>();
        foreach (Match m in Regex.Matches(html,
            @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)"">(.*?)</a>\s*<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.Singleline))
        {
            var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            var uddg = Regex.Match(href, @"uddg=([^&]+)");
            var link = uddg.Success ? Uri.UnescapeDataString(uddg.Groups[1].Value) : href;
            results.Add($"{StripTags(m.Groups[2].Value)}\n  {StripTags(m.Groups[3].Value)}\n  {link}");
            if (results.Count >= 5) break;
        }
        return results.Count > 0 ? string.Join("\n\n", results) : "没有搜索结果。";
    }

    // ---- 实时天气 ----
    [Description("查询指定城市的实时天气，返回温度、体感温度、天气状况、湿度、风速。")]
    async Task<string> GetWeather([Description("城市名，如 北京、上海、杭州")] string city)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            var url = "https://wttr.in/" + Uri.EscapeDataString(city) + "?format=j1";
            var json = await http.GetStringAsync(url);
            var root = JsonNode.Parse(json);
            var cc = root?["current_condition"]?[0];
            if (cc is null) return $"没查到 {city} 的天气。";
            var desc = cc["weatherDesc"]?[0]?["value"]?.GetValue<string>() ?? "未知";
            var temp = cc["temp_C"]?.GetValue<string>() ?? "?";
            var feels = cc["FeelsLikeC"]?.GetValue<string>() ?? "?";
            var hum = cc["humidity"]?.GetValue<string>() ?? "?";
            var wind = cc["windspeedKmph"]?.GetValue<string>() ?? "?";
            return $"{city} 当前天气：{desc}，气温 {temp}°C（体感 {feels}°C），湿度 {hum}%，风速 {wind} km/h。";
        }
        catch (Exception ex) { return $"天气查询失败: {ex.Message}"; }
    }

    // ---- 网页爬取 ----
    [Description("抓取一个网页并提取正文文本（最多 2000 字符）。")]
    async Task<string> FetchUrl([Description("要抓取的网页完整 URL，需带 http(s)://")] string url)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            http.Timeout = TimeSpan.FromSeconds(25);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            var html = await http.GetStringAsync(url);
            html = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", " ");
            var text = System.Net.WebUtility.HtmlDecode(html);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 2000 ? text[..2000] + "…" : text;
        }
        catch (Exception ex) { return $"抓取失败: {ex.Message}"; }
    }

    // ---- 通用 ----
    [Description("获取当前对话所在的 QQ 群号。在群里对话返回群号，私聊返回 0。")]
    long GetCurrentGroup() => CurrentGroupId;

    [Description("获取当前日期和时间（本地时间，含星期）。")]
    string GetCurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd");

    [Description("获取当前群的所有成员列表（每行：QQ号 + 昵称/群名片），用于查找某人。")]
    async Task<string> GetGroupMembers()
    {
        if (CurrentGroupId <= 0) return "当前不在群聊中。";
        var r = await _api.GetGroupMemberListAsync(CurrentGroupId);
        if (!r.Success || r.Data is null) return "获取失败: " + r.ErrorMessage;
        return string.Join("\n", r.Data.Take(200).Select(x => $"{x.UserId} {x.CardOrNickname}"));
    }

    [Description("在当前群按昵称/群名片查找成员（不区分大小写），返回匹配的 QQ 号与名字。")]
    async Task<string> FindMemberByName([Description("要查找的名字，不区分大小写")] string name)
    {
        if (CurrentGroupId <= 0) return "当前不在群聊中。";
        var r = await _api.GetGroupMemberListAsync(CurrentGroupId);
        if (!r.Success || r.Data is null) return "获取失败: " + r.ErrorMessage;
        var matches = r.Data
            .Where(x => x.CardOrNickname.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                        x.Nickname.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();
        if (matches.Count == 0) return $"没找到名字包含「{name}」的成员。";
        return string.Join("\n", matches.Select(x => $"{x.UserId} {x.CardOrNickname}"));
    }

    // ---- 记忆 ----
    // 记忆按当前对话范围隔离存储：群里按 group_<群号>、私聊按 user_<QQ>，互不混用
    private string ScopeDir()
    {
        var scope = CurrentGroupId > 0 ? $"group_{CurrentGroupId}" : CurrentCallerUin > 0 ? $"user_{CurrentCallerUin}" : "general";
        return Path.Combine(_memoryDir, scope);
    }

    [Description("保存一条长期记忆到本地（Markdown 格式，按当前群/用户隔离存储）。当用户提供需要长期记住的信息、偏好、事实、称呼、待办等时主动调用。")]
    string SaveMemory([Description("记忆主题名（建议英文/数字/短词，作为文件名）")] string name, [Description("记忆内容，Markdown 格式")] string content)
    {
        try
        {
            var dir = ScopeDir();
            Directory.CreateDirectory(dir);
            var safe = SanitizeName(name);
            if (string.IsNullOrEmpty(safe)) return "记忆名称无效。";
            var path = Path.Combine(dir, safe + ".md");
            File.WriteAllText(path, content);
            return $"已保存记忆「{safe}」。";
        }
        catch (Exception ex) { return "保存失败: " + ex.Message; }
    }

    [Description("读取一条已保存的记忆内容（按当前群/用户隔离）。当用户问起之前记住的事情时调用。")]
    string ReadMemory([Description("记忆主题名")] string name)
    {
        try
        {
            var dir = ScopeDir();
            var safe = SanitizeName(name);
            var path = Path.Combine(dir, safe + ".md");
            if (!File.Exists(path)) return $"没有找到记忆「{safe}」。可先调用 list_memories 查看已有记忆。";
            return File.ReadAllText(path);
        }
        catch (Exception ex) { return "读取失败: " + ex.Message; }
    }

    [Description("列出当前范围（当前群/用户）下所有已保存的记忆主题名。")]
    string ListMemories()
    {
        try
        {
            var dir = ScopeDir();
            if (!Directory.Exists(dir)) return "暂无记忆。";
            var files = Directory.GetFiles(dir, "*.md").Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n)).ToArray();
            return files.Length == 0 ? "暂无记忆。" : string.Join(", ", files);
        }
        catch (Exception ex) { return "读取失败: " + ex.Message; }
    }

    [Description("删除当前范围（当前群/用户）下的一条记忆。")]
    string DeleteMemory([Description("记忆主题名")] string name)
    {
        try
        {
            var dir = ScopeDir();
            var safe = SanitizeName(name);
            var path = Path.Combine(dir, safe + ".md");
            if (!File.Exists(path)) return $"没有找到记忆「{safe}」。";
            File.Delete(path);
            return $"已删除记忆「{safe}」。";
        }
        catch (Exception ex) { return "删除失败: " + ex.Message; }
    }

    // ---- OneBot API（仅管理员） ----
    [Description("在指定 QQ 群发送一条消息，可 @ 某人。group_id 可省略，默认发送到当前所在群。")]
    async Task<string> SendGroupMsg([Description("目标群号，可省略，默认当前群")] long? groupId, [Description("消息内容")] string message, [Description("要 @ 的用户QQ号，可省略")] long? atUserId = null)
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以让我发群消息。";
        var gid = groupId ?? CurrentGroupId;
        if (gid <= 0) return "不知道目标群号，请明确告诉我群号。";
        object content = message;
        if (atUserId.HasValue && atUserId > 0)
        {
            content = new List<OneBotLib.MessageSegment.MessageSegment>
            {
                OneBotLib.MessageSegment.MessageSegment.At(atUserId.Value),
                OneBotLib.MessageSegment.MessageSegment.Text(" " + message)
            };
        }
        var r = await _api.SendGroupMsgAsync(gid, content);
        return r.Success ? $"已发送到群 {gid}，message_id={r.Data}" : $"发送失败: {r.ErrorMessage}";
    }

    [Description("给指定 QQ 发送私聊消息。")]
    async Task<string> SendPrivateMsg([Description("目标 QQ 号")] long userId, [Description("消息内容")] string message)
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以让我发私聊消息。";
        var r = await _api.SendPrivateMsgAsync(userId, message);
        return r.Success ? $"已发送私聊消息，message_id={r.Data}" : $"发送失败: {r.ErrorMessage}";
    }

    [Description("把本地图片文件发送给指定 QQ（私聊）或 QQ 群。图片路径是绝对路径，例如 D:\\xx\\screenshot.png。")]
    async Task<string> SendImage([Description("本地图片文件绝对路径")] string path, [Description("目标 QQ 号（私聊）或群号（群聊）")] long targetId, [Description("true=发送到群，false=发送私聊，可省略默认私聊")] bool toGroup = false)
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以让我发图片。";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return $"图片不存在: {path}";
        try
        {
            var segments = new List<OneBotLib.MessageSegment.MessageSegment> { OneBotLib.MessageSegment.MessageSegment.Image(path) };
            if (toGroup)
            {
                var r = await _api.SendGroupMsgAsync(targetId, segments);
                return r.Success ? $"已发送图片到群 {targetId}，message_id={r.Data}" : $"发送失败: {r.ErrorMessage}";
            }
            var rp = await _api.SendPrivateMsgAsync(targetId, segments);
            return rp.Success ? $"已发送图片私聊给 {targetId}，message_id={rp.Data}" : $"发送失败: {rp.ErrorMessage}";
        }
        catch (Exception ex) { return "发送图片失败: " + ex.Message; }
    }

    [Description("在 QQ 群戳指定用户（poke），可指定次数并一次并发发出，无间隔。group_id 可省略，默认戳当前所在群的人。任何人可用，可戳任何人。")]
    async Task<string> SendGroupPoke([Description("目标群号，可省略，默认当前群")] long? groupId, [Description("要戳的用户 QQ 号")] long userId, [Description("戳的次数，可省略，默认 1，最大 100")] int? count = 1)
    {
        var gid = groupId ?? CurrentGroupId;
        if (gid <= 0) return "不知道目标群号，请明确告诉我群号。";
        var n = Math.Clamp(count ?? 1, 1, 100);
        var tasks = Enumerable.Range(0, n).Select(_ => _api.GroupPokeAsync(gid, userId)).ToArray();
        await Task.WhenAll(tasks);
        var failed = tasks.Where(t => !t.Result.Success).ToList();
        return failed.Count == 0
            ? $"已在群 {gid} 并发戳了 {userId} {n} 下"
            : $"{n - failed.Count} 下成功，{failed.Count} 下失败: {failed[0].Result.ErrorMessage}";
    }

    [Description("从 URL 下载一个文件并发送到群（大小限制 100MB）。")]
    async Task<string> DownloadAndSendFile([Description("文件下载链接，需带 http(s)://")] string url, [Description("发送到的群号，可省略默认当前群")] long? groupId = null, [Description("文件名，可省略")] string? filename = null)
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以让我下载并发送文件。";
        var gid = groupId ?? CurrentGroupId;
        if (gid <= 0) return "不知道目标群号，请明确告诉我群号。";

        const long MaxBytes = 100L * 1024 * 1024; // 100MB

        try
        {
            var name = SafeFilename(filename, url);
            var local = Path.Combine(Path.GetTempPath(), name);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(120);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return $"下载失败: HTTP {(int)resp.StatusCode}";

            var declared = resp.Content.Headers.ContentLength ?? -1;
            if (declared > MaxBytes) return $"文件过大（{declared / 1048576.0:F1}MB），超过 100MB 限制。";

            long written = 0;
            var buffer = new byte[81920];
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(local, FileMode.Create, FileAccess.Write);
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                written += read;
                if (written > MaxBytes)
                {
                    await dst.DisposeAsync();
                    File.Delete(local);
                    return "下载中止：文件超过 100MB 限制。";
                }
                await dst.WriteAsync(buffer.AsMemory(0, read));
            }
            await dst.FlushAsync();

            var segments = new List<OneBotLib.MessageSegment.MessageSegment>
            {
                OneBotLib.MessageSegment.MessageSegment.File(local, name)
            };
            var r = await _api.SendGroupMsgAsync(gid, segments);
            return r.Success ? $"已下载并发送到群 {gid}（{name}，{written} 字节）" : $"发送失败: {r.ErrorMessage}";
        }
        catch (Exception ex) { return "下载/发送失败: " + ex.Message; }
    }

    private static string SafeFilename(string? filename, string url)
    {
        var name = filename ?? "";
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            name = Path.GetFileName(u.AbsolutePath);
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) name = "download_" + DateTime.Now.Ticks + ".bin";
        return Regex.Replace(name, @"[^\w.\-]", "_");
    }

    private static string StripTags(string s) =>
        Regex.Replace(System.Net.WebUtility.HtmlDecode(s), @"<[^>]+>", " ").Trim();

    private static string SanitizeName(string name) =>
        Regex.Replace(name.Trim(), @"[^a-zA-Z0-9_-]", "_");

    // ---- 关注群管理（主人专属，与 Admin 插件共享 watched_groups.txt） ----
    [Description("把指定 QQ 群加入关注列表，之后机器人会收集并选择性回复该群消息（仅主人可用）。")]
    public string AddWatchedGroup([Description("群号")] long groupId)
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以添加关注群。";
        if (groupId <= 0) return "群号无效。";
        var list = LoadWatched().ToHashSet();
        if (list.Add(groupId))
        {
            SaveWatched(list);
            return $"已关注群 {groupId}。";
        }
        return $"群 {groupId} 已在关注列表中。";
    }

    [Description("把指定 QQ 群移出关注列表（仅主人可用）。")]
    public string RemoveWatchedGroup([Description("群号")] long groupId)
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以移除关注群。";
        var list = LoadWatched().ToHashSet();
        if (list.Remove(groupId))
        {
            SaveWatched(list);
            return $"已取消关注群 {groupId}。";
        }
        return $"群 {groupId} 不在关注列表中。";
    }

    [Description("列出当前关注的 QQ 群列表（仅主人可用）。")]
    public string ListWatchedGroups()
    {
        if (!IsOwner) return "无权限：只有机器人管理员可以查看关注列表。";
        var list = LoadWatched();
        return list.Count == 0 ? "尚未关注任何群。" : "关注群: " + string.Join(", ", list);
    }

    public bool IsWatched(long groupId) => LoadWatched().Contains(groupId);

    private List<long> LoadWatched()
    {
        try
        {
            var path = Path.Combine(_memoryDir, "watched_groups.txt");
            if (!File.Exists(path)) return new List<long>();
            return File.ReadAllLines(path)
                .Select(l => long.TryParse(l.Trim(), out var v) ? v : 0)
                .Where(v => v > 0)
                .Distinct()
                .ToList();
        }
        catch { return new List<long>(); }
    }

    private void SaveWatched(IEnumerable<long> list)
    {
        try
        {
            Directory.CreateDirectory(_memoryDir);
            File.WriteAllLines(Path.Combine(_memoryDir, "watched_groups.txt"), list.Select(x => x.ToString()));
        }
        catch (Exception ex) { Console.WriteLine($"[watch] save failed: {ex.Message}"); }
    }
}

// 包装 AIFunction：每次工具调用前后在控制台打印参数和结果；
// MCP 工具返回的图片（ImageContent）自动解码保存为本地文件，避免 base64 撑爆 AI 上下文
class LoggingFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly string? _saveDir;
    private readonly Action<string>? _onImageSaved;
    public LoggingFunction(AIFunction inner, string? saveDir = null, Action<string>? onImageSaved = null)
    {
        _inner = inner;
        _saveDir = saveDir;
        _onImageSaved = onImageSaved;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override System.Text.Json.JsonElement JsonSchema => _inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override MethodInfo UnderlyingMethod => _inner.UnderlyingMethod;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string ArgsStr()
        {
            if (arguments.Count == 0) return "(无参数)";
            var parts = arguments.Select(kv => $"{kv.Key}={kv.Value}");
            var s = string.Join(" ", parts);
            return s.Length > 300 ? s[..300] + "…" : s;
        }

        Console.WriteLine($"[工具调用] {_inner.Name}({ArgsStr()})");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        object? result;
        try
        {
            result = await _inner.InvokeAsync(arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            // 参数缺失/格式错误：返回友好提示给模型（而非抛异常导致 HTTP 400 崩掉整轮）
            if (ex.Message.Contains("arguments dictionary", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("missing a value", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("required parameter", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("无法转换", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[工具异常] {_inner.Name} 参数错误: {ex.Message}");
                return $"工具 {_inner.Name} 调用参数错误：{ex.Message}。请检查参数是否完整、类型是否正确后重试。";
            }
            Console.WriteLine($"[工具异常] {_inner.Name} 失败: {ex.Message}");
            throw;
        }
        sw.Stop();
        result = ConvertMcpImageResult(result);
        var text = result?.ToString() ?? "(null)";
        if (text.Length > 500) text = text[..500] + "…";
        Console.WriteLine($"[工具结果] {_inner.Name} => {text} ({sw.ElapsedMilliseconds}ms)");
        return result;
    }

    // MCP 工具（如 computer_take_screenshot）的图片结果可能以多种形态返回：
    // 1) 单个 DataContent —— MCP 层把单图片响应直接包装（最常见）
    // 2) IEnumerable<AIContent> —— MCP 层对多 content 响应（如图片+OCR文本）返回的列表
    // 这里统一把图片解码保存为文件，只把文件路径/文本返回给 AI，避免 base64 撑爆 AI 上下文。
    private object? ConvertMcpImageResult(object? result)
    {
        // 形态 1：单个 DataContent
        if (result is DataContent dc && dc.Data is { Length: > 0 })
            return SaveImageToFile(dc.Data);

        // 形态 2：IEnumerable<AIContent>（MCP 多 content 响应）
        if (result is IEnumerable<AIContent> items)
        {
            var parts = new List<string>();
            foreach (var item in items)
            {
                switch (item)
                {
                    case DataContent d when d.Data is { Length: > 0 }:
                        parts.Add(SaveImageToFile(d.Data));
                        break;
                    case TextContent t when !string.IsNullOrWhiteSpace(t.Text):
                        parts.Add(t.Text);
                        break;
                    default:
                        if (item?.ToString() is { Length: > 0 } s) parts.Add(s);
                        break;
                }
            }
            return string.Join("\n", parts);
        }
        return result;
    }

    private string SaveImageToFile(ReadOnlyMemory<byte> data)
    {
        try
        {
            var dir = Path.Combine(_saveDir ?? Path.GetTempPath(), "screenshots");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            File.WriteAllBytes(path, data.ToArray());
            _onImageSaved?.Invoke(path);
            return $"图片已保存: {path}";
        }
        catch (Exception ex) { return $"保存截图失败: {ex.Message}"; }
    }
}
