using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlexBot.PluginApi;
using OneBotLib.Events;

namespace AOnlinePlugin;

/// <summary>
/// AOnline 插件：对接 ADOFAI Online 根服务器。
/// - 后台定时轮询管理员申请列表（get_all_requests 命令可手动拉取）
/// - 设置：服务器 URL / 登录用户名 / 密码 / 轮询间隔
/// 认证流程：POST /auth/login → {token, uid} → Authorization: Bearer token 调管理接口。
/// API 参照 memsys-lizi/ADOFAIOnline-ServerOpenSource（API.md）。
/// </summary>
public sealed class AOnlinePlugin : IBotPlugin
{
    private IBotContext _ctx = null!;
    private IBotScheduler? _sched;
    private IDisposable? _schedSub;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private CancellationTokenSource? _cts;

    private string _baseUrl = "";
    private string _token = "";
    private long _uid;
    private DateTime _tokenAt;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(6); // 服务器默认 7 天，提前刷新

    // 上次见过的申请 ID 集合（新申请通知用）
    private HashSet<long> _seenIds = [];
    private bool _initialized;

    public string Name => "AOnline";
    public string Version => "1.1.0";
    public string Description => "AOnline 根服务器对接：申请列表轮询通知 + get_requests/get_all_requests/login 命令（私聊/群聊均可用）";

    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("BaseUrl", "根服务器 URL", "text", "", "如 http://your.server:4004（末尾不带 /）"),
        new("Token", "登录 Token（推荐）", "password", "", "浏览器登录后 F12 → 网络 → login 响应里复制 token 粘贴 сюда；优先使用"),
        new("Username", "登录用户名", "text", "", "备用自动登录用（需过 ALTCHA）"),
        new("Password", "登录密码", "password", "", "备用自动登录用"),
        new("PollSeconds", "检测频率（秒）", "number", "60", "后台轮询申请列表间隔；0 = 关闭轮询"),
        new("NotifyTarget", "通知目标", "text", "", "新增申请发送到哪：群号 / p:QQ号（私聊）；留空 = 不通知"),
    ];

    public Task OnSettingsChangedAsync()
    {
        _ctx?.Log.Info("设置已热应用（重启插件生效轮询周期）");
        return Task.CompletedTask;
    }

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;
        _sched = context.Scheduler;
        ReadSettings();

        context.RegisterCommand("get_all_requests", "拉取 AOnline 全部申请列表", _ => GetAllRequestsAsync());
        context.RegisterCommand("get_requests", "拉取最近 N 条申请", GetRequestsCmdAsync, "get_requests <条数>");
        context.RegisterCommand("login", "手动登录 AOnline（测试凭据）", _ => LoginCmdAsync());

        StartPolling();
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync()
    {
        StopPolling();
        _ctx = null!;
        return Task.CompletedTask;
    }

    private void ReadSettings()
    {
        _baseUrl = _ctx.GetSetting("BaseUrl", "").Trim().TrimEnd('/');
        _token = ""; // 设置可能变过，强制重新登录
    }

    // ===================== 轮询 =====================

    private void StartPolling()
    {
        StopPolling();
        var interval = Math.Max(0, _ctx.GetSetting("PollSeconds", 60));
        if (interval == 0 || _baseUrl.Length == 0)
        {
            _ctx.Log.Info(interval == 0 ? "轮询已关闭（PollSeconds=0）" : "BaseUrl 未配置，轮询未启动");
            return;
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            // 首轮延迟 10s 等宿主就绪
            try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch { return; }
            while (!token.IsCancellationRequested)
            {
                try { await PollOnceAsync(token); }
                catch (Exception ex) { _ctx.Log.Warn($"轮询失败: {ex.Message}"); }
                try { await Task.Delay(TimeSpan.FromSeconds(interval), token); }
                catch { return; }
            }
        });
        _ctx.Log.Info($"申请列表轮询已启动（每 {interval}s）");
    }

    private void StopPolling()
    {
        try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
        _cts = null;
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (!await EnsureTokenAsync()) return;
        var r = await GetAdminApplicationsAsync(page: 1, pageSize: 20, ct);
        if (r is null) return;

        // 首轮只记基线不发通知
        var ids = r.Data.Select(d => d.Id).ToHashSet();
        if (!_initialized)
        {
            _seenIds = ids;
            _initialized = true;
            _ctx.Log.Info($"基线建立：{ids.Count} 条申请");
            return;
        }

        // 新申请（出现在列表里但没见过，排除已审核的——只关注 status=0 审核中）
        var fresh = r.Data.Where(d => d.Status == 0 && !_seenIds.Contains(d.Id)).ToList();
        _seenIds = ids;
        if (fresh.Count == 0) return;

        var target = _ctx.GetSetting("NotifyTarget", "").Trim();
        _ctx.Log.Info($"检测到 {fresh.Count} 条新申请");
        if (target.Length == 0) return;

        var sb = new StringBuilder($"[AOnline] 新申请 {fresh.Count} 条：\n");
        foreach (var a in fresh.Take(5))
            sb.AppendLine($"#{a.Id} {a.Username}（{a.CreatedAt:MM-dd HH:mm}）{(a.SteamLink.Length > 0 ? "Steam" : "截图")}流水");
        if (fresh.Count > 5) sb.AppendLine($"…等 {fresh.Count} 条");
        sb.Append("处理：登录 Web 管理端 或让管理员审核");

        // target 语法：g:<群号> / p:<QQ号> / 纯数字（>=6位按群，其余按私聊）
        var seg = OneBotLib.MessageSegment.MessageSegment.Text(sb.ToString());
        if (target.StartsWith("p:", StringComparison.OrdinalIgnoreCase) && long.TryParse(target[2..], out var qq))
            await _ctx.Api.SendPrivateMsgAsync(qq, seg);
        else if (target.StartsWith("g:", StringComparison.OrdinalIgnoreCase) && long.TryParse(target[2..], out var gid2))
            await _ctx.Api.SendGroupMsgAsync(gid2, seg);
        else if (long.TryParse(target, out var id))
        {
            if (target.Length >= 6) await _ctx.Api.SendGroupMsgAsync(id, seg);
            else await _ctx.Api.SendPrivateMsgAsync(id, seg);
        }
        else _ctx.Log.Warn($"NotifyTarget 格式无法识别: {target}（应填群号 / p:QQ号 / g:群号）");
    }

    // ===================== 认证（ALTCHA PoW 三步握手） =====================

    // 1) GET /auth/altcha-challenge  2) 本地解 PBKDF2 工作量证明 + POST /auth/altcha-verify 换 verification_token
    // 3) POST /auth/login 带 verification_token
    private async Task<bool> EnsureTokenAsync()
    {
        if (_baseUrl.Length == 0) { _ctx.Log.Warn("BaseUrl 未配置"); return false; }
        if (_token.Length > 0) return true; // 内存里有（含配置粘贴的 token，401 时会被清）

        // 优先：用户从浏览器登录响应里粘贴的 JWT（免 ALTCHA）
        var manualToken = _ctx.GetSetting("Token", "").Trim();
        if (manualToken.Length > 20)
        {
            _token = manualToken;
            _tokenAt = DateTime.Now;
            _ctx.Log.Info("使用粘贴的 JWT token");
            return true;
        }

        var user = _ctx.GetSetting("Username", "");
        var pass = _ctx.GetSetting("Password", "");
        if (user.Length == 0 || pass.Length == 0) { _ctx.Log.Warn("用户名/密码未配置，跳过"); return false; }

        try
        {
            // 1. 取挑战
            var chalResp = await _http.GetAsync($"{_baseUrl}/auth/altcha-challenge");
            var chalBody = await chalResp.Content.ReadAsStringAsync();
            if (!chalResp.IsSuccessStatusCode)
            {
                _ctx.Log.Warn($"取挑战失败 HTTP {(int)chalResp.StatusCode}: {Trunc(chalBody)}");
                return false;
            }

            // 2. 解 PoW（PBKDF2-HMAC-SHA256: password = nonce+u32be(counter)，直到 derivedKey 以 keyPrefix 开头）
            var solveStart = DateTime.Now;
            var challenge = Altcha.TryParseChallenge(chalBody);
            if (challenge is null) { _ctx.Log.Warn("挑战格式无法解析"); return false; }
            var solution = Altcha.Solve(challenge);
            _ctx.Log.Info($"ALTCHA 已解（counter={solution.Counter}，耗时 {(DateTime.Now - solveStart).TotalMilliseconds:F0}ms）");

            // 3. 校验换 verification_token（payload = base64(原挑战 JSON + solution)）
            var payload = Altcha.BuildPayload(chalBody, solution);
            var verifyResp = await _http.PostAsJsonAsync($"{_baseUrl}/auth/altcha-verify", new { payload });
            var verifyBody = await verifyResp.Content.ReadAsStringAsync();
            if (!verifyResp.IsSuccessStatusCode)
            {
                _ctx.Log.Warn($"验证失败 HTTP {(int)verifyResp.StatusCode}: {Trunc(verifyBody)}");
                return false;
            }
            using (var vd = JsonDocument.Parse(verifyBody))
            {
                var vtoken = vd.RootElement.TryGetProperty("verification_token", out var vt) ? vt.GetString() ?? "" : "";
                if (vtoken.Length == 0)
                {
                    _ctx.Log.Warn("验证响应缺 verification_token");
                    return false;
                }

                // 4. 登录
                var loginResp = await _http.PostAsJsonAsync($"{_baseUrl}/auth/login",
                    new { username = user, password = pass, verification_token = vtoken });
                var loginBody = await loginResp.Content.ReadAsStringAsync();
                if (!loginResp.IsSuccessStatusCode)
                {
                    _ctx.Log.Warn($"登录失败 HTTP {(int)loginResp.StatusCode}: {Trunc(loginBody)}");
                    _token = "";
                    return false;
                }
                using var doc = JsonDocument.Parse(loginBody);
                _token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
                _uid = doc.RootElement.TryGetProperty("uid", out var u) && u.TryGetInt64(out var uid) ? uid : 0;
                _tokenAt = DateTime.Now;
                _ctx.Log.Info($"AOnline 登录成功（uid={_uid}）");
                return _token.Length > 0;
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"登录异常: {ex.Message}");
            _token = "";
            return false;
        }
    }

    // ===================== 数据获取 =====================

    private sealed class AppPage
    {
        public int Total;
        public List<AppRow> Data = [];
    }

    private sealed class AppRow
    {
        public long Id;
        public string Username = "";
        public string SteamLink = "";
        public int Status;
        public string RejectReason = "";
        public DateTime CreatedAt;
    }

    // GET /admin/applications（管理员）；失败返回 null
    private async Task<AppPage?> GetAdminApplicationsAsync(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/admin/applications?page={page}&page_size={pageSize}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", _token);
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _ctx.Log.Warn($"拉取申请失败 HTTP {(int)resp.StatusCode}: {Trunc(body)}");
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                _token = ""; // token 失效：下次重新读设置（用户可能已粘贴新 token）
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var result = new AppPage
        {
            Total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var total) ? total : 0,
        };
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                var row = new AppRow
                {
                    Id = el.TryGetProperty("id", out var id) && id.TryGetInt64(out var idv) ? idv : 0,
                    Username = el.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "?",
                    SteamLink = el.TryGetProperty("steam_link", out var sl) ? sl.GetString() ?? "" : "",
                    Status = el.TryGetProperty("status", out var st) && st.TryGetInt32(out var stv) ? stv : -1,
                    RejectReason = el.TryGetProperty("reject_reason", out var rr) ? rr.GetString() ?? "" : "",
                    CreatedAt = el.TryGetProperty("created_at", out var ca) && DateTime.TryParse(ca.GetString(), out var cav) ? cav : DateTime.MinValue,
                };
                result.Data.Add(row);
            }
        }
        return result;
    }

    private static string StatusText(int s) => s switch
    {
        0 => "🔍审核中", 1 => "✅已通过", 2 => "❌已拒绝", _ => "?"
    };

    // ===================== 命令 =====================

    // login：手动登录验证凭据（宿主命令通道已覆盖群聊与主人私聊）
    private async Task<string> LoginCmdAsync()
    {
        _token = ""; // 强制重新登录
        if (!await EnsureTokenAsync())
            return "登录失败：检查 BaseUrl/用户名/密码设置（或服务器不可达）";
        var user = _ctx.GetSetting("Username", "");
        return $"✅ AOnline 登录成功（uid={_uid}，账号 {user}）";
    }

    // get_requests <count>：拉取最近 N 条（默认 5，最大 50）
    private async Task<string> GetRequestsCmdAsync(string args)
    {
        var count = 5;
        if (args.Trim().Length > 0 && (!int.TryParse(args.Trim(), out count) || count < 1))
            return "格式: get_requests <条数>（1-50）";
        count = Math.Min(count, 50);
        if (!await EnsureTokenAsync()) return "登录失败：先执行 login 排查或检查设置";
        var r = await GetAdminApplicationsAsync(page: 1, pageSize: count);
        if (r is null) return "拉取失败（详见插件日志）";
        return FormatRows(r, $"最近 {r.Data.Count} 条（共 {r.Total}）：");
    }

    private async Task<string> GetAllRequestsAsync()
    {
        if (!await EnsureTokenAsync()) return "登录失败：检查 BaseUrl/用户名/密码设置";
        var r = await GetAdminApplicationsAsync(page: 1, pageSize: 20);
        if (r is null) return "拉取失败（详见插件日志）";
        return FormatRows(r, $"AOnline 申请列表（共 {r.Total} 条，显示前 {r.Data.Count}）：");
    }

    private static string FormatRows(AppPage r, string header)
    {
        var sb = new StringBuilder(header + "\n");
        foreach (var a in r.Data)
        {
            var line = $"#{a.Id} {a.Username} {StatusText(a.Status)} {a.CreatedAt:MM-dd HH:mm}";
            if (a.Status == 2 && a.RejectReason.Length > 0) line += $" 拒因:{Trunc(a.RejectReason, 20)}";
            sb.AppendLine(line);
        }
        if (r.Data.Count == 0) sb.AppendLine("（无记录）");
        return sb.ToString().TrimEnd();
    }

    private static string Trunc(string s, int n = 120) =>
        s.Length <= n ? s : s[..n] + "…";
}
