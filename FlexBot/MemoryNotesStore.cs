using FlexBot.PluginApi;
using Microsoft.Data.Sqlite;

namespace FlexBot;

/// <summary>
/// 长期记忆条目（P3 尾巴的存储半）：与消息库共用一个 SQLite 文件，独立表 mem。
/// 去重键 (scope, text)：同一句话被反复总结出来时只更新重要度与时间，不重复堆积。
/// </summary>
sealed class MemoryNotesStore : IMemoryNotes, IDisposable
{
    public static MemoryNotesStore? Current { get; private set; }

    private readonly string _dbPath;
    private readonly Lock _gate = new();

    public MemoryNotesStore(string dbPath)
    {
        _dbPath = dbPath;
        Initialize();
        Current = this;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var p = conn.CreateCommand();
        p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        p.ExecuteNonQuery();
        return conn;
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS mem(
          id INTEGER PRIMARY KEY,
          scope TEXT NOT NULL,
          kind TEXT NOT NULL,
          text TEXT NOT NULL,
          importance INTEGER NOT NULL DEFAULT 3,
          tags TEXT NOT NULL DEFAULT '',
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          UNIQUE(scope, text)
        );
        CREATE INDEX IF NOT EXISTS idx_mem_scope ON mem(scope, importance DESC, updated_at DESC);
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

    public void Add(string scope, string kind, string text, int importance, string tags)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                // 已存在则只抬重要度、更新时间（保留首次的 kind/tags，避免反复覆盖）
                cmd.CommandText = """
                    INSERT INTO mem(scope, kind, text, importance, tags, created_at, updated_at)
                    VALUES($s, $k, $t, $i, $g, $now, $now)
                    ON CONFLICT(scope, text) DO UPDATE SET
                      importance = MAX(mem.importance, excluded.importance),
                      updated_at = excluded.updated_at
                    """;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                cmd.Parameters.AddWithValue("$s", scope);
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$t", text.Trim());
                cmd.Parameters.AddWithValue("$i", Math.Clamp(importance, 1, 5));
                cmd.Parameters.AddWithValue("$g", tags ?? "");
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Console.WriteLine($"[mem] 写入失败: {ex.Message}"); }
    }

    public IReadOnlyList<MemoryNote> List(string scope, int limit = 50)
    {
        var list = new List<MemoryNote>();
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT kind, text, importance, tags FROM mem
                    WHERE scope = $s ORDER BY importance DESC, updated_at DESC LIMIT $n
                    """;
                cmd.Parameters.AddWithValue("$s", scope);
                cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 500));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new MemoryNote(r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetString(3)));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[mem] 读取失败: {ex.Message}"); }
        return list;
    }

    public void Dispose() => Current = null;
}
