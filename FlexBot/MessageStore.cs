using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace FlexBot;

/// <summary>
/// 消息落库（TODO 数据层 P1）：**只存「关注群」与「私聊」**，未关注群一律不落库。
/// 双向都存：`dir='in'` 是别人说的、`dir='out'` 是机器人自己发的 —— 只看一半就没法回答
/// "机器人当时到底回了什么"。
///
/// 四个要点：
///   · 写入走内存队列 + 后台批量事务 —— 每条消息都 fsync 是性能陷阱（日志管线那轮已踩过）
///   · 去重靠 UNIQUE(transport, scope, message_id, ts) + INSERT OR IGNORE；
///     特意带上 ts：onebot 的 message_id 是 int32，机器人重启后可能重复
///   · 出站消息发起时还不知道真实 message_id（要等通道返回），用负的自增序号占位，
///     既避免"同一秒发多条"被唯一键吞掉，也不至于和真实 id 冲突
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
    private long _syntheticId;                 // 出站消息的真实 id 未知时的占位（负数，避免与真实 id 冲突）

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
        string Transport, string Scope, string Dir, long MessageId, long Timestamp,
        long UserId, string Nick, string Text, string Raw);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS msg(
          id INTEGER PRIMARY KEY,
          transport  TEXT    NOT NULL,
          scope      TEXT    NOT NULL,
          dir        TEXT    NOT NULL DEFAULT 'in',
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
        -- 检索索引：contentless FTS5，只存二元切分后的文本（中文 2 字词也能命中；trigram 要求 ≥3 字，不适用）
        CREATE VIRTUAL TABLE IF NOT EXISTS msg_fts USING fts5(seg, content='');
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
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        BackfillIndex();
        // 迁移：P1 早先的库没有 dir 列（那时只记入站）
        try
        {
            using var migrate = conn.CreateCommand();
            migrate.CommandText = "ALTER TABLE msg ADD COLUMN dir TEXT NOT NULL DEFAULT 'in'";
            migrate.ExecuteNonQuery();
            Console.WriteLine("[history] 已为既有消息库补上 dir 列（历史数据视为入站）");
        }
        catch { /* 列已存在 */ }
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

    /// <summary>入站消息落库（不阻塞调用方）；不符合策略的直接丢弃</summary>
    public void RecordIn(string transport, string scope, long messageId, long ts, long userId,
                         string nick, string text, string? raw)
        => Enqueue(new Row(transport, scope, "in", messageId, ts, userId, nick, text ?? "", raw ?? ""));

    /// <summary>
    /// 出站消息落库（机器人在群里/私聊里说的话）。发起时拿不到真实 message_id，
    /// 用负数占位序号保证唯一性；因此这里记的是**发起动作**，不保证通道一定送达。
    /// </summary>
    public void RecordOut(string scope, long ts, string text)
        => Enqueue(new Row(Transport, scope, "out", -Interlocked.Increment(ref _syntheticId), ts,
                           botId, "机器人", text ?? "", ""));

    private long botId;
    /// <summary>机器人自身 QQ 号（用于 out 行的 user_id；通道连上后由 Program 设置）</summary>
    public long BotId { get => botId; set => botId = value; }

    /// <summary>当前通道名（onebot / official），由 Program 设置</summary>
    public string Transport { get; set; } = "onebot";

    private void Enqueue(Row row)
    {
        if (_queue.Count >= QueueSoftLimit) return;      // 极端情况丢新消息，也不让内存无限涨
        if (!ShouldStore(row.Scope)) return;
        _queue.Enqueue(row);
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
            INSERT OR IGNORE INTO msg(transport, scope, dir, message_id, ts, user_id, nick, text, raw_json)
            VALUES($t, $s, $d, $m, $ts, $u, $n, $x, $r)
            """;
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        var ps = cmd.Parameters.Add("$s", SqliteType.Text);
        var pd = cmd.Parameters.Add("$d", SqliteType.Text);
        var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
        var pts = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pu = cmd.Parameters.Add("$u", SqliteType.Integer);
        var pn = cmd.Parameters.Add("$n", SqliteType.Text);
        var px = cmd.Parameters.Add("$x", SqliteType.Text);
        var pr = cmd.Parameters.Add("$r", SqliteType.Text);
        // FTS 索引与正文同事务维护：INSERT OR IGNORE 被唯一键挡掉时 changes() 为 0，索引也不会多写
        using var fts = conn.CreateCommand();
        fts.Transaction = tx;
        fts.CommandText = "INSERT INTO msg_fts(rowid, seg) SELECT last_insert_rowid(), $g WHERE changes() > 0";
        var pg = fts.Parameters.Add("$g", SqliteType.Text);

        var inserted = 0;
        foreach (var row in batch)
        {
            pt.Value = row.Transport; ps.Value = row.Scope; pd.Value = row.Dir; pm.Value = row.MessageId;
            pts.Value = row.Timestamp; pu.Value = row.UserId; pn.Value = row.Nick; px.Value = row.Text; pr.Value = row.Raw;
            inserted += cmd.ExecuteNonQuery();
            if (inserted > 0) { pg.Value = Segment(row.Text); fts.ExecuteNonQuery(); }
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

    /// <summary>总条数（含未落盘的队列长度）、按类型与方向分组、时间范围、库体积</summary>
    public (long Total, long Group, long Private, long In, long Out, long Queued, long Bytes, string Oldest, string Newest) Stats()
    {
        long total = 0, group = 0, priv = 0, inbound = 0, outbound = 0, oldest = 0, newest = 0;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN scope LIKE 'group:%'   THEN 1 ELSE 0 END),
                       SUM(CASE WHEN scope LIKE 'private:%' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN dir = 'out' THEN 1 ELSE 0 END),
                       MIN(ts), MAX(ts)
                FROM msg
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                total = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                group = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                priv = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                outbound = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                inbound = total - outbound;
                oldest = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                newest = r.IsDBNull(5) ? 0 : r.GetInt64(5);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[history] 统计失败: {ex.Message}"); }

        static string Fmt(long unix) => unix <= 0 ? "—"
            : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return (total, group, priv, inbound, outbound, _queue.Count, DbBytes(), Fmt(oldest), Fmt(newest));
    }

    /// <summary>
    /// 一行历史消息。**必须用具名类型**：早先返回 List&lt;object&gt; 装匿名类型，
    /// System.Text.Json 按声明类型 object 序列化，每个元素会变成 {}（前端因此显示空白）。
    /// </summary>
    public sealed record HistoryRow(string Ts, string Dir, long UserId, string Nick, string Text, bool Recalled);

    /// <summary>某个 scope 的最近若干条（时间正序返回）</summary>
    public List<HistoryRow> Recent(string scope, int limit)
    {
        var list = new List<HistoryRow>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ts, dir, user_id, nick, text, recalled FROM msg
                WHERE scope = $s ORDER BY ts DESC, id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 500));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HistoryRow(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime().ToString("MM-dd HH:mm:ss"),
                    r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5) != 0));
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


    // ---------------- 检索：二元切分 + FTS5（P2） ----------------

    /// <summary>
    /// 中文按二元切分（重叠 bigram）、ASCII 按词 —— 不依赖任何分词器。
    /// 为什么不用 FTS5 的 trigram：它要求查询至少 3 个字符，而中文常用词多是 2 字（"基金""投资"），
    /// 直接搜不到；bigram 方案对 2 字词同样有效。
    /// </summary>
    internal static string Segment(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder();
        var cjk = new System.Text.StringBuilder();
        void Flush()
        {
            var run = cjk.ToString();
            cjk.Clear();
            if (run.Length == 0) return;
            if (run.Length == 1) { sb.Append(run).Append(' '); return; }
            for (var i = 0; i + 1 < run.Length; i++) sb.Append(run[i]).Append(run[i + 1]).Append(' ');
        }
        foreach (var ch in s)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) { cjk.Append(ch); continue; }
            Flush();
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        Flush();
        return sb.ToString();
    }

    /// <summary>把查询串转成 FTS 表达式（多个片段用 AND 连接）</summary>
    private static string ToMatchQuery(string query)
    {
        var parts = Segment(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "" : string.Join(" AND ", parts);
    }

    /// <summary>为还没有索引的行补建 FTS（首次引入检索、或库是从 P1 升上来的）</summary>
    private void BackfillIndex()
    {
        try
        {
            using var conn = Open();
            long indexed;
            using (var c = conn.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM msg_fts"; indexed = Convert.ToInt64(c.ExecuteScalar() ?? 0L); }
            long total;
            using (var c = conn.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM msg"; total = Convert.ToInt64(c.ExecuteScalar() ?? 0L); }
            if (indexed >= total) return;

            var rows = new List<(long Id, string Text)>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT m.id, m.text FROM msg m WHERE m.id NOT IN (SELECT rowid FROM msg_fts)";
                using var r = c.ExecuteReader();
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
            }
            if (rows.Count == 0) return;

            using var tx = conn.BeginTransaction();
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO msg_fts(rowid, seg) VALUES($id, $g)";
            var pid = ins.Parameters.Add("$id", SqliteType.Integer);
            var pg = ins.Parameters.Add("$g", SqliteType.Text);
            foreach (var (id, text) in rows) { pid.Value = id; pg.Value = Segment(text); ins.ExecuteNonQuery(); }
            tx.Commit();
            Console.WriteLine($"[history] 已为 {rows.Count} 条历史消息补建检索索引");
        }
        catch (Exception ex) { Console.WriteLine($"[history] 建索引失败: {ex.Message}"); }
    }

    /// <summary>在指定 scope 内检索（scope 由宿主强制，调用方无法扩大范围）</summary>
    public List<HistoryRow> Search(string scope, string query, int limit)
    {
        var list = new List<HistoryRow>();
        var match = ToMatchQuery(query);
        if (match.Length == 0) return list;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT m.ts, m.dir, m.user_id, m.nick, m.text, m.recalled
                FROM msg_fts f JOIN msg m ON m.id = f.rowid
                WHERE msg_fts MATCH $q AND m.scope = $s
                ORDER BY rank LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$q", match);
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 200));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HistoryRow(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime().ToString("MM-dd HH:mm:ss"),
                    r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5) != 0));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[history] 检索失败: {ex.Message}"); }
        return list;
    }

    /// <summary>某 scope 最新一条消息之前（不含）的若干条 —— 供"重启后补上下文"用</summary>
    public List<HistoryRow> Before(string scope, long beforeTs, int limit)
    {
        var list = new List<HistoryRow>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT ts, dir, user_id, nick, text, recalled FROM msg
                WHERE scope = $s AND ts < $t ORDER BY ts DESC, id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$t", beforeTs);
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 200));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HistoryRow(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime().ToString("MM-dd HH:mm"),
                    r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5) != 0));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[history] 读取更早消息失败: {ex.Message}"); }
        list.Reverse();
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
                using var checkpoint = conn.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
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
