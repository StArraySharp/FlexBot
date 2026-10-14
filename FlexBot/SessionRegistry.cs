using Microsoft.Data.Sqlite;

namespace FlexBot;

/// <summary>
/// 会话注册表（P4）：给每个会话一个稳定短 id，并支持把多个会话放进同一个「记忆组」共享记忆。
///
/// 为什么：会话原本以裸 scope 字符串（group:123 / private:456）为身份，群改组/换号后难迁移、
/// 也无法表达"某人的私聊与该群共享记忆"。参考项目 AstrBot 的做法是
/// unified_msg_origin（platform:type:session）+ conversation_id(uuid) + switch_conversation，
/// 这里取"稳定 id + 记忆组"的核心，不引入会话切换（宿主里一个 scope 永远对应一个会话）。
///
/// 安全：把 private 关联进 group **等于把私聊内容暴露给该群**，所以：
///   · 只允许管理员命令操作（宿主侧已按管理员门禁）
///   · link 时若涉及 private 必须显式加 --yes（见命令实现）
///   · 全部操作写审计表
/// 检索时由宿主按"当前会话 → 记忆组"解析出过滤集合，模型/用户无权指定 scope。
/// </summary>
sealed class SessionRegistry : IDisposable
{
    public static SessionRegistry? Current { get; private set; }

    private readonly string _dbPath;
    private readonly Lock _gate = new();

    public SessionRegistry(string dbPath)
    {
        _dbPath = dbPath;
        Initialize();
        Current = this;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS session(
          id          TEXT PRIMARY KEY,
          scope       TEXT NOT NULL UNIQUE,
          alias       TEXT NOT NULL DEFAULT '',
          cluster_id  TEXT NOT NULL,
          created_at  INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS cluster(
          id         TEXT PRIMARY KEY,
          name       TEXT NOT NULL DEFAULT '',
          note       TEXT NOT NULL DEFAULT '',
          created_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS audit(
          id     INTEGER PRIMARY KEY,
          ts     INTEGER NOT NULL,
          actor  TEXT NOT NULL,
          action TEXT NOT NULL,
          detail TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit(ts);
        """;

    private void Initialize()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    /// <summary>确保某会话已登记（新会话自动分配 id 并独占一个记忆组）</summary>
    public void Ensure(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return;
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using (var check = conn.CreateCommand())
                {
                    check.CommandText = "SELECT 1 FROM session WHERE scope = $s";
                    check.Parameters.AddWithValue("$s", scope);
                    if (check.ExecuteScalar() is not null) return;
                }
                var cid = NewId("c_");
                var sid = NewId("s_");
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using var tx = conn.BeginTransaction();
                using (var c1 = conn.CreateCommand())
                {
                    c1.Transaction = tx;
                    c1.CommandText = "INSERT OR IGNORE INTO cluster(id, name, created_at) VALUES($i, $n, $t)";
                    c1.Parameters.AddWithValue("$i", cid);
                    c1.Parameters.AddWithValue("$n", scope);
                    c1.Parameters.AddWithValue("$t", now);
                    c1.ExecuteNonQuery();
                }
                using (var c2 = conn.CreateCommand())
                {
                    c2.Transaction = tx;
                    c2.CommandText = "INSERT OR IGNORE INTO session(id, scope, cluster_id, created_at) VALUES($i, $s, $c, $t)";
                    c2.Parameters.AddWithValue("$i", sid);
                    c2.Parameters.AddWithValue("$s", scope);
                    c2.Parameters.AddWithValue("$c", cid);
                    c2.Parameters.AddWithValue("$t", now);
                    c2.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        catch (Exception ex) { Console.WriteLine($"[session] 登记失败: {ex.Message}"); }
    }

    public void Audit(string action, string detail, string actor = "admin")
    {
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO audit(ts, actor, action, detail) VALUES($t, $a, $k, $d)";
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$a", actor);
                cmd.Parameters.AddWithValue("$k", action);
                cmd.Parameters.AddWithValue("$d", detail);
                cmd.ExecuteNonQuery();
            }
            Console.WriteLine($"[session] 审计 {action}: {detail}");
        }
        catch (Exception ex) { Console.WriteLine($"[session] 审计写入失败: {ex.Message}"); }
    }

    private (string Id, string Scope, string Alias, string ClusterId)? Find(string key)
    {
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, scope, alias, cluster_id FROM session WHERE id = $k OR scope = $k LIMIT 1";
                cmd.Parameters.AddWithValue("$k", key.Trim());
                using var r = cmd.ExecuteReader();
                if (r.Read()) return (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[session] 查询失败: {ex.Message}"); }
        return null;
    }

    public List<(string Id, string Scope, string Alias, string ClusterId, long Created)> List()
    {
        var list = new List<(string, string, string, string, long)>();
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, scope, alias, cluster_id, created_at FROM session ORDER BY created_at";
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[session] 列表失败: {ex.Message}"); }
        return list;
    }

    /// <summary>某会话可见的全部 scope（记忆组内成员；未登记时退化为自身）</summary>
    public List<string> ScopesFor(string scope)
    {
        var list = new List<string> { scope };
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                string? cluster = null;
                using (var q = conn.CreateCommand())
                {
                    q.CommandText = "SELECT cluster_id FROM session WHERE scope = $s";
                    q.Parameters.AddWithValue("$s", scope);
                    cluster = q.ExecuteScalar() as string;
                }
                if (cluster is null) return list;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT scope FROM session WHERE cluster_id = $c";
                cmd.Parameters.AddWithValue("$c", cluster);
                using var r = cmd.ExecuteReader();
                var merged = new List<string>();
                while (r.Read())
                {
                    var sc = r.GetString(0);
                    if (!merged.Contains(sc)) merged.Add(sc);
                }
                if (merged.Count > 0 && !merged.Contains(scope)) merged.Add(scope);
                if (merged.Count > 0) return merged;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[session] 解析记忆组失败: {ex.Message}"); }
        return list;
    }

    /// <summary>把两个会话放进同一个记忆组（后者并入前者的组）</summary>
    public string Link(string keyA, string keyB, bool allowPrivate)
    {
        var a = Find(keyA);
        var b = Find(keyB);
        if (a is null) return $"找不到会话：{keyA}";
        if (b is null) return $"找不到会话：{keyB}";
        if (a.Value.ClusterId == b.Value.ClusterId) return "它们已经在同一个记忆组里了。";

        var involvesPrivate = a.Value.Scope.StartsWith("private:") || b.Value.Scope.StartsWith("private:");
        if (involvesPrivate && !allowPrivate)
            return "⚠ 涉及私聊的关联会把私聊内容暴露给关联方。确认请加 --yes 重试（会自动写审计）。";

        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE session SET cluster_id = $c WHERE cluster_id = $old";
                cmd.Parameters.AddWithValue("$c", a.Value.ClusterId);
                cmd.Parameters.AddWithValue("$old", b.Value.ClusterId);
                var moved = cmd.ExecuteNonQuery();
                using var clean = conn.CreateCommand();
                clean.Transaction = tx;
                clean.CommandText = "DELETE FROM cluster WHERE id = $old AND id <> $keep";
                clean.Parameters.AddWithValue("$old", b.Value.ClusterId);
                clean.Parameters.AddWithValue("$keep", a.Value.ClusterId);
                clean.ExecuteNonQuery();
                tx.Commit();
                Audit("session.link", $"{a.Value.Scope} <- {b.Value.Scope}（并入 {moved} 个会话）");
            }
            return $"已关联：{b.Value.Scope} 并入 {a.Value.Scope} 的记忆组。此后两边共享记忆。";
        }
        catch (Exception ex) { return $"关联失败：{ex.Message}"; }
    }

    /// <summary>把某会话移出记忆组，恢复独占</summary>
    public string Unlink(string key)
    {
        var s = Find(key);
        if (s is null) return $"找不到会话：{key}";
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                var cid = NewId("c_");
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using var tx = conn.BeginTransaction();
                using (var c1 = conn.CreateCommand())
                {
                    c1.Transaction = tx;
                    c1.CommandText = "INSERT INTO cluster(id, name, created_at) VALUES($i, $n, $t)";
                    c1.Parameters.AddWithValue("$i", cid);
                    c1.Parameters.AddWithValue("$n", s.Value.Scope);
                    c1.Parameters.AddWithValue("$t", now);
                    c1.ExecuteNonQuery();
                }
                using (var c2 = conn.CreateCommand())
                {
                    c2.Transaction = tx;
                    c2.CommandText = "UPDATE session SET cluster_id = $c WHERE id = $i";
                    c2.Parameters.AddWithValue("$c", cid);
                    c2.Parameters.AddWithValue("$i", s.Value.Id);
                    c2.ExecuteNonQuery();
                }
                tx.Commit();
                Audit("session.unlink", s.Value.Scope);
            }
            return $"已分离：{s.Value.Scope} 恢复独占记忆组。历史数据按原始 scope 保留，分离后互不可见。";
        }
        catch (Exception ex) { return $"分离失败：{ex.Message}"; }
    }

    public string Rename(string key, string alias)
    {
        var s = Find(key);
        if (s is null) return $"找不到会话：{key}";
        alias = alias.Trim();
        if (alias.Length == 0) return "别名不能为空。";
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE session SET alias = $a WHERE id = $i";
                cmd.Parameters.AddWithValue("$a", alias);
                cmd.Parameters.AddWithValue("$i", s.Value.Id);
                cmd.ExecuteNonQuery();
            }
            Audit("session.rename", $"{s.Value.Scope} → {alias}");
            return $"已把 {s.Value.Id}（{s.Value.Scope}）的别名设为「{alias}」。";
        }
        catch (Exception ex) { return $"改名失败：{ex.Message}"; }
    }

    /// <summary>
    /// 处理 session 命令（宿主聊天分发与 WebUI /api/command 共用同一份实现，避免两处走偏）。
    /// 返回 null 表示不是 session 命令。
    /// </summary>
    public static string? HandleCommand(string args)
    {
        var reg = Current;
        if (reg is null) return "会话注册表未启用";
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] is "list") return reg.Describe();
        switch (parts[0])
        {
            case "info" when parts.Length >= 2: return reg.Describe();
            case "link" when parts.Length >= 3: return reg.Link(parts[1], parts[2], allowPrivate: parts.Contains("--yes"));
            case "link": return "用法: session link <会话A> <会话B> [--yes]（涉及私聊必须加 --yes）";
            case "unlink" when parts.Length >= 2: return reg.Unlink(parts[1]);
            case "rename" when parts.Length >= 3: return reg.Rename(parts[1], string.Join(" ", parts.Skip(2)));
            default: return "用法: session list | link <A> <B> [--yes] | unlink <会话> | rename <会话> <别名>";
        }
    }

    public void Dispose() => Current = null;

    /// <summary>会话概览文本（管理员命令用）</summary>
    public string Describe()
    {
        var all = List();
        if (all.Count == 0) return "还没有登记任何会话（有群/私聊消息往来后会自动登记）。";
        var sb = new System.Text.StringBuilder();
        var byCluster = all.GroupBy(x => x.ClusterId);
        foreach (var g in byCluster)
        {
            var names = g.Select(x => string.IsNullOrEmpty(x.Alias) ? x.Scope : $"{x.Scope}({x.Alias})");
            sb.AppendLine($"[{g.Key}] " + string.Join(" + ", names));
        }
        sb.AppendLine($"共 {all.Count} 个会话，{byCluster.Count()} 个记忆组");
        return sb.ToString().TrimEnd();
    }
}
