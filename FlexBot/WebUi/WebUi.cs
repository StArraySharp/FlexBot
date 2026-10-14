using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlexBot.WebUi;

// WebUI：本机 HTTP 服务，同一页面供浏览器与 Photino 窗口使用。
// 静态资源放在 <exe旁>/WebUi/（由 csproj 的 Content 拷贝）——改完刷新浏览器即生效，无需重建/重启。
// API：GET /api/status | /api/logs?after= | /api/plugins | /api/config | /api/instructions
//      POST /api/plugins/{name}/{action} | /api/command | /api/logs/clear | /api/config | /api/instructions
static class WebUi
{
    // 不允许的常见端口
    // 前端模块清单：按序拼接为 /app.css 与 /app.js；亦可单独访问各文件（/js/xxx.js、/css/xxx.css）
    internal static readonly string[] UiAssets =
    [
        // JS 顺序敏感：nav（页面切换/工具）→ 日志/状态/插件/配置 → 外观 → 认证 → 快捷命令 → main（轮询入口）
        "js/nav.js", "js/logs.js", "js/status.js", "js/plugins.js",
        "js/config.js", "js/history.js", "js/appearance.js", "js/auth.js", "js/main.js",
        // CSS 顺序：变量/基础 → 侧栏 → 通用组件 → 页面 → 移动端
        "css/base.css", "css/nav.css", "css/components.css", "css/pages.css", "css/mobile.css",
    ];

    private static readonly int[] ReservedPorts = [
        80, 443, 3000, 3001, 5000, 5173, 5174, 8000, 8001, 8008, 8080, 8443, 9000,
        22, 23, 25, 53, 110, 143, 465, 587, 993, 995, 3306, 5432, 6379
    ];

    // 登录失败退避表：来源 IP → (连续失败次数, 解禁时刻)。内存态，重启清零。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, DateTime Until)> LoginFails = new();
    // 登录会话 token（认证开启时所有 /api/* 与页面均需携带）。
    // **持久化**：早期实现每次重启都轮换 ⇒ 一部署就把已登录的浏览器踢回登录页，
    // 表现为"页面突然空了/要求重新登录"。现在优先用配置里的令牌，缺失才生成并保存。
    private static string SessionToken = "";

    /// <summary>是否曾有页面（/ 或 /login）被请求过：窗口模式用来判断 WebView 是否真的导航成功</summary>
    public static volatile bool HasServedPage;

    public static string Start(PluginManager plugins, HostSettings settings, IBotTransport client, ProcessManager procs, Action? requestShutdown = null)
    {
        var port = GetPort(settings.WebUiPort);
        // WebUI 固定绑定全部网卡（0.0.0.0，公网可达），对外暴露请设置 WebUiPassword
        const string bindAddr = "0.0.0.0";
        var authOn = !string.IsNullOrWhiteSpace(settings.WebUiPassword);
        // 兼容手写明文：非 64 位十六进制一律视为明文，启动时自动转 SHA256 存储（磁盘不落明文）
        if (authOn && !IsSha256Hex(settings.WebUiPassword))
        {
            settings.WebUiPassword = Sha256Hex(settings.WebUiPassword);
            settings.Save();
            Console.WriteLine("[webui] WebUiPassword 已由明文自动转换为 SHA256 存储");
        }
        // 会话令牌：来自配置则直接沿用（重启不再登出），否则生成一次并落盘（加密存储）
        if (string.IsNullOrWhiteSpace(settings.WebUiSessionToken))
        {
            settings.WebUiSessionToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
            settings.Save();
            Console.WriteLine("[webui] 已生成并保存会话令牌：此后重启保留登录状态");
        }
        SessionToken = settings.WebUiSessionToken;
        // 首次启动（或密码被清空后）自动生成随机密码：避免"无密码 + 全网卡监听"的裸奔窗口。
        // 明文只写真实控制台，不走 Console.Out（那个被 TimestampWriter 接到日志文件与环形缓冲了）。
        if (!authOn)
        {
            var plain = GeneratePassword(16);
            settings.WebUiPassword = Sha256Hex(plain);
            try { settings.Save(); }
            catch (Exception ex) { Console.WriteLine($"[webui] 随机密码写入 config.json 失败: {ex.Message}"); }
            WriteConsoleOnly($"\n[webui] 已生成随机登录密码：{plain}\n"
                           + "        明文只在控制台打印这一次（已以 SHA256 存入 config.json）。\n"
                           + "        要改成自己的密码：WebUI → 设置 → 端口与访问控制 → 登录密码\n\n");
            Console.WriteLine("[webui] 未设置密码，已自动生成随机密码并保存（明文仅打印在控制台，未写入日志）");
            authOn = true;   // 本次运行即启用认证
        }
        // 日志里给人看的地址用本机回环（0.0.0.0 不是能直接打开的地址）；实际监听仍是全网卡
    Console.WriteLine($"[webui] 启动 WebUI 在 http://127.0.0.1:{port}（本机；实际监听 0.0.0.0，局域网可访问）{(authOn ? "，已开启登录认证" : "，无密码（建议设置 WebUiPassword）")}");
    CheckAssets();   // 校验静态资源清单与磁盘是否一致
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(plugins);
        builder.Services.AddResponseCompression(o => { o.EnableForHttps = true; o.MimeTypes = ["text/html", "text/css", "application/javascript", "application/json", "image/svg+xml"]; });
        builder.Logging.ClearProviders(); // 不让 ASP.NET 日志污染控制台
        builder.WebHost.UseUrls($"http://{bindAddr}:{port}");
        var app = builder.Build();
        app.UseResponseCompression();   // 前端资源与 API 响应压缩（文本类，不影响 ETag/304 逻辑）

        // 全局异常日志（常驻：500 时不至于无线索）
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[webui][EX] {ctx.Request.Path}: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        });


        // ---- 认证中间件：密码开启时，页面凭 cookie、API 凭 cookie 或 Bearer ----
        app.Use(async (ctx, next) =>
        {
            if (!authOn) { await next(); return; }
            var path = ctx.Request.Path;

            // 白名单：登录页、认证接口、静态资源（css/js 是纯样式与脚本，无敏感数据；登录页需要它们渲染）
            // /api/theme-cfg + /background：外观设置（背景图/透明度）对所有访问者（含登录页）生效，无敏感数据
            var pStr = path.Value ?? "";
            // 页面请求留痕：黑屏/白屏这类问题要能分辨"页面根本没被请求"还是"请求到了但渲染不出来"
            if (pStr is "/" or "/login")
            {
                HasServedPage = true;
                Console.WriteLine($"[webui] {ctx.Request.Method} {pStr} ← {ctx.Connection.RemoteIpAddress} ua={(ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(60, ua.Length)] : "-")}");
            }

            if (pStr == "/login" || pStr == "/app.css" || pStr == "/app.js"
                || pStr.StartsWith("/css/") || pStr.StartsWith("/js/")
                || pStr.StartsWith("/api/auth")
                || pStr == "/api/theme-cfg"
                || pStr == "/background"
                || pStr == "/healthz") { await next(); return; }   // 探活：部署/监控用，只暴露存活与连接状态
            var ok = false;
            if (ctx.Request.Headers.TryGetValue("Authorization", out var auth) && auth.ToString() == $"Bearer {SessionToken}") ok = true;
            else if (ctx.Request.Cookies.TryGetValue("session_token", out var tok) && tok == SessionToken) ok = true;
            if (ok) { await next(); return; }
            if (path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
            ctx.Response.Redirect("/login");
        });

        // 首页与静态资源：每次请求都从磁盘读（<exe旁>/WebUi/），改完 CSS/JS 刷新即生效，不用重启
        app.MapGet("/", (HttpContext ctx) => RenderHtmlResult(ctx, "index.html"));

        // ---- 登录页与认证接口 ----
        app.MapGet("/login", (HttpContext ctx) => RenderHtmlResult(ctx, "login.html"));
        app.MapPost("/api/auth/login", (LoginPayload p, HttpContext ctx) =>
        {
            // 连续失败退避：前 2 次立即回，之后 2^(n-2) 秒（上限 5 分钟），压住暴力破解
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (LoginFails.TryGetValue(ip, out var st) && st.Until > DateTime.UtcNow)
            {
                var wait = (int)Math.Ceiling((st.Until - DateTime.UtcNow).TotalSeconds);
                Console.WriteLine($"[webui] 登录被退避：{ip} 连续失败 {st.Count} 次，还需 {wait}s");
                return Results.Json(new { ok = false, error = $"失败次数过多，请 {wait} 秒后再试" });
            }
            if (string.IsNullOrWhiteSpace(p.Password) || !FixedTimeEquals(Sha256Hex(p.Password), settings.WebUiPassword))
            {
                var n = LoginFails.TryGetValue(ip, out var prev) ? prev.Count + 1 : 1;
                var delay = n >= 3 ? TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(n - 2, 9)))) : TimeSpan.Zero;
                LoginFails[ip] = (n, DateTime.UtcNow + delay);
                Console.WriteLine($"[webui] 登录失败：密码错误（{ip} 第 {n} 次{(delay > TimeSpan.Zero ? $"，退避 {delay.TotalSeconds:F0}s" : "")}）");
                return Results.Json(new { ok = false, error = "密码错误" });
            }
            LoginFails.TryRemove(ip, out _);
            Console.WriteLine($"[webui] 登录成功（{ip}）");
            var cookieOpt = new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" };
            if (p.Remember == true) cookieOpt.MaxAge = TimeSpan.FromDays(30);
            ctx.Response.Cookies.Append("session_token", SessionToken, cookieOpt);
            return Results.Json(new { ok = true });
        });
        app.MapPost("/api/auth/logout", (HttpResponse resp) =>
        {
            resp.Cookies.Delete("session_token");
            return Results.Json(new { ok = true });
        });
        app.MapGet("/api/auth/status", () => Results.Json(new { authRequired = authOn }));

        // 静态资源：js/ 与 css/ 目录内的模块文件（内嵌资源，一键全量注册）
        foreach (var f in UiAssets)
        {
            var file = f;
            app.MapGet("/" + file, () =>
            {
                var ct = file.EndsWith(".js") ? "application/javascript; charset=utf-8" : "text/css; charset=utf-8";
                return Results.Text(LoadResource(file), ct);
            });
        }
        // 注意：这里必须用"同步返回 IResult"的 lambda。若写成 async lambda（返回 Task<IResult>），
        // 它同时可转成 RequestDelegate(Func<HttpContext,Task>)，MapGet 会选中那个重载并**丢弃 IResult**，
        // 表现为 200 + 空响应体（handler 照常执行，只是没人把结果写出去）。
        app.MapGet("/app.js", (HttpContext ctx) => ServeBundle(
            ctx, "application/javascript; charset=utf-8",
            string.Join("\n", UiAssets.Where(f => f.StartsWith("js/")).Select(LoadResource))));
        app.MapGet("/app.css", (HttpContext ctx) => ServeBundle(
            ctx, "text/css; charset=utf-8",
            string.Join("\n", UiAssets.Where(f => f.StartsWith("css/")).Select(LoadResource))));

        // ---- 自定义背景 ----
        // 背景图存 memory/webui/background.*（上传时统一转存为 background.jpg/png）
        string BgDir() => Path.Combine(settings.MemoryDir, "webui");
        string? BgPath()
        {
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp" })
            {
                var p = Path.Combine(BgDir(), "background" + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        app.MapGet("/background", () =>
        {
            var path = BgPath();
            if (path is null) return Results.NotFound();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
            return Results.File(path, mime);
        });
        app.MapDelete("/background", () =>
        {
            var path = BgPath();
            if (path is null) return Results.Json(new { ok = false, error = "未设置背景" });
            System.IO.File.Delete(path);
            return Results.Json(new { ok = true });
        });
        app.MapPost("/background", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType || req.Form.Files.Count == 0)
                return Results.Json(new { ok = false, error = "请选择图片文件" });
            var file = req.Form.Files[0];
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant() switch
            {
                ".png" => ".png", ".webp" => ".webp", ".gif" => ".jpg", // gif 存首帧近似：直接按原扩展不转码
                ".jpg" or ".jpeg" => ".jpg",
                _ => ""
            };
            if (ext == "") return Results.Json(new { ok = false, error = "仅支持 jpg/png/webp" });
            if (file.Length > 15 * 1024 * 1024) return Results.Json(new { ok = false, error = "图片不能超过 15MB" });
            Directory.CreateDirectory(BgDir());
            // 先清掉旧背景（换扩展名防残留）
            foreach (var old in Directory.GetFiles(BgDir(), "background.*"))
                try { System.IO.File.Delete(old); } catch { }
            var dest = Path.Combine(BgDir(), "background" + ext);
            await using var fs = System.IO.File.Create(dest);
            await file.CopyToAsync(fs);
            return Results.Json(new { ok = true });
        });

        // ---- 主题个性化配置（透明度等）----
        app.MapGet("/api/theme-cfg", () => Results.Json(new
        {
            uiOpacity = settings.WebUiUiOpacity,
            ctlOpacity = settings.WebUiCtlOpacity,
            bgOpacity = settings.WebUiBgOpacity,
            hasBackground = BgPath() is not null,
        }));
        app.MapPost("/api/theme-cfg", (ThemeCfgPayload p) =>
        {
            if (p.UiOpacity.HasValue) settings.WebUiUiOpacity = Math.Clamp(p.UiOpacity.Value, 0, 100);
            if (p.CtlOpacity.HasValue) settings.WebUiCtlOpacity = Math.Clamp(p.CtlOpacity.Value, 0, 100);
            if (p.BgOpacity.HasValue) settings.WebUiBgOpacity = Math.Clamp(p.BgOpacity.Value, 0, 100);
            return Results.Json(new { ok = true, uiOpacity = settings.WebUiUiOpacity, ctlOpacity = settings.WebUiCtlOpacity, bgOpacity = settings.WebUiBgOpacity });
        });

        app.MapGet("/api/status", () => Results.Json(new
        {
            connected = BotState.Connected,
            selfId = BotState.SelfId,
            wsUrl = settings.WsUrl,
            transport = client.TransportName,
            startedAt = BotState.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            uptimeSec = (long)(DateTime.Now - BotState.StartedAt).TotalSeconds
        }));

        // 探活（免认证）：只暴露"活着、连没连上、跑多久"，不含任何敏感信息
        app.MapGet("/healthz", () => Results.Json(new
        {
            ok = true,
            connected = BotState.Connected,
            transport = client.TransportName,
            uptimeSec = (long)(DateTime.Now - BotState.StartedAt).TotalSeconds
        }));

        // 运行统计（认证）：消息/命令/重连/去重/插件异常计数 + 最近消息缓冲条数
        app.MapGet("/api/stats", () => Results.Json(Stats.Snapshot()));

        // 实时消息流（认证）：增量轮询，协议与 /api/logs 一致（用返回的 next 作为下次 after）
        app.MapGet("/api/messages", (long after, int? max) => Results.Json(Stats.Recent.GetAfter(after, max ?? 200)));

        // 历史消息（认证）：只读统计 / 会话清单 / 最近消息（数据层 P1；全文检索属于 P2）
        static string FmtTs(long unix) => unix <= 0 ? "—"
            : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        app.MapGet("/api/history/stats", () =>
        {
            var s = MessageStore.Current;
            if (s is null) return Results.Json(new { enabled = false });
            var (total, group, priv, inbound, outbound, queued, bytes, oldest, newest) = s.Stats();
            return Results.Json(new
            {
                enabled = true,
                total,
                group,
                privateMsgs = priv,
                inbound,
                outbound,
                queued,
                bytes,
                oldest,
                newest,
                groupKeepDays = MessageStore.GroupKeepDays,
                privateKeepDays = MessageStore.PrivateKeepDays,
            });
        });

        app.MapGet("/api/history/scopes", () =>
        {
            var s = MessageStore.Current;
            if (s is null) return Results.Json(Array.Empty<object>());
            return Results.Json(s.Scopes().Select(x => new { scope = x.Scope, count = x.Count, last = FmtTs(x.Last) }));
        });

        app.MapGet("/api/history/recent", (string? scope, int? limit) =>
        {
            var s = MessageStore.Current;
            if (s is null || string.IsNullOrWhiteSpace(scope)) return Results.Json(Array.Empty<object>());
            return Results.Json(s.Recent(scope, limit ?? 50));
        });

        app.MapGet("/api/logs", (int after) =>
        {
            var (next, added) = LogStore.GetAfter(after);
            return Results.Json(new
            {
                next,
                lines = added.Select(l => new { id = l.Id, t = l.Text })
            });
        });

        app.MapGet("/api/plugins", () =>
            Results.Json(plugins.GetSnapshot().Select(p => new
            {
                name = p.Name,
                version = p.Version,
                desc = p.Description,
                loaded = p.Loaded,
                autoLoad = p.AutoLoad,
                source = p.Source,   // ai = AI 生成 / manual = pb_build 手工 / handmade = 无生成记录
                model = p.Model
            })));

        // ---- 插件：重新搜索目录（把手工拷进来的插件目录识别出来）----
        app.MapPost("/api/plugins/rescan", () =>
        {
            var (total, loaded) = plugins.Rescan();
            return Results.Json(new { ok = true, total, loaded });
        });

        // ---- 插件：上传 zip 安装（解压到 plugins/<名>/ → 覆盖同名旧版 → 自动加载）----
        app.MapPost("/api/plugins/upload", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType || req.Form.Files.Count == 0)
                return Results.Json(new { ok = false, error = "请选择 .zip 文件" });

            var file = req.Form.Files[0];
            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { ok = false, error = "仅支持 .zip（把插件目录打包成压缩包上传）" });
            if (file.Length == 0)
                return Results.Json(new { ok = false, error = "文件为空" });

            try
            {
                await using var stream = file.OpenReadStream();
                var fallback = Path.GetFileNameWithoutExtension(file.FileName);
                var r = await plugins.InstallFromZipAsync(stream, fallback, load: true);
                return Results.Json(new
                {
                    ok = r.Ok,
                    name = r.Name,
                    installed = r.Installed,   // 一个 zip 可能装了多个插件
                    fileCount = r.FileCount,
                    loaded = r.Loaded,
                    warnings = r.Warnings,
                    message = r.Message,
                    error = r.Ok ? null : r.Message
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[webui] 插件 zip 安装失败: {ex}");
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        app.MapPost("/api/plugins/{name}/{action}", async (string name, string action) =>
        {
            // 插件名必须是单一目录名：挡掉 ..\ 之类的路径穿越（PluginManager 内部还有一层同样的校验）
            if (!PluginManager.IsSafePluginName(name)) return Results.Json(new { ok = false, error = "插件名非法" });
            var ok = action switch
            {
                "load" => await plugins.LoadAsync(name),
                "unload" => await plugins.UnloadAsync(name),
                "reload" => await plugins.ReloadAsync(name),
                _ => false
            };
            return Results.Json(new { ok });
        });

        // ===================== 插件设置 =====================

        app.MapGet("/api/plugins/{name}/settings", (string name) =>
        {
            if (!PluginManager.IsSafePluginName(name)) return Results.Json(new { ok = false, error = "插件名非法" });
            var defs = plugins.GetSettingDefs(name);
            return Results.Json(new
            {
                loaded = plugins.IsLoaded(name),
                defs = defs.Select(d => new { d.Key, d.Label, d.Type, d.Default, d.Description, d.Options }),
                values = plugins.GetSettings(name)
            });
        });

        app.MapPost("/api/plugins/{name}/settings", async (string name, Dictionary<string, object?>? body) =>
        {
            if (!PluginManager.IsSafePluginName(name)) return Results.Json(new { ok = false, error = "插件名非法" });
            var (ok, error) = await plugins.UpdateSettingsAsync(name, body);
            return Results.Json(new { ok, error });
        });

        // ---- 人格 md 文件管理：正文存 <插件目录>/Agent/personas/<file>.md，Personas 设置只留元数据 ----
        static string PersonaDir(PluginManager pm) => Path.Combine(pm.PluginRoot, "Agent", "personas");
        app.MapGet("/api/personas/files", ([FromServices] PluginManager pm) =>
        {
            var dir = PersonaDir(pm);
            string[] files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.md").Select(Path.GetFileName)!.Where(f => f is not null).Select(f => f!).OrderBy(x => x).ToArray()
                : [];
            return Results.Json(new { files });
        });
        app.MapGet("/api/personas/file", ([FromServices] PluginManager pm, string? file) =>
        {
            if (string.IsNullOrWhiteSpace(file)) return Results.Json(new { error = "缺少 file" });
            var path = Path.Combine(PersonaDir(pm), Path.GetFileName(file));
            return Results.Json(new { ok = File.Exists(path), text = File.Exists(path) ? File.ReadAllText(path) : "" });
        });
        app.MapPost("/api/personas/file", ([FromServices] PluginManager pm, PersonaFilePayload p) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p.File)) return Results.Json(new { ok = false, error = "缺少 file" });
                var safe = Path.GetFileName(p.File.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? p.File : p.File + ".md");
                var dir = PersonaDir(pm);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, safe), p.Text ?? "");
                return Results.Json(new { ok = true, file = safe });
            }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
        });
        app.MapDelete("/api/personas/file", ([FromServices] PluginManager pm, string file) =>
        {
            try
            {
                var path = Path.Combine(PersonaDir(pm), Path.GetFileName(file));
                if (File.Exists(path)) File.Delete(path);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
        });
        app.MapPost("/api/command", async (CommandPayload payload) =>
        {
            var text = payload.Text ?? "";
            // 走完整命令分发（内置 + 插件注册命令；WebUI 视为管理员通道）
            var prefix = string.IsNullOrEmpty(settings.CommandPrefix) ? "!" : settings.CommandPrefix;
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
                return Results.Json(new { message = $"命令需以 {prefix} 开头" });
            var body = text[prefix.Length..].TrimStart();
            var sp = body.IndexOf(' ');
            var cmdName = (sp < 0 ? body : body[..sp]).ToLowerInvariant();
            var args = sp < 0 ? "" : body[(sp + 1)..].Trim();
            if (cmdName is "plugin" or "help" or "帮助")
                return Results.Json(new { message = await plugins.HandleCommandAsync(text) });
            var result = await plugins.InvokeCommandAsync(cmdName, args);
            return Results.Json(new { message = result ?? $"未知命令: {cmdName}（{prefix}help 查看）" });
        });

        app.MapPost("/api/logs/clear", () =>
        {
            LogStore.Clear();
            return Results.Ok();
        });

        // 优雅停止宿主：卸载插件 → 断开通道 → 停随宿主拉起的依赖进程（先回响应再退出）
        app.MapPost("/api/shutdown", () =>
        {
            if (requestShutdown is null) return Results.Json(new { ok = false, error = "当前宿主未启用该能力" });
            Console.WriteLine("[webui] 收到停止宿主请求，开始优雅停机");
            _ = Task.Run(async () => { await Task.Delay(300); try { requestShutdown(); } catch { } });
            return Results.Json(new { ok = true, message = "宿主正在优雅退出：卸载插件 → 断开通道 → 停依赖进程" });
        });

        // ===================== 启动依赖 + 立即连接 =====================

        // 依赖状态查询：前端按钮据此决定默认行为（两配置均在且未跑 → 弹选择）
        app.MapGet("/api/deps", () => Results.Json(new
        {
            napcatConfigured = !string.IsNullOrWhiteSpace(settings.NapCatCmd),
            llbotConfigured = !string.IsNullOrWhiteSpace(settings.LLBotCmd),
            napcatRunning = procs.IsNapCatRunning(),
            llbotRunning = procs.IsLlBotRunning(),
            connected = BotState.Connected
        }));

        // start = napcat / llbot / none；启动后自动尝试连接
        app.MapPost("/api/start-connect", (StartConnectPayload p) =>
        {
            var notes = new List<string>();
            var started = false;
            if (p.Start is "napcat" or "llbot")
            {
                var cmd = p.Start == "napcat" ? settings.NapCatCmd : settings.LLBotCmd;
                started = procs.StartOne(p.Start, cmd, out var msg);
                notes.Add(msg);
            }
            if (BotState.Connected)
            {
                notes.Add("WS 已连接");
                return Results.Json(new { ok = started, connected = true, message = string.Join("；", notes) });
            }
            // 启动了新进程：等几秒让它就绪再连；否则直接连
            _ = Task.Run((Func<Task?>)(async () =>
            {
                if (started) await Task.Delay(6000);
                try { await client.ConnectAsync(settings.WsUrl, settings.Token); }
                catch (Exception ex) { Console.WriteLine($"[webui] 启动后连接失败: {ex.Message}（看门狗将继续重试）"); }
            }));
            notes.Add("正在连接…");
            return Results.Json(new { ok = true, connected = false, message = string.Join("；", notes) });
        });

        // ===================== 立即连接（手动触发，不等指数退避） =====================

        app.MapPost("/api/connect", () =>
        {
            if (BotState.Connected) return Results.Json(new { ok = true, message = "已处于连接状态" });
            // 后台连接（不等完成，前端轮询 /api/status 看结果）；失败由看门狗循环保底
            _ = Task.Run((Func<Task?>)(async () =>
            {
                try { await client.ConnectAsync(settings.WsUrl, settings.Token); }
                catch (Exception ex) { Console.WriteLine($"[webui] 手动连接失败: {ex.Message}（看门狗将继续重试）"); }
            }));
            return Results.Json(new { ok = true, message = "正在连接…" });
        });

        // ===================== 配置 =====================

        app.MapGet("/api/config", () => Results.Json(new
        {
            wsUrl = settings.WsUrl,
            token = settings.Token,
            ownerUin = settings.OwnerUin,
            adminUins = settings.ExtraAdminUins.Where(x => x > 0 && x != settings.OwnerUin).ToList(),
            memoryDir = settings.MemoryDir,
            reloadPluginsAfterSave = settings.ReloadPluginsAfterSave,
            napCatCmd = settings.NapCatCmd,
            llBotCmd = settings.LLBotCmd,
            logDir = settings.LogDir,
            isolateDependencyLogs = settings.IsolateDependencyLogs,
            webUiPort = settings.WebUiPort,
            webUiPasswordSet = !string.IsNullOrWhiteSpace(settings.WebUiPassword),
            transport = settings.Transport,
            officialAppId = settings.OfficialAppId,
            officialSandbox = settings.OfficialSandbox,
            officialRegisterCommands = settings.OfficialRegisterCommands,
            officialAppSecretSet = !string.IsNullOrWhiteSpace(settings.OfficialAppSecret),   // 密钥不回显（只告诉是否已设置）
            pluginAutoload = plugins.GetSnapshot().ToDictionary(p => p.Name, p => p.AutoLoad)
        }));

        app.MapPost("/api/config", async (ConfigPayload p) =>
        {
            var oldWs = settings.WsUrl;
            var oldToken = settings.Token;
            var oldTransport = settings.Transport;
            try
            {
                if (p.WsUrl is not null) settings.WsUrl = p.WsUrl.Trim();
                if (p.Token is not null) settings.Token = p.Token.Trim();
                if (p.OwnerUin is > 0) settings.OwnerUin = p.OwnerUin.Value;
                if (p.AdminUins is not null)
                {
                    var admins = p.AdminUins.Where(x => x > 0 && x != settings.OwnerUin).Distinct().ToList();
                    settings.ExtraAdminUins = admins;
                }
                if (settings.OwnerUin <= 0)
                    return Results.Json(new { ok = false, error = "必须设置机器人主人 QQ。" });
                if (p.MemoryDir is not null && p.MemoryDir.Trim().Length > 0) settings.MemoryDir = p.MemoryDir.Trim();
                if (p.ReloadPluginsAfterSave.HasValue) settings.ReloadPluginsAfterSave = p.ReloadPluginsAfterSave.Value;
                if (p.PluginAutoload is not null) settings.PluginAutoload = p.PluginAutoload;
                if (p.NapCatCmd is not null) settings.NapCatCmd = p.NapCatCmd.Trim();
                if (p.LLBotCmd is not null) settings.LLBotCmd = p.LLBotCmd.Trim();
                if (p.LogDir is not null) settings.LogDir = p.LogDir.Trim();
                if (p.IsolateDependencyLogs.HasValue) settings.IsolateDependencyLogs = p.IsolateDependencyLogs.Value;
                if (p.WebUiPort.HasValue)
                {
                    var port = p.WebUiPort.Value;
                    if (port < 0 || port > 65535) return Results.Json(new { ok = false, error = "WebUI 端口必须在 1-65535 之间（0 表示自动）。" });
                    settings.WebUiPort = port;
                }
                if (p.WebUiPassword is not null)
                {
                    var pwdRaw = p.WebUiPassword.Trim();
                    // "-" = 清除密码（关认证）；否则存 SHA256（不落明文）
                    settings.WebUiPassword = pwdRaw == "-" ? "" : Sha256Hex(pwdRaw);
                }
                // 通道：onebot（LLBot/NapCat）或 official（QQ 官方机器人）；改完需重启宿主生效
                if (p.Transport is not null)
                {
                    var t = p.Transport.Trim().ToLowerInvariant();
                    if (t is not ("onebot" or "official"))
                        return Results.Json(new { ok = false, error = "通道只能是 onebot 或 official。" });
                    settings.Transport = t;
                }
                if (p.OfficialAppId is not null) settings.OfficialAppId = p.OfficialAppId.Trim();
                if (p.OfficialSandbox.HasValue) settings.OfficialSandbox = p.OfficialSandbox.Value;
                if (p.OfficialRegisterCommands.HasValue) settings.OfficialRegisterCommands = p.OfficialRegisterCommands.Value;
                // 官方机器人密钥：留空=不修改；"-"=清除（与 WebUI 密码同规矩，且从不回显）
                if (!string.IsNullOrEmpty(p.OfficialAppSecret))
                {
                    var sec = p.OfficialAppSecret.Trim();
                    settings.OfficialAppSecret = sec == "-" ? "" : sec;
                }
                settings.NormalizeAccessSettings();
                settings.Save();
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }

            // 按需热重载已加载插件（模型/MCP/提示词等在重载后生效）
            var reloaded = new List<string>();
            if (p.ReloadPlugins == true)
            {
                foreach (var name in plugins.GetSnapshot().Where(x => x.Loaded).Select(x => x.Name).ToList())
                    if (await plugins.ReloadAsync(name)) reloaded.Add(name);
            }

            // 连接地址/令牌变更：已连接则断开触发看门狗用新地址重连；未连接则立即试新地址（不等退避计时器）
            var wsChanged = settings.WsUrl != oldWs || settings.Token != oldToken;
            if (wsChanged && BotState.Connected)
            {
                try { await client.DisconnectAsync(); } catch { }
            }
            // 保存时仍未连接：无论配置是否变化都立即尝试连接（刚把 NapCat 拉起来/改完地址的场景）
            if (!BotState.Connected)
            {
                _ = Task.Run((Func<Task?>)(async () =>
                {
                    try { await client.ConnectAsync(settings.WsUrl, settings.Token); }
                    catch (Exception ex) { Console.WriteLine($"[webui] 保存后重连失败: {ex.Message}（看门狗将继续）"); }
                }));
            }

            // 通道变更只在下次启动生效（当前进程已在用某条通道），前端据此提示"需重启"
            var transportChanged = settings.Transport != oldTransport;
            return Results.Json(new { ok = true, wsChanged, reloaded, transportChanged });
        });

        // ===================== 模型测试 =====================

        app.MapPost("/api/test-model", async (TestModelPayload p) =>
        {
            // 默认值取 Agent 插件设置（POCO 反序列化空字符串转 null）
            static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
            var apiKey = NonEmpty(p.ApiKey) ?? plugins.GetPluginSettingString("Agent", "ApiKey") ?? settings.ApiKey;
            var baseUrl = (NonEmpty(p.BaseUrl) ?? plugins.GetPluginSettingString("Agent", "BaseUrl") ?? settings.BaseUrl).Trim();
            var model = (NonEmpty(p.Model) ?? plugins.GetPluginSettingString("Agent", "Model") ?? settings.Model).Trim();
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                return Results.Json(new { ok = false, error = "Base URL 无效（需 http(s):// 开头）" });

            // base 不以 / 结尾时 new Uri(base, rel) 会丢掉最后一段（/v1 → 根路径）→ 404。
            // 统一补尾斜杠后再拼 chat/completions。
            var fixedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
            var endpoint = new Uri(new Uri(fixedBase), "chat/completions");

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
                var body = JsonSerializer.Serialize(new
                {
                    model,
                    messages = new[] { new { role = "user", content = "回复：OK" } },
                    max_tokens = 8
                });
                var resp = await http.PostAsync(endpoint,
                    new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                var text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    var msg = text.Length > 300 ? text[..300] + "…" : text;
                    return Results.Json(new { ok = false, error = $"HTTP {(int)resp.StatusCode}: {msg}" });
                }
                using var doc = JsonDocument.Parse(text);
                var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
                    .TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : "";
                return Results.Json(new { ok = true, reply, model });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // ===================== 系统提示词（Chat 插件） =====================

        app.MapGet("/api/instructions", () =>
        {
            var path = InstructionsPath(plugins);
            return Results.Json(new { text = File.Exists(path) ? File.ReadAllText(path) : "" });
        });

        app.MapPost("/api/instructions", async (InstructionsPayload p) =>
        {
            var path = InstructionsPath(plugins);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, p.Text ?? "");
            var reloaded = false;
            if (p.Reload == true && plugins.IsLoaded("Agent"))
                reloaded = await plugins.ReloadAsync("Agent");
            return Results.Json(new { ok = true, reloaded });
        });

        _ = app.RunAsync();
        return $"http://127.0.0.1:{port}";
    }

    // 提示词文件：写在插件源目录（加载时随影子拷贝进入，重载 Agent 即应用）
    private static string InstructionsPath(PluginManager plugins) =>
        Path.Combine(plugins.PluginRoot, "Agent", "agent_instructions.md");

    // SHA256 十六进制（小写）——WebUI 密码只用哈希存储/比对
    private static string Sha256Hex(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsSha256Hex(string s) =>
        s.Length == 64 && s.All(char.IsAsciiHexDigit);

    // 常数时间比较（防时序侧信道）
    private static bool FixedTimeEquals(string hashA, string hashB)
    {
        var a = Convert.FromHexString(hashA);
        var b = Convert.FromHexString(hashB);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed record ThemeCfgPayload(int? UiOpacity, int? CtlOpacity, int? BgOpacity);
    private sealed record PersonaFilePayload(string? File, string? Text);
    private sealed record LoginPayload(string? Password, bool? Remember);
    private sealed record StartConnectPayload(string? Start);
    private sealed record CommandPayload(string? Text);
    private sealed record ConfigPayload(
        string? WsUrl, string? Token,
        long? OwnerUin, List<long>? AdminUins, string? MemoryDir,
        bool? ReloadPluginsAfterSave,
        Dictionary<string, bool>? PluginAutoload, bool? ReloadPlugins,
        string? NapCatCmd, string? LLBotCmd, string? LogDir, bool? IsolateDependencyLogs,
        int? WebUiPort, string? WebUiPassword,
        string? Transport, string? OfficialAppId, string? OfficialAppSecret, bool? OfficialSandbox, bool? OfficialRegisterCommands);
    private sealed record TestModelPayload(string? ApiKey, string? BaseUrl, string? Model);
    private sealed record InstructionsPayload(string? Text, bool? Reload);

    private static int GetPort(int configPort)
    {
        // 如果配置为 0 或无效，自动分配
        if (configPort <= 0 || configPort > 65535)
            return GetFreePort();

        // 检查是否为保留端口
        if (ReservedPorts.Contains(configPort))
        {
            Console.WriteLine($"[webui] ⚠️ 端口 {configPort} 是常见系统端口，不允许使用。自动改用随机端口。");
            return GetFreePort();
        }

        // 尝试绑定配置端口
        try
        {
            var listener = new TcpListener(IPAddress.Any, configPort);
            listener.Start();
            listener.Stop();
            return configPort;
        }
        catch
        {
            Console.WriteLine($"[webui] ⚠️ 端口 {configPort} 已被占用，自动改用随机端口。");
            return GetFreePort();
        }
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ===================== 静态资源（磁盘 + 热更） =====================

    /// <summary>进程真实 stdout：绕过 Console.SetOut 的日志接管（用于不该进日志的内容，如随机密码）。
    /// 只打开一次、永不 Dispose（Dispose 可能关掉底层句柄）。</summary>
    private static readonly Stream RawStdout = Console.OpenStandardOutput();

    /// <summary>只写到真实控制台（无控制台/被重定向时写进重定向目标），不写日志文件与 WebUI 日志环形缓冲</summary>
    private static void WriteConsoleOnly(string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            RawStdout.Write(bytes, 0, bytes.Length);
            RawStdout.Flush();
        }
        catch { /* 没有控制台时忽略 */ }
    }

    /// <summary>随机密码：字母表已去掉易混字符（0/O/1/l/I），加密强随机</summary>
    private static string GeneratePassword(int len)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var buf = new char[len];
        for (var i = 0; i < len; i++) buf[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(buf);
    }

    /// <summary>WebUI 静态资源目录：&lt;exe旁&gt;/WebUi/（构建与发布时由 csproj 的 Content 项拷贝过来）</summary>
    private static string WebUiDir => Path.Combine(AppContext.BaseDirectory, "WebUi");

    private static string AssetPath(string name) =>
        Path.Combine(WebUiDir, name.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>每次调用都读盘：改了 CSS/JS 只要刷新浏览器就生效（无需重建宿主、无需重启）</summary>
    private static string LoadResource(string name)
    {
        var path = AssetPath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"WebUI 资源缺失: {path}（发布包需要带上 WebUi/ 目录）");
        return File.ReadAllText(path);
    }

    private static string LoadIndex() => LoadResource("index.html");

    /// <summary>资源版本指纹：随各文件大小/修改时间变化 —— 改了文件版本号自动变，不用手改 index.html</summary>
    private static string PageVersion()
    {
        var sb = new StringBuilder();
        foreach (var f in UiAssets)
        {
            var fi = new FileInfo(AssetPath(f));
            sb.Append(f).Append('|').Append(fi.Exists ? fi.Length : -1).Append('|')
              .Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..8].ToLowerInvariant();
    }

    /// <summary>首页 HTML：把 index.html 里的 app.css?v=… / app.js?v=… 换成当前版本指纹</summary>
    private static string RenderIndex() => RenderHtml("index.html");

    /// <summary>HTML 页面统一处理：注入资源版本指纹（index.html 与 login.html 都要，
    /// 否则页面里的 ?v= 是写死的旧值，一旦被浏览器缓存成坏内容就再也刷不掉），并禁用 HTML 自身缓存</summary>
    private static IResult RenderHtmlResult(HttpContext ctx, string name)
    {
        ctx.Response.Headers.CacheControl = "no-cache";
        return Results.Text(RenderHtml(name), "text/html; charset=utf-8");
    }

    private static string RenderHtml(string name) =>
        // 必须写成 ${1}：若写成 "$1"+ver，当 ver 以数字开头时 "$14..." 会被当作"第 14 个捕获组"
        // （不存在则原样输出），整个替换结果会变成 /$14ba40431 这种坏 URL
        Regex.Replace(LoadResource(name), @"(app\.(?:css|js)\?v=)[^""']*", "${1}" + PageVersion());

    /// <summary>带 ETag 的合并资源响应：内容没变回 304（省流量），变了立刻给新内容</summary>
    private static IResult ServeBundle(HttpContext ctx, string contentType, string body)
    {
        var ver = PageVersion();
        ctx.Response.Headers.ETag = '"' + ver + '"';
        ctx.Response.Headers.CacheControl = "no-cache";   // 每次带 ETag 来校验，不做强缓存
        // 比对时同时容忍 "ver" 与 ver 两种写法（规范要求带引号，但客户端实现并不齐）
        var inm = ctx.Request.Headers.IfNoneMatch.ToString().Trim().Trim('"');
        if (inm.Length > 0 && inm == ver)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Text(body, contentType);
    }

    /// <summary>启动自检：清单与磁盘对不上就明确报出来（新加文件忘了登记不会静默失效）</summary>
    private static void CheckAssets()
    {
        try
        {
            var missing = UiAssets.Where(a => !File.Exists(AssetPath(a))).ToList();
            var extra = Directory.Exists(WebUiDir)
                ? Directory.GetFiles(WebUiDir, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(WebUiDir, p).Replace('\\', '/'))
                    .Where(rel => (rel.EndsWith(".js") || rel.EndsWith(".css"))
                                  && !UiAssets.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    .ToList()
                : [];
            if (missing.Count > 0)
                Console.WriteLine($"[webui] 警告：清单里的资源在磁盘上不存在 → {string.Join(", ", missing)}");
            if (extra.Count > 0)
                Console.WriteLine($"[webui] 警告：磁盘上有未登记的资源（不会被 /app.js|/app.css 打包）→ {string.Join(", ", extra)}");
            Console.WriteLine($"[webui] 静态资源目录 {WebUiDir}（版本 {PageVersion()}）");
        }
        catch (Exception ex) { Console.WriteLine($"[webui] 资源自检失败: {ex.Message}"); }
    }
}
