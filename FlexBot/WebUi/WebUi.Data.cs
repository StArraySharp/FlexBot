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

/// <summary>② 端点分模块：数据类端点（历史消息 / AI 用量 / 系统指标）。纯搬移；settings 改为参数传入。</summary>
internal static partial class WebUi
{
    private static void MapDataEndpoints(WebApplication app, HostSettings settings)
    {
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


// AI 用量（认证）：对照 AstrBot 的 provider_stats，只统计 token 不折算费用
app.MapGet("/api/usage", () =>
{
    var u = UsageStore.Current;
    if (u is null) return Results.Json(new { enabled = false });
    var (calls, inTok, outTok, cached, todayCalls, todayIn, todayOut, avgMs) = u.Summary();
    return Results.Json(new
    {
        enabled = true,
        calls, inTok, outTok, cached,
        todayCalls, todayIn, todayOut,
        avgMs,
        byModel = u.ByModel().Select(m => new { model = m.Model, calls = m.Calls, inTok = m.In, outTok = m.Out }),
    });
});

// 系统指标（认证）：磁盘可用、进程内存、库体积
app.MapGet("/api/system", () =>
{
    long diskFree = 0, diskTotal = 0;
    try
    {
        var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";
        var di = new DriveInfo(root);
        diskFree = di.AvailableFreeSpace;
        diskTotal = di.TotalSize;
    }
    catch { }
    var proc = System.Diagnostics.Process.GetCurrentProcess();
    long dbBytes = 0;
    foreach (var p in new[] { Path.Combine(settings.MemoryDir, "flexbot.db"), Path.Combine(settings.MemoryDir, "flexbot.db-wal") })
        try { if (File.Exists(p)) dbBytes += new FileInfo(p).Length; } catch { }
    return Results.Json(new
    {
        diskFreeMb = diskFree / 1024 / 1024,
        diskTotalMb = diskTotal / 1024 / 1024,
        memWorkingSetMb = proc.WorkingSet64 / 1024 / 1024,
        memGcMb = GC.GetTotalMemory(false) / 1024 / 1024,
        threads = proc.Threads.Count,
        uptimeSec = (long)(DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds,
        dbBytes,
    });
});
    }
}
