using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace FlexBot;

/// <summary>
/// 消息落库（TODO 数据层 P1）：**只存「关注群」与「私聊」**，未关注群一律不落库。
///
/// 三个要点：
///   · 写入走内存队列 + 后台批量事务 —— 每条消息都 fsync 是性能陷阱（日志管线那轮已踩过）
///   · 去重靠 UNIQUE(transport, scope, message_id, ts) + INSERT OR IGNORE，
///     特意带上 ts：onebot 的 message_id 是 int32，机器人重启后可能重复
///   · 保留期到期清理（群 90 天 / 私聊 180 天），沿用 LogHousekeeping 那套"启动一次 + 周期一次"的模式
///
/// 检索（FTS5）与长期记忆表属于 P2，本类先只提供落库、统计与最近若干条（供 WebUI 只读页）。
/// </summary>
sealed class MessageStore : IDisposable
{
    /// <summary>宿主启动时创建一次；WebUI 只读页通过它取数据</summary>
    public static MessageStore? Current { get; private set; }

    public const int GroupKeepDays = 90;
    public const int PrivateKeepDays = 180;
    private const int FlushIntervalMs = 200;
    private const int FlushBatchSize = 200;
    private const int QueueSoftLimit = 20_000;

    private readonly string _dbPath;
    private readonly string _watchedFile;
    private readonly ConcurrentQueue<Row> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _flushLoop;

    private DateTime _watchedMtime = DateTime.MinValue;
    private HashSet<long> _watched = [];

    public MessageStore(string dbPath, string watchedFile)
    {
        _dbPath = dbPath;
        _watchedFile = watchedFile;
        Initialize();
        _flushLoop = Task.Run(FlushLoopAsync);
        Current = this;
        Console.WriteLine($"[history] 消息库就绪: {dbPath}");
    }

    private readonly record struct Row(
        string Transport, string Scope, long MessageId, long Timestamp,
        long UserId, string Nick, string Text, string Raw);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS msg(
          id INTEGER PRIMARY KEY,
          transport  TEXT    NOT NULL,
          scope      TEXT    NOT NULL,
          message_id INTEGER NOT NULL,
          ts         INTEGER NOT NULL,
          user_id    INTEGER NOT NULL,
          nick       TEXT    NOT NULL,
          text       TEXT    NOT NULL,
          raw_json   TEXT,
          recalled   INTEGER NOT NULL DEFAULT 0,
          UNIQUE(transport, scope, message_id, ts)
        );
        CREATE INDEX IF NOT EXISTS idx_msg_scope_ts ON msg(scope, ts);
        """;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";  // 写入不阻塞读取
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

    // ---------------- 策略：只存关注群 + 私聊 ----------------

    /// <summary>关注群名单（`memory/watched_groups.txt`，Admin/Agent 插件共用同一份文件）；按 mtime 缓存</summary>
    private HashSet<long> WatchedGroups()
    {
        try
        {
            if (!File.Exists(_watchedFile)) return _watched;
            var mtime = File.GetLastWriteTimeUtc(_watchedFile);
            if (mtime == _watchedMtime) return _watched;
            var set = new HashSet<long>();
            foreach (var line in File.ReadAllLines(_watchedFile))
                if (long.TryParse(line.Trim(), out var id) && id > 0) set.Add(id);
            _watched = set;
            _watchedMtime = mtime;
        }
        catch (Exception ex) { Console.WriteLine($"[history] 读取关注群名单失败: {ex.Message}"); }
        return _watched;
    }

    public string? ScopeFor(long? groupId, long userId) =>
        groupId is > 0 ? $"group:{groupId}" : userId > 0 ? $"private:{userId}" : null;

    public bool ShouldStore(string scope)
    {
        if (scope.StartsWith("private:", StringComparison.Ordinal)) return true;   // 私聊全存
        if (!scope.StartsWith("group:", StringComparison.Ordinal)) return false;
        return long.TryParse(scope.AsSpan(6), out var gid) && WatchedGroups().Contains(gid);
    }

    // ---------------- 写入：入队 + 后台批量事务 ----------------

    /// <summary>落库入口（不阻塞调用方）；不符合策略的直接丢弃</summary>
    public void Record(string transport, string scope, long messageId, long ts, long userId,
                       string nick, string text, string? raw)
    {
        if (_queue.Count >= QueueSoftLimit) return;      // 极端情况丢新消息，也不让内存无限涨
        if (!ShouldStore(scope)) return;
        _queue.Enqueue(new Row(transport, scope, messageId, ts, userId, nick, text ?? "", raw ?? ""));
    }

    private async Task FlushLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushIntervalMs, token).ConfigureAwait(false);
                FlushOnce();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[history] 写入失败: {ex.Message}"); }
        }
        try { FlushOnce(); } catch { }                   // 收尾：把队列里剩下的写完
    }

    private void FlushOnce()
    {
        if (_queue.IsEmpty) return;
        var batch = new List<Row>(FlushBatchSize);
        while (batch.Count < FlushBatchSize && _queue.TryDequeue(out var row)) batch.Add(row);
        if (batch.Count == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO msg(transport, scope, message_id, ts, user_id, nick, text, raw_json)
            VALUES($t, $s, $m, $ts, $u, $n, $x, $r)
            """;
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        var ps = cmd.Parameters.Add("$s", SqliteType.Text);
        var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
        var pts = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pu = cmd.Parameters.Add("$u", SqliteType.Integer);
        var pn = cmd.Parameters.Add("$n", SqliteType.Text);
        var px = cmd.Parameters.Add("$x", SqliteType.Text);
        var pr = cmd.Parameters.Add("$r", SqliteType.Text);
        var inserted = 0;
        foreach (var row in batch)
        {
            pt.Value = row.Transport; ps.Value = row.Scope; pm.Value = row.MessageId; pts.Value = row.Timestamp;
            pu.Value = row.UserId; pn.Value = row.Nick; px.Value = row.Text; pr.Value = row.Raw;
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        if (inserted > 0) Console.WriteLine($"[history] 落库 {inserted} 条（队列剩余 {_queue.Count}）");
    }

    // ---------------- 读取：统计与最近消息（只给 WebUI 只读页） ----------------

    public long DbBytes()
    {
        try
        {
            long sum = 0;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var f = new FileInfo(_dbPath + suffix);
                if (f.Exists) sum += f.Length;
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>总条数（含未落盘的队列长度）、按类型分组的条数、时间范围、库体积</summary>
    public (long Total, long Group, long Private, long Queued, long Bytes, string Oldest, string Newest) Stats()
    {
        long total = 0, group = 0, priv = 0, oldest = 0, newest = 0;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN scope LIKE 'group:%'   THEN 1 ELSE 0 END),
                       SUM(CASE WHEN scope LIKE 'private:%' THEN 1 ELSE 0 END),
                       MIN(ts), MAX(ts)
                FROM msg
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                total = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                group = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                priv = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                oldest = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                newest = r.IsDBNull(4) ? 0 : r.GetInt64(4);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[history] 统计失败: {ex.Message}"); }

        static string Fmt(long unix) => unix <= 0 ? "—"
            : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return (total, group, priv, _queue.Count, DbBytes(), Fmt(oldest), Fmt(newest));
    }

    /// <summary>某个 scope 的最近若干条（时间正序返回）</summary>
    public List<object> Recent(string scope, int limit)
    {
        var list = new List<object>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ts, user_id, nick, text, recalled FROM msg
                WHERE scope = $s ORDER BY ts DESC, id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 500));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new
                {
                    ts = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime().ToString("MM-dd HH:mm:ss"),
                    userId = r.GetInt64(1),
                    nick = r.GetString(2),
                    text = r.GetString(3),
                    recalled = r.GetInt64(4) != 0,
                });
            }
        }
        catch (Exception ex) { Console.WriteLine($"[history] 读取最近消息失败: {ex.Message}"); }
        list.Reverse();
        return list;
    }

    /// <summary>已知 scope 列表（消息库里出现过的）</summary>
    public List<(string Scope, long Count, long Last)> Scopes()
    {
        var list = new List<(string, long, long)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT scope, COUNT(*), MAX(ts) FROM msg GROUP BY scope ORDER BY MAX(ts) DESC LIMIT 200";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        }
        catch (Exception ex) { Console.WriteLine($"[history] 列出会话失败: {ex.Message}"); }
        return list;
    }

    // ---------------- 保留期清理 ----------------

    /// <summary>按保留期删除过期消息，返回删除条数。启动时与周期任务各调一次。</summary>
    public int Purge()
    {
        var removed = 0;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var groupCut = now - (long)GroupKeepDays * 86400;
            var privCut = now - (long)PrivateKeepDays * 86400;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM msg WHERE (scope LIKE 'group:%' AND ts < $g) OR (scope LIKE 'private:%' AND ts < $p)";
            cmd.Parameters.AddWithValue("$g", groupCut);
            cmd.Parameters.AddWithValue("$p", privCut);
            removed = cmd.ExecuteNonQuery();
            if (removed > 0)
            {
                using var vacuum = conn.CreateCommand();
                vacuum.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                vacuum.ExecuteNonQuery();
                Console.WriteLine($"[history] 保留期清理删除 {removed} 条（群 {GroupKeepDays} 天 / 私聊 {PrivateKeepDays} 天）");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[history] 清理失败: {ex.Message}"); }
        return removed;
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            try { _flushLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            Current = null;
            _cts.Dispose();
        }
        catch { }
    }
}
