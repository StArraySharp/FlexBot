using System.Text;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;

namespace FileSystemPlugin;

/// <summary>
/// 沙箱文件操作插件：fs_read/fs_write/fs_append/fs_list/fs_delete/fs_mkdir/fs_info/fs_download
/// 一切操作限制在沙箱根目录内（默认 memory/fs），路径越界一律拒绝。
/// 命令仅管理员可触发（宿主分发保证 + 工具侧 IsOwner 双保险）。
/// </summary>
public sealed class FileSystemPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;
    private string _root = "";
    private long _maxFileKb;
    private long _maxTotalMb;
    private string _pendingRoot = "";
    private bool _rootChangeRequested = false;
    private DateTime _rootChangeTimestamp = DateTime.MinValue;
    private const int RootChangeTimeoutSec = 300; // 5分钟超时

    public string Name => "FileSystem";
    public string Version => "1.0.0";
    public string Description => "沙箱文件操作：fs_read/fs_write/fs_list/fs_delete/fs_download（供其他插件与 AI 复用）";

    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("Root", "沙箱根目录", "text", "", "留空 = memory/fs；一切读写被限制在该目录内"),
        new("MaxFileKb", "单文件上限 KB", "number", "512", "单次写入/下载的最大尺寸"),
        new("MaxTotalMb", "沙箱总容量 MB", "number", "64", "超过则拒绝新写入（防 AI 刷爆磁盘）"),
    ];

    public async Task OnSettingsChangedAsync()
    {
        var newRoot = _ctx.GetSetting("Root", "");
        var def = Path.Combine(_ctx.Config.MemoryDir, "fs");
        var newRootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(newRoot) ? def : newRoot);
        var currentRootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(_root) ? def : _root);

        // 检查是否有超时的待审批变更
        if (_rootChangeRequested && 
            DateTime.UtcNow - _rootChangeTimestamp > TimeSpan.FromSeconds(RootChangeTimeoutSec))
        {
            _ctx.Log.Warn($"根目录变更请求已超时，已自动回滚");
            _rootChangeRequested = false;
            _pendingRoot = "";
            ClearPendingRootChange();
        }

        // 检测新的变更
        if (!string.Equals(newRootPath, currentRootPath, StringComparison.OrdinalIgnoreCase))
        {
            _rootChangeRequested = true;
            _pendingRoot = newRootPath;
            _rootChangeTimestamp = DateTime.UtcNow;
            SavePendingRootChange();
            
            var msg = $"根目录变更请求：{newRootPath}（5分钟后自动超时）";
            _ctx.Log.Info(msg);

            var commands = _ctx.ListCommands();
            var hasApproveCmd = commands.Any(c => c.Name == "fs_approve_root");
            if (!hasApproveCmd)
            {
                _ctx.RegisterCommand("fs_approve_root", "管理员确认根目录变更", _ => ApproveRootChangeAsync());
            }
        }
        else
        {
            ApplySettings();
            _ctx.Log.Info("设置已热应用（沙箱参数）");
        }

        await Task.CompletedTask;
    }

    private async Task<string> ApproveRootChangeAsync()
    {
        if (!_rootChangeRequested)
            return "没有待审批的根目录变更。";

        _root = _pendingRoot;
        Directory.CreateDirectory(_root);
        _rootChangeRequested = false;
        _rootChangeTimestamp = DateTime.MinValue;
        ClearPendingRootChange();
        
        _ctx.Log.Info($"根目录已确认：{_root}");
        return $"根目录已变更为：{_root}";
    }

    private void SavePendingRootChange()
    {
        try
        {
            var state = System.Text.Json.JsonSerializer.Serialize(new
            {
                requested = _rootChangeRequested,
                pending = _pendingRoot,
                timestamp = _rootChangeTimestamp.Ticks
            }, BotJson.Compact);
            File.WriteAllText(Path.Combine(_ctx.DataDir, ".rootchange"), state);
        }
        catch { }
    }

    private void ClearPendingRootChange()
    {
        try
        {
            File.Delete(Path.Combine(_ctx.DataDir, ".rootchange"));
        }
        catch { }
    }

    private void LoadPendingRootChange()
    {
        try
        {
            var file = Path.Combine(_ctx.DataDir, ".rootchange");
            if (File.Exists(file))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<RootChangeState>(File.ReadAllText(file));
                if (state is not null)
                {
                    _rootChangeRequested = state.requested;
                    _pendingRoot = state.pending ?? "";
                    _rootChangeTimestamp = new DateTime(state.timestamp, DateTimeKind.Utc);

                    // 检查是否已超时
                    if (_rootChangeRequested && 
                        DateTime.UtcNow - _rootChangeTimestamp > TimeSpan.FromSeconds(RootChangeTimeoutSec))
                    {
                        _ctx.Log.Warn($"恢复的根目录变更请求已超时，已自动回滚");
                        _rootChangeRequested = false;
                        _pendingRoot = "";
                        ClearPendingRootChange();
                    }
                }
            }
        }
        catch { }
    }

    private void ApplySettings()
    {
        var def = Path.Combine(_ctx.Config.MemoryDir, "fs");
        var raw = _ctx.GetSetting("Root", def);
        var newRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(raw) ? def : raw);
        
        if (string.IsNullOrEmpty(_root))
        {
            _root = newRoot;
        }
        
        Directory.CreateDirectory(_root);
        _maxFileKb = Math.Max(1, _ctx.GetSetting("MaxFileKb", 512));
        _maxTotalMb = Math.Max(1, _ctx.GetSetting("MaxTotalMb", 64));
    }

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;
        LoadPendingRootChange();
        ApplySettings();

        context.RegisterCommand("fs_read", "读沙箱内文本文件", a => Run(a, 1, ReadAsync), "fs_read <相对路径> [前N字符]");
        context.RegisterCommand("fs_write", "写/覆盖沙箱内文本文件", a => Run(a, 2, WriteAsync), "fs_write <相对路径> <内容>");
        context.RegisterCommand("fs_append", "追加写入沙箱内文本文件", a => Run(a, 2, AppendAsync), "fs_append <相对路径> <内容>");
        context.RegisterCommand("fs_list", "列出沙箱目录", a => Run(a, 0, ListAsync), "fs_list [相对路径]");
        context.RegisterCommand("fs_delete", "删除沙箱内文件/空目录", a => Run(a, 1, DeleteAsync), "fs_delete <相对路径>");
        context.RegisterCommand("fs_mkdir", "创建目录", a => Run(a, 1, MkdirAsync), "fs_mkdir <相对路径>");
        context.RegisterCommand("fs_info", "查看文件/目录信息", a => Run(a, 1, InfoAsync), "fs_info <相对路径>");
        context.RegisterCommand("fs_download", "下载 URL 到沙箱", a => Run(a, 2, DownloadAsync), "fs_download <URL> <相对路径>");
        context.RegisterCommand("fs_move", "移动/重命名", a => Run(a, 2, MoveAsync), "fs_move <原路径> <新路径>");
        context.RegisterCommand("fs_search", "正则搜索文件内容", a => Run(a, 2, SearchAsync), "fs_search <相对路径|目录> <正则> [前N条]");
        context.RegisterCommand("fs_replace", "正则替换文件内容", a => Run(a, 3, ReplaceAsync), "fs_replace <相对路径> <正则> <替换串>");
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync()
    {
        _ctx = null!;
        return Task.CompletedTask;
    }

    // ---------------- 核心：参数拆分 + 统一异常 ----------------

    private static async Task<string> Run(string args, int minParts, Func<string[], Task<string>> fn)
    {
        try
        {
            // 用简单拆分：首 N-1 个空格分隔 + 最后一段作为整体（保留内容中的空格）
            var parts = SplitArgs(args, minParts);
            if (parts is null) return $"参数不足（期望至少 {minParts} 段）";
            var result = await fn(parts);
            return result;
        }
        catch (Exception ex)
        {
            return $"操作失败: {ex.Message}";
        }
    }

    // 前段按空格切 minParts-1 次，余下整体作为最后一段
    private static string[]? SplitArgs(string args, int minParts)
    {
        if (minParts <= 1)
        {
            var one = args.Trim();
            return one.Length == 0 ? (minParts == 0 ? [""] : null) : [one];
        }
        var idx = 0;
        var parts = new List<string>();
        var rest = args.Trim();
        for (var i = 0; i < minParts - 1; i++)
        {
            var sp = rest.IndexOf(' ', idx);
            if (sp < 0) return null;
            parts.Add(rest[idx..sp].Trim());
            idx = sp + 1;
        }
        parts.Add(rest[idx..].TrimStart());
        return parts.All(p => p.Length > 0) ? parts.ToArray() : null;
    }

    // ---------------- 路径安全 ----------------

    /// <summary>相对路径 → 沙箱内绝对路径；越界/非法返回 null。</summary>
    private string? SafePath(string rel)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;
            var full = Path.GetFullPath(Path.Combine(_root, rel.Trim().Trim('"')));
            var ok = full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(full, _root, StringComparison.OrdinalIgnoreCase);
            return ok ? full : null;
        }
        catch { return null; }
    }

    private long TotalSize()
    {
        try
        {
            return Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    // ---------------- 各操作 ----------------

    private async Task<string> ReadAsync(string[] p)
    {
        var path = SafePath(p[0]);
        if (path is null) return Reject(p[0]);
        if (!File.Exists(path)) return $"文件不存在: {p[0]}";
        var text = await File.ReadAllTextAsync(path);
        if (p.Length > 1 && int.TryParse(p[1].Trim(), out var n) && n > 0 && n < text.Length)
            text = text[..n] + $"…（共 {text.Length} 字符，已截断）";
        if (text.Length > 4000) text = text[..4000] + $"…（共 {text.Length} 字符）";
        return text.Length == 0 ? "（空文件）" : text;
    }

    private async Task<string> WriteAsync(string[] p) => await WriteCoreAsync(p, append: false);
    private async Task<string> AppendAsync(string[] p) => await WriteCoreAsync(p, append: true);

    private async Task<string> WriteCoreAsync(string[] p, bool append)
    {
        var path = SafePath(p[0]);
        if (path is null) return Reject(p[0]);
        // 允许调用方（如 AI 工具）用字面 \n 传多行内容：此处还原为真实换行
        var content = p[1].Replace("\\n", "\n");
        if (content.Length > _maxFileKb * 1024) return $"拒绝：内容超过单文件上限 {_maxFileKb}KB";
        var cur = TotalSize();
        if (cur + content.Length > _maxTotalMb * 1024 * 1024)
            return $"拒绝：沙箱总容量已达 {_maxTotalMb}MB 上限";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (append) await File.AppendAllTextAsync(path, content);
        else await File.WriteAllTextAsync(path, content);
        return $"{(append ? "已追加" : "已写入")} {Rel(path)}（{content.Length} 字符）";
    }

    private async Task<string> ListAsync(string[] p)
    {
        var dir = p.Length > 0 && p[0].Length > 0 ? SafePath(p[0]) : _root;
        if (dir is null) return Reject(p[0]);
        if (!Directory.Exists(dir)) return $"目录不存在: {p[0]}";
        var sb = new StringBuilder($"[{Rel(dir)}]\n");
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            var info = new DirectoryInfo(d);
            sb.AppendLine($"📁 {info.Name}/");
        }
        foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
        {
            var info = new FileInfo(f);
            sb.AppendLine($"📄 {info.Name}  ({info.Length}B {info.LastWriteTime:MM-dd HH:mm})");
        }
        var s = sb.ToString().TrimEnd();
        return s.Length == 0 ? "（空目录）" : s;
    }

    private Task<string> DeleteAsync(string[] p)
    {
        var path = SafePath(p[0]);
        if (path is null) return Task.FromResult(Reject(p[0]));
        if (File.Exists(path)) { File.Delete(path); return Task.FromResult($"已删除文件 {Rel(path)}"); }
        if (Directory.Exists(path))
        {
            if (Directory.GetFileSystemEntries(path).Length > 0)
                return Task.FromResult("目录非空，拒绝删除（先清空或用 fs_delete 逐个删文件）");
            Directory.Delete(path);
            return Task.FromResult($"已删除空目录 {Rel(path)}");
        }
        return Task.FromResult($"不存在: {p[0]}");
    }

    private Task<string> MkdirAsync(string[] p)
    {
        var path = SafePath(p[0]);
        if (path is null) return Task.FromResult(Reject(p[0]));
        Directory.CreateDirectory(path);
        return Task.FromResult($"已创建目录 {Rel(path)}");
    }

    private Task<string> InfoAsync(string[] p)
    {
        var path = SafePath(p[0]);
        if (path is null) return Task.FromResult(Reject(p[0]));
        if (File.Exists(path))
        {
            var f = new FileInfo(path);
            return Task.FromResult($"{Rel(path)}\n类型: 文件  大小: {f.Length}B\n创建: {f.CreationTime:yyyy-MM-dd HH:mm:ss}  修改: {f.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        }
        if (Directory.Exists(path))
        {
            var d = new DirectoryInfo(path);
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            return Task.FromResult($"{Rel(path)}\n类型: 目录  子项: {files.Count} 文件  总大小: {files.Sum(x => new FileInfo(x).Length)}B\n创建: {d.CreationTime:yyyy-MM-dd HH:mm:ss}");
        }
        return Task.FromResult($"不存在: {p[0]}");
    }

    private async Task<string> DownloadAsync(string[] p)
    {
        var url = p[0];
        var path = SafePath(p[1]);
        if (path is null) return Reject(p[1]);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "URL 无效（需 http(s):// 开头）";
        var bytes = await _ctx.Http.Client.GetByteArrayAsync(uri);
        if (bytes.Length > _maxFileKb * 1024) return $"拒绝：下载内容 {_maxFileKb * 1024 / bytes.Length}x 超过单文件上限 {_maxFileKb}KB";
        if (TotalSize() + bytes.Length > _maxTotalMb * 1024 * 1024)
            return $"拒绝：沙箱总容量已达 {_maxTotalMb}MB 上限";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        return $"已下载 {bytes.Length / 1024.0:F1}KB → {Rel(path)}";
    }

    private Task<string> MoveAsync(string[] p)
    {
        var from = SafePath(p[0]);
        var to = SafePath(p[1]);
        if (from is null) return Task.FromResult(Reject(p[0]));
        if (to is null) return Task.FromResult(Reject(p[1]));
        if (!File.Exists(from) && !Directory.Exists(from)) return Task.FromResult($"不存在: {p[0]}");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (File.Exists(to) || Directory.Exists(to)) return Task.FromResult($"目标已存在: {p[1]}");
        if (File.Exists(from)) File.Move(from, to);
        else Directory.Move(from, to);
        return Task.FromResult($"已移动 {Rel(from)} → {Rel(to)}");
    }

    // ---------------- 搜索 / 替换（供 AI 迭代修改代码用） ----------------

    private async Task<string> SearchAsync(string[] p)
    {
        // p[0] = 文件或目录，p[1] = 正则，p[2] = 可选上限（默认 50 行）
        var target = SafePath(p[0]);
        if (target is null) return Reject(p[0]);
        Regex rx;
        try { rx = new Regex(p[1], RegexOptions.Compiled); }
        catch (Exception ex) { return $"正则无效: {ex.Message}"; }
        var limit = p.Length > 2 && int.TryParse(p[2].Trim(), out var n) && n > 0 ? n : 50;

        // 收集待搜文件：单个文件或目录下全部文本文件
        var files = new List<string>();
        if (File.Exists(target)) files.Add(target);
        else if (Directory.Exists(target))
        {
            // 目录：递归搜文本类文件（.cs/.json/.md/.txt）
            files.AddRange(Directory.EnumerateFiles(target, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(".shadow"))
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
        }
        else return $"不存在: {p[0]}";

        var sb = new StringBuilder();
        var hits = 0;
        foreach (var f in files)
        {
            if (hits >= limit) break;
            string text;
            try { text = await File.ReadAllTextAsync(f); }
            catch { continue; }
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && hits < limit; i++)
            {
                if (!rx.IsMatch(lines[i])) continue;
                hits++;
                var preview = lines[i].Length > 200 ? lines[i][..200] + "…" : lines[i].TrimEnd();
                sb.AppendLine($"{Rel(f)}:{i + 1}: {preview}");
            }
        }
        if (sb.Length == 0) return $"未命中（0 行）";
        sb.AppendLine($"— 共 {hits} 行命中{(hits >= limit ? $"（达上限 {limit}，可能不止）" : "")}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ReplaceAsync(string[] p)
    {
        // p[0] = 单文件，p[1] = 正则，p[2] = 替换串（支持 $1 等分组引用；字面 \n 还原换行）
        var path = SafePath(p[0]);
        if (path is null) return Reject(p[0]);
        if (!File.Exists(path)) return $"文件不存在: {p[0]}";
        Regex rx;
        try { rx = new Regex(p[1], RegexOptions.Compiled); }
        catch (Exception ex) { return $"正则无效: {ex.Message}"; }
        var text = await File.ReadAllTextAsync(path);
        var replacement = p[2].Replace("\\n", "\n").Replace("\\t", "\t");
        var newText = rx.Replace(text, replacement);
        if (newText == text) return "未发生变化（无命中或替换后相同）";
        if (newText.Length > _maxFileKb * 1024) return $"拒绝：替换后超过单文件上限 {_maxFileKb}KB";
        await File.WriteAllTextAsync(path, newText);
        return $"已替换 {Rel(path)}（{rx.Matches(text).Count} 处，新长度 {newText.Length} 字符）";
    }

    private string Rel(string full) =>
        Path.GetRelativePath(_root, full) is { } r && r != "." ? r : "/";

    private static string Reject(string rel) =>
        $"拒绝：路径「{rel}」越界或非法（仅允许沙箱内相对路径）";
}

// 根目录变更状态（持久化用）
class RootChangeState
{
    public bool requested { get; set; }
    public string? pending { get; set; }
    public long timestamp { get; set; }
}
