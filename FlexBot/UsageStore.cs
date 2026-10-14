using FlexBot.PluginApi;
using Microsoft.Data.Sqlite;

namespace FlexBot;

/// <summary>
/// AI 用量统计（对照参考项目 AstrBot 的 provider_stats：按次记录 provider/model/
/// 输入 tokens/输出 tokens/缓存命中 tokens/耗时）。
///
/// 与消息库共用同一个 SQLite 文件（单文件便于备份与查看），但表独立、连接独立、写入开销极低
/// （每条 AI 回复一行），因此直接写、不做批量。
/// 只统计 token，不折算费用 —— 费用需要各模型的单价表，配置里没有，硬编码会失真。
/// </summary>
sealed class UsageStore : FlexBot.PluginApi.IUsageSink, IDisposable
{
    public static UsageStore? Current { get; private set; }

    private readonly string _dbPath;
    private readonly Lock _gate = new();

    public UsageStore(string dbPath)
    {
        _dbPath = dbPath;
        Initialize();
        Current = this;
    }

    private readonly record struct UsageRow(
        long Timestamp, string Provider, string Model,
        long InputTokens, long OutputTokens, long CachedTokens, long ElapsedMs);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS usage(
          id INTEGER PRIMARY KEY,
          ts INTEGER NOT NULL,
          provider TEXT NOT NULL,
          model TEXT NOT NULL,
          tokens_in INTEGER NOT NULL DEFAULT 0,
          tokens_out INTEGER NOT NULL DEFAULT 0,
          tokens_cached INTEGER NOT NULL DEFAULT 0,
          elapsed_ms INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_usage_ts ON usage(ts);
        """;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    /// <summary>记一次 AI 调用（失败也算一次，token 为 0）</summary>
    public void Record(string provider, string model, long inputTokens, long outputTokens, long cachedTokens, long elapsedMs)
    {
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO usage(ts, provider, model, tokens_in, tokens_out, tokens_cached, elapsed_ms)
                    VALUES($ts, $p, $m, $i, $o, $c, $e)
                    """;
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$p", provider ?? "");
                cmd.Parameters.AddWithValue("$m", model ?? "");
                cmd.Parameters.AddWithValue("$i", inputTokens);
                cmd.Parameters.AddWithValue("$o", outputTokens);
                cmd.Parameters.AddWithValue("$c", cachedTokens);
                cmd.Parameters.AddWithValue("$e", elapsedMs);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Console.WriteLine($"[usage] 记录失败: {ex.Message}"); }
    }

    /// <summary>累计用量：总次数/输入/输出/缓存，以及今日与近 7 天</summary>
    public (long Calls, long In, long Out, long Cached, long TodayCalls, long TodayIn, long TodayOut, long RecentCallMs) Summary()
    {
        long calls = 0, inTok = 0, outTok = 0, cached = 0, todayCalls = 0, todayIn = 0, todayOut = 0, avgMs = 0;
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                var todayStart = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).ToUnixTimeSeconds();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT COUNT(*), COALESCE(SUM(tokens_in),0), COALESCE(SUM(tokens_out),0), COALESCE(SUM(tokens_cached),0)
                        FROM usage
                        """;
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        calls = r.GetInt64(0); inTok = r.GetInt64(1); outTok = r.GetInt64(2); cached = r.GetInt64(3);
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT COUNT(*), COALESCE(SUM(tokens_in),0), COALESCE(SUM(tokens_out),0)
                        FROM usage WHERE ts >= $t
                        """;
                    cmd.Parameters.AddWithValue("$t", todayStart);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        todayCalls = r.GetInt64(0); todayIn = r.GetInt64(1); todayOut = r.GetInt64(2);
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(AVG(elapsed_ms),0) FROM usage";
                    avgMs = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[usage] 统计失败: {ex.Message}"); }
        return (calls, inTok, outTok, cached, todayCalls, todayIn, todayOut, avgMs);
    }

    /// <summary>按模型聚合（次数/输入/输出），用于看清哪个模型在烧 token</summary>
    public List<(string Model, long Calls, long In, long Out)> ByModel()
    {
        var list = new List<(string, long, long, long)>();
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT model, COUNT(*), COALESCE(SUM(tokens_in),0), COALESCE(SUM(tokens_out),0)
                    FROM usage GROUP BY model ORDER BY SUM(tokens_out) DESC LIMIT 20
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[usage] 按模型统计失败: {ex.Message}"); }
        return list;
    }

    public void Dispose() => Current = null;
}
