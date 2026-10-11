using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlexBot.OfficialBot;

/// <summary>
/// 官方通道的身份映射。官方只给 openid（字符串/Guid，且每个机器人各不相同），而宿主全链路
/// （OwnerUin 权限、管理员白名单、Luck 每人每天、Agent 记忆、统计）都按 long 身份工作；
/// 这里把 openid 稳定映射成合成 long，对既有逻辑完全透明，并支持可选绑定真实 QQ 号。
/// 持久化到 &lt;MemoryDir&gt;/official/idmap.json，重启后保持一致。
/// </summary>
sealed class IdMap(string path)
{
    private readonly Dictionary<string, long> _forward = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _back = new();
    private readonly Dictionary<long, long> _bound = new();     // 合成 id → 真实 QQ 号（!bind 绑定）
    private readonly Lock _gate = new();

    /// <summary>合成 id 上限 2^52：留出余量让 WebUI 的 JS Number 精确表示（避免前端丢精度）</summary>
    private const long Mask = (1L << 52) - 1;

    public static IdMap Load(string path)
    {
        var m = new IdMap(path);
        try
        {
            if (!File.Exists(path)) return m;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("forward", out var f))
                foreach (var p in f.EnumerateObject())
                    if (long.TryParse(p.Value.ToString(), out var v)) { m._forward[p.Name] = v; m._back[v] = p.Name; }
            if (doc.RootElement.TryGetProperty("bound", out var b))
                foreach (var p in b.EnumerateObject())
                    if (long.TryParse(p.Name, out var k) && long.TryParse(p.Value.ToString(), out var v)) m._bound[k] = v;
        }
        catch { /* 映射文件坏了就重建，不阻塞启动 */ }
        return m;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                forward = _forward,
                bound = _bound
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
        }
        catch { }
    }

    /// <summary>openid → 合成 long（同一 openid 永远同一 id；群用 "g:" 前缀、用户用 "u:" 前缀避免撞车）</summary>
    public long ToLong(string openId)
    {
        lock (_gate)
        {
            if (_forward.TryGetValue(openId, out var id)) return id;
            var h = SHA256.HashData(Encoding.UTF8.GetBytes(openId));
            id = (BitConverter.ToInt64(h, 0) & Mask);
            if (id == 0) id = 1;
            while (_back.ContainsKey(id)) id = (id + 1) & Mask;   // 极端碰撞：线性探测
            _forward[openId] = id;
            _back[id] = openId;
            Save();
            return id;
        }
    }

    /// <summary>合成 id → openid（发消息时反查）；也接受直接传真实 QQ 号（绑定过的话）</summary>
    public bool TryOpenId(long id, out string openId)
    {
        lock (_gate)
        {
            if (_back.TryGetValue(id, out openId!)) return true;
            foreach (var (sid, bound) in _bound)
                if (bound == id && _back.TryGetValue(sid, out var o)) { openId = o; return true; }
        }
        openId = "";
        return false;
    }

    /// <summary>绑定真实 QQ 号（官方下拿不到 QQ 号，绑了才能让 OwnerUin 那套权限/统计生效）</summary>
    public void Bind(long syntheticId, long qq)
    {
        lock (_gate) { _bound[syntheticId] = qq; Save(); }
    }

    /// <summary>该身份的对外展示/权限判定用 id：绑定过就是 QQ 号，否则是合成 id</summary>
    public long Resolve(long syntheticId)
    {
        lock (_gate) return _bound.TryGetValue(syntheticId, out var qq) ? qq : syntheticId;
    }

    /// <summary>反查：真实 QQ 号 → openid（主动发私聊时用）</summary>
    public bool TryOpenIdOfQq(long qq, out string openId)
    {
        lock (_gate)
        {
            foreach (var (sid, bound) in _bound)
                if (bound == qq && _back.TryGetValue(sid, out var o)) { openId = o; return true; }
        }
        openId = "";
        return false;
    }
}
