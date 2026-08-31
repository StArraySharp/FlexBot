using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlexBot.WebUi;

// 内嵌 WebUI：本机 HTTP 服务，同一页面供浏览器与 Photino 窗口使用
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
        "js/config.js", "js/appearance.js", "js/auth.js", "js/quickcmd.js", "js/main.js",
        // CSS 顺序：变量/基础 → 侧栏 → 通用组件 → 页面 → 移动端
        "css/base.css", "css/nav.css", "css/components.css", "css/pages.css", "css/mobile.css",
    ];

    private static readonly int[] ReservedPorts = [
        80, 443, 3000, 3001, 5000, 5173, 5174, 8000, 8001, 8008, 8080, 8443, 9000,
        22, 23, 25, 53, 110, 143, 465, 587, 993, 995, 3306, 5432, 6379
    ];

    // 登录会话 token（重启轮换；认证开启时所有 /api/* 与页面均需携带）
    private static readonly string SessionToken = Convert.ToHexString(Guid.NewGuid().ToByteArray())
        .ToLowerInvariant();

    public static string Start(PluginManager plugins, HostSettings settings, BotClient client, ProcessManager procs)
    {
        var port = GetPort(settings.WebUiPort);
        // 绑定地址：BindAll=true → 0.0.0.0（公网可达）；默认 127.0.0.1 仅本机
        var bindAddr = settings.WebUiBindAll ? "0.0.0.0" : "127.0.0.1";
        var authOn = !string.IsNullOrWhiteSpace(settings.WebUiPassword);
        // 兼容手写明文：非 64 位十六进制一律视为明文，启动时自动转 SHA256 存储（磁盘不落明文）
        if (authOn && !IsSha256Hex(settings.WebUiPassword))
        {
            settings.WebUiPassword = Sha256Hex(settings.WebUiPassword);
            settings.Save();
            Console.WriteLine("[webui] WebUiPassword 已由明文自动转换为 SHA256 存储");
        }
        Console.WriteLine($"[webui] 启动 WebUI 在 http://{bindAddr}:{port}{(bindAddr == "0.0.0.0" ? "（全网卡，公网可达）" : "")}{(authOn ? "，已开启登录认证" : "，无密码（仅限本机安全）")}");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(plugins);
        builder.Logging.ClearProviders(); // 不让 ASP.NET 日志污染控制台
        builder.WebHost.UseUrls($"http://{bindAddr}:{port}");
        var app = builder.Build();

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
            if (pStr == "/login" || pStr == "/app.css" || pStr == "/app.js"
                || pStr.StartsWith("/css/") || pStr.StartsWith("/js/")
                || pStr.StartsWith("/api/auth")
                || pStr == "/api/theme-cfg"
                || pStr == "/background") { await next(); return; }
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

        var html = LoadIndex();
        app.MapGet("/", () => Results.Text(html, "text/html; charset=utf-8"));

        // ---- 登录页与认证接口 ----
        app.MapGet("/login", () => Results.Text(LoadResource("login.html"), "text/html; charset=utf-8"));
        app.MapPost("/api/auth/login", (LoginPayload p, HttpResponse resp) =>
        {
            if (string.IsNullOrWhiteSpace(p.Password) || !FixedTimeEquals(Sha256Hex(p.Password), settings.WebUiPassword))
            {
                Console.WriteLine($"[webui] 登录失败：密码错误");
                return Results.Json(new { ok = false, error = "密码错误" });
            }
            Console.WriteLine($"[webui] 登录成功");
            var cookieOpt = new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" };
            if (p.Remember == true) cookieOpt.MaxAge = TimeSpan.FromDays(30);
            resp.Cookies.Append("session_token", SessionToken, cookieOpt);
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
        app.MapGet("/app.js", () => Results.Text(
            string.Join("\n", UiAssets.Where(f => f.StartsWith("js/")).Select(LoadResource)),
            "application/javascript; charset=utf-8"));
        app.MapGet("/app.css", () => Results.Text(
            string.Join("\n", UiAssets.Where(f => f.StartsWith("css/")).Select(LoadResource)),
            "text/css; charset=utf-8"));

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
            startedAt = BotState.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            uptimeSec = (long)(DateTime.Now - BotState.StartedAt).TotalSeconds
        }));

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
                autoLoad = p.AutoLoad
            })));

        app.MapPost("/api/plugins/{name}/{action}", async (string name, string action) =>
        {
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
            adminUins = settings.AdminUins.Where(x => x > 0 && x != settings.OwnerUin).ToList(),
            memoryDir = settings.MemoryDir,
            reloadPluginsAfterSave = settings.ReloadPluginsAfterSave,
            napCatCmd = settings.NapCatCmd,
            llBotCmd = settings.LLBotCmd,
            logDir = settings.LogDir,
            isolateDependencyLogs = settings.IsolateDependencyLogs,
            webUiPort = settings.WebUiPort,
            webUiBindAll = settings.WebUiBindAll,
            webUiPasswordSet = !string.IsNullOrWhiteSpace(settings.WebUiPassword),
            pluginAutoload = plugins.GetSnapshot().ToDictionary(p => p.Name, p => p.AutoLoad)
        }));

        app.MapPost("/api/config", async (ConfigPayload p) =>
        {
            var oldWs = settings.WsUrl;
            var oldToken = settings.Token;
            try
            {
                if (p.WsUrl is not null) settings.WsUrl = p.WsUrl.Trim();
                if (p.Token is not null) settings.Token = p.Token.Trim();
                if (p.OwnerUin is > 0) settings.OwnerUin = p.OwnerUin.Value;
                if (p.AdminUins is not null)
                {
                    var admins = p.AdminUins.Where(x => x > 0 && x != settings.OwnerUin).Distinct().ToList();
                    settings.AdminUins = admins;
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
                if (p.WebUiBindAll.HasValue)
                {
                    // 公网暴露强制要求密码已设（本次或之前）
                    if (p.WebUiBindAll.Value && string.IsNullOrWhiteSpace(settings.WebUiPassword) && string.IsNullOrEmpty(p.WebUiPassword))
                        return Results.Json(new { ok = false, error = "公网绑定必须同时设置登录密码。" });
                    settings.WebUiBindAll = p.WebUiBindAll.Value;
                }
                if (p.WebUiPassword is not null)
                {
                    var pwdRaw = p.WebUiPassword.Trim();
                    // "-" = 清除密码（关认证）；否则存 SHA256（不落明文）
                    settings.WebUiPassword = pwdRaw == "-" ? "" : Sha256Hex(pwdRaw);
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
                try { await client.CloseAsync(); } catch { }
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

            return Results.Json(new { ok = true, wsChanged, reloaded });
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
        int? WebUiPort, bool? WebUiBindAll, string? WebUiPassword);
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
            var listener = new TcpListener(IPAddress.Loopback, configPort);
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
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string LoadIndex() => LoadResource("index.html");

    private static string LoadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"内嵌资源 {name} 不存在");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
