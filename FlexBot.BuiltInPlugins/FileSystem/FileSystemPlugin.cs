using System.Text;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;

using Zio;
using Zio.FileSystems;
using FlexBot.BuiltInPlugins.FileSystem;
namespace FileSystemPlugin;

/// <summary>
/// 沙箱文件操作插件：fs_read/fs_write/fs_append/fs_list/fs_delete/fs_mkdir/fs_info/fs_move/fs_download/fs_search/fs_replace
/// 一切操作限制在沙箱根目录内（默认 memory/fs），路径越界一律拒绝。
/// 命令仅管理员可触发（宿主分发保证 + 工具侧 IsAdmin 双保险）。
/// </summary>
public sealed class FileSystemPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;
    private string _root = "";
    private FsSandbox? _sb;   // Zio 沙箱（迁移中：读操作已改走它）
    private long _maxFileKb;
    private long _maxTotalMb;
    private string _pendingRoot = "";
    private bool _rootChangeRequested = false;
    private DateTime _rootChangeTimestamp = DateTime.MinValue;
    private const int RootChangeTimeoutSec = 300; // 5分钟超时

    public string Name => "FileSystem";
    public string Version => "1.0.0";
    public string Description => "沙箱文件操作：fs_read/fs_write/fs_append/fs_list/fs_delete/fs_mkdir/fs_info/fs_move/fs_download/fs_search/fs_replace（供其他插件与 AI 复用）";

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
        RebuildSandbox();
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
        RebuildSandbox();
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
        context.RegisterCommand("fs_search", "正则搜索文件内容（支持目录/glob/上下文/忽略大小写）", SearchCommandAsync,
            "fs_search <相对路径|目录> <正则> [--glob *.cs] [--ignore-case] [--limit N] [--context N] [--files-only]");
        context.RegisterCommand("fs_replace", "正则替换（支持目录；默认只预览，加 --apply 才写入）", ReplaceCommandAsync,
            "fs_replace <相对路径|目录> <正则> <替换串> [--apply] [--glob *.cs] [--ignore-case] [--backup]");
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


    /// <summary>根目录变化后重建沙箱</summary>
    private void RebuildSandbox()
    {
        try { _sb?.Dispose(); } catch { }
        _sb = new FsSandbox(_root);
    }

    /// <summary>相对路径 -> 沙箱内 UPath（越界返回 false）</summary>
    private bool TryU(string? rel, out UPath path)
    {
        path = UPath.Root;
        if (_sb is null) return false;
        return _sb.TryResolve(rel, out path);
    }

    /// <summary>沙箱内路径 -> 显示用相对路径（复用既有 Rel）</summary>
    private string RelU(UPath path)
    {
        try { return Rel(_sb!.Fs.ConvertPathToInternal(path)); }
        catch { return path.FullName; }
    }

    /// <summary>确保目标的父目录存在（Zio 不会替调用方建父目录）</summary>
    private void CreateParents(UPath path)
    {
        try
        {
            var dir = path.GetDirectory();
            if (!dir.IsNull && dir != UPath.Root && !_sb!.Fs.DirectoryExists(dir)) _sb.Fs.CreateDirectory(dir);
        }
        catch { }
    }

    private long TotalSize()
    {
        try
        {
            if (_sb is null) return 0;
            return _sb.Fs.EnumeratePaths(UPath.Root, "*", SearchOption.AllDirectories, SearchTarget.File).Sum(x => _sb.Fs.GetFileLength(x));
        }
        catch { return 0; }
    }

    // ---------------- 各操作 ----------------

    private async Task<string> ReadAsync(string[] p)
    {
        var raw = p[0];
        var maxChars = 0;
        if (p.Length > 1)
        {
            _ = int.TryParse(p[1].Trim(), out maxChars);
        }
        else
        {
            var sp = raw.LastIndexOf(" ");
            if (sp > 0 && int.TryParse(raw[(sp + 1)..].Trim(), out var tail)) { maxChars = tail; raw = raw[..sp].Trim(); }
        }

        if (!TryU(raw, out var path)) return Reject(raw);
        if (!_sb!.Fs.FileExists(path)) return $"文件不存在: {raw}";
        var text = _sb.Fs.ReadAllText(path);
        if (maxChars > 0 && maxChars < text.Length) text = text[..maxChars] + $"…（共 {text.Length} 字符，已截断）";
        if (text.Length > 4000) text = text[..4000] + $"…（共 {text.Length} 字符）";
        return text.Length == 0 ? "（空文件）" : text;
    }

    private async Task<string> WriteAsync(string[] p) => await WriteCoreAsync(p, append: false);
    private async Task<string> AppendAsync(string[] p) => await WriteCoreAsync(p, append: true);

    private async Task<string> WriteCoreAsync(string[] p, bool append)
    {
        if (!TryU(p[0], out var path)) return Reject(p[0]);
        // 允许调用方（如 AI 工具）用字面 \n 传多行内容：此处还原为真实换行
        var content = p[1].Replace("\\n", "\n");
        if (content.Length > _maxFileKb * 1024) return $"拒绝：内容超过单文件上限 {_maxFileKb}KB";
        var cur = TotalSize();
        if (cur + content.Length > _maxTotalMb * 1024 * 1024)
            return $"拒绝：沙箱总容量已达 {_maxTotalMb}MB 上限";
        CreateParents(path);
        if (append) _sb!.Fs.AppendAllText(path, content);
        else _sb!.Fs.WriteAllText(path, content);
        return $"{(append ? "已追加" : "已写入")} {RelU(path)}（{content.Length} 字符）";
    }

    private async Task<string> ListAsync(string[] p)
    {
        UPath dir;
        if (p.Length > 0 && p[0].Length > 0)
        {
            if (!TryU(p[0], out dir)) return Reject(p[0]);
        }
        else
        {
            if (_sb is null) return Reject("");
            dir = UPath.Root;
        }
        if (!_sb!.Fs.DirectoryExists(dir)) return $"目录不存在: {(p.Length > 0 ? p[0] : "")}";
        var sb = new StringBuilder($"[{RelU(dir)}]\n");
        foreach (var d in _sb.Fs.EnumeratePaths(dir, "*", SearchOption.TopDirectoryOnly, SearchTarget.Directory).OrderBy(x => x.FullName))
            sb.AppendLine($"📁 {d.GetName()}/");
        foreach (var fl in _sb.Fs.EnumeratePaths(dir, "*", SearchOption.TopDirectoryOnly, SearchTarget.File).OrderBy(x => x.FullName))
            sb.AppendLine($"📄 {fl.GetName()}  ({_sb.Fs.GetFileLength(fl)}B {_sb.Fs.GetLastWriteTime(fl):MM-dd HH:mm})");
        var s = sb.ToString().TrimEnd();
        return s.Length == 0 ? "（空目录）" : s;
    }

    private Task<string> DeleteAsync(string[] p)
    {
        if (!TryU(p[0], out var path)) return Task.FromResult(Reject(p[0]));
        if (_sb!.Fs.FileExists(path)) { _sb.Fs.DeleteFile(path); return Task.FromResult($"已删除文件 {RelU(path)}"); }
        if (_sb.Fs.DirectoryExists(path))
        {
            if (_sb.Fs.EnumeratePaths(path, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both).Any())
                return Task.FromResult("目录非空，拒绝删除（先清空或用 fs_delete 逐个删文件）");
            _sb.Fs.DeleteDirectory(path, false);
            return Task.FromResult($"已删除空目录 {RelU(path)}");
        }
        return Task.FromResult($"不存在: {p[0]}");
    }

    private Task<string> MkdirAsync(string[] p)
    {
        if (!TryU(p[0], out var path)) return Task.FromResult(Reject(p[0]));
        _sb!.Fs.CreateDirectory(path);
        return Task.FromResult($"已创建目录 {RelU(path)}");
    }

    private Task<string> InfoAsync(string[] p)
    {
        if (!TryU(p[0], out var path)) return Task.FromResult(Reject(p[0]));
        if (_sb!.Fs.FileExists(path))
        {
            var len = _sb.Fs.GetFileLength(path);
            var w = _sb.Fs.GetLastWriteTime(path);
            return Task.FromResult($"{RelU(path)}\n类型: 文件  大小: {len}B\n修改: {w:yyyy-MM-dd HH:mm:ss}");
        }
        if (_sb.Fs.DirectoryExists(path))
        {
            var files = _sb.Fs.EnumeratePaths(path, "*", SearchOption.AllDirectories, SearchTarget.File).ToList();
            return Task.FromResult($"{RelU(path)}\n类型: 目录  子项: {files.Count} 文件  总大小: {files.Sum(x => _sb.Fs.GetFileLength(x))}B");
        }
        return Task.FromResult($"不存在: {p[0]}");
    }

    private async Task<string> DownloadAsync(string[] p)
    {
        var url = p[0];
        if (!TryU(p[1], out var path)) return Reject(p[1]);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "URL 无效（需 http(s):// 开头）";
        var bytes = await _ctx.Http.Client.GetByteArrayAsync(uri);
        if (bytes.Length > _maxFileKb * 1024) return $"拒绝：下载内容 {bytes.Length / 1024}KB 超过单文件上限 {_maxFileKb}KB";
        if (TotalSize() + bytes.Length > _maxTotalMb * 1024 * 1024)
            return $"拒绝：沙箱总容量已达 {_maxTotalMb}MB 上限";
        CreateParents(path);
        _sb!.Fs.WriteAllBytes(path, bytes);
        return $"已下载 {bytes.Length / 1024.0:F1}KB → {RelU(path)}";
    }

    private Task<string> MoveAsync(string[] p)
    {
        if (!TryU(p[0], out var from)) return Task.FromResult(Reject(p[0]));
        if (!TryU(p[1], out var to)) return Task.FromResult(Reject(p[1]));
        if (!_sb!.Fs.FileExists(from) && !_sb.Fs.DirectoryExists(from)) return Task.FromResult($"不存在: {p[0]}");
        CreateParents(to);
        if (_sb.Fs.FileExists(to) || _sb.Fs.DirectoryExists(to)) return Task.FromResult($"目标已存在: {p[1]}");
        if (_sb.Fs.FileExists(from)) _sb.Fs.MoveFile(from, to);
        else _sb.Fs.MoveDirectory(from, to);
        return Task.FromResult($"已移动 {RelU(from)} → {RelU(to)}");
    }

    // ---------------- 搜索 / 替换（供 AI 迭代修改代码用） ----------------

    // ---------------- 选项分词（给 fs_search / fs_replace 用）----------------

    /// <summary>
    /// 把参数串拆成 位置参数 + 开关(--flag) + 带值选项(--glob/--limit/--context)。
    /// 选项可出现在任意位置；其余按出现顺序作为位置参数（替换串可以是多个词，最后拼回去）。
    /// </summary>
    private static (List<string> Pos, HashSet<string> Flags, Dictionary<string, string> Opts) Tokenize(string args)
    {
        var pos = new List<string>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toks = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < toks.Length; i++)
        {
            var tok = toks[i];
            if (tok is "-i") { flags.Add("--ignore-case"); continue; }
            if (!tok.StartsWith("--")) { pos.Add(tok); continue; }
            if (tok is "--glob" or "--limit" or "--context")
            {
                if (i + 1 < toks.Length) opts[tok.ToLowerInvariant()] = toks[++i];
                continue;
            }
            flags.Add(tok.ToLowerInvariant());
        }
        return (pos, flags, opts);
    }

    private async Task<string> SearchCommandAsync(string args)
    {
        try
        {
            var (pos, flags, opts) = Tokenize(args);
            if (pos.Count < 2) return "参数不足：fs_search <路径|目录> <正则> [--glob *.cs] [--ignore-case] [--limit N] [--context N] [--files-only]";
            var limit = opts.TryGetValue("--limit", out var ls) && int.TryParse(ls, out var l) && l > 0 ? l : 50;
            var context = opts.TryGetValue("--context", out var cs) && int.TryParse(cs, out var c) && c > 0 ? c : 0;
            // 原方法按 p[2..] 扫选项，这里把选项按老格式拼回去（顺序无关）
            var p = new List<string> { pos[0], pos[1] };
            if (limit != 50) { p.Add("--limit"); p.Add(limit.ToString()); }
            if (context > 0) { p.Add("--context"); p.Add(context.ToString()); }
            if (flags.Contains("--files-only")) p.Add("--files-only");
            if (flags.Contains("--ignore-case")) p.Add("--ignore-case");
            if (opts.TryGetValue("--glob", out var g)) { p.Add("--glob"); p.Add(g); }
            return await SearchAsync(p.ToArray());
        }
        catch (Exception ex) { return $"操作失败: {ex.Message}"; }
    }

    private async Task<string> ReplaceCommandAsync(string args)
    {
        try
        {
            var (pos, flags, opts) = Tokenize(args);
            if (pos.Count < 3) return "参数不足：fs_replace <路径|目录> <正则> <替换串> [--apply] [--glob *.cs] [--ignore-case] [--backup]";
            var p = new List<string> { pos[0], pos[1], string.Join(" ", pos.Skip(2)) };
            if (flags.Contains("--apply")) p.Add("--apply");
            if (flags.Contains("--backup")) p.Add("--backup");
            if (flags.Contains("--ignore-case")) p.Add("--ignore-case");
            if (opts.TryGetValue("--glob", out var g)) { p.Add("--glob"); p.Add(g); }
            return await ReplaceAsync(p.ToArray());
        }
        catch (Exception ex) { return $"操作失败: {ex.Message}"; }
    }
    // ---------------- 搜索 / 替换 ----------------
    // 两者共用一套"目标文件枚举"：支持单文件或整目录、--glob 过滤、跳过 .shadow、跳过二进制与超大文件。

    /// <summary>枚举待处理文件（目录则递归）；glob 为空表示全部文件</summary>
    private IEnumerable<UPath> EnumerateTargets(UPath target, string? glob)
    {
        if (_sb!.Fs.FileExists(target)) return [target];
        // 注：Zio 的 EnumeratePaths 没有老 EnumerationOptions.ReparsePoint 跳过参数，
        // 沙箱内容全部经本插件写入（不允许创建链接），语义等价。
        return _sb.Fs.EnumeratePaths(target, string.IsNullOrWhiteSpace(glob) ? "*" : glob,
                                     SearchOption.AllDirectories, SearchTarget.File)
                   .Where(f2 => !f2.FullName.Contains(".shadow"));
    }

    /// <summary>沙箱内文件的二进制判定（读文本判 NUL；读不到按二进制处理，避免误当文本改写）</summary>
    private bool LooksBinary(UPath path)
    {
        try
        {
            var text = _sb!.Fs.ReadAllText(path);
            return text.Contains(((char)0).ToString());
        }
        catch { return true; }
    }

    private async Task<string> SearchAsync(string[] p)
    {
        // p[0] = 文件或目录，p[1] = 正则，其余为选项
        if (!TryU(p[0], out var target)) return Reject(p[0]);
        
        if (!_sb!.Fs.FileExists(target) && !_sb.Fs.DirectoryExists(target)) return $"不存在: {p[0]}";

        string? glob = null;
        var ignoreCase = false;
        var filesOnly = false;
        var limit = 50;
        var context = 0;
        for (var i = 2; i < p.Length; i++)
        {
            var a = p[i];
            if (a is "--ignore-case" or "-i") ignoreCase = true;
            else if (a is "--files-only") filesOnly = true;
            else if (a is "--glob" && i + 1 < p.Length) glob = p[++i];
            else if (a is "--limit" && i + 1 < p.Length && int.TryParse(p[++i], out var n) && n > 0) limit = n;
            else if (a is "--context" && i + 1 < p.Length && int.TryParse(p[++i], out var c) && c > 0) context = Math.Min(c, 5);
        }

        Regex rx;
        try { rx = new Regex(p[1], RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)); }
        catch (Exception ex) { return $"正则无效: {ex.Message}"; }

        var sb = new StringBuilder();
        var hits = 0;
        var scanned = 0;
        var fileHits = new List<string>();
        foreach (var f in EnumerateTargets(target, glob))
        {
            if (hits >= limit) break;
            try { if (_sb.Fs.GetFileLength(f) > _maxFileKb * 1024) continue; } catch { continue; }
            if (LooksBinary(f)) continue;
            scanned++;

            string text;
            try { text = _sb.Fs.ReadAllText(f); } catch { continue; }
            var lines = text.Split('\n');
            var here = 0;
            for (var i = 0; i < lines.Length && hits < limit; i++)
            {
                if (!rx.IsMatch(lines[i])) continue;
                here++;
                hits++;
                if (filesOnly) continue;

                // 上下文（前 N 行 / 后 N 行）：只在有 context 时输出，且不重复打印同一行
                if (context > 0)
                {
                    var from = Math.Max(0, i - context);
                    for (var k = from; k < i; k++) sb.AppendLine($"  {RelU(f)}:{k + 1}- {Clip(lines[k])}");
                }
                sb.AppendLine($"{RelU(f)}:{i + 1}: {Clip(lines[i])}");
                if (context > 0)
                {
                    var to = Math.Min(lines.Length - 1, i + context);
                    for (var k = i + 1; k <= to; k++) sb.AppendLine($"  {RelU(f)}:{k + 1}- {Clip(lines[k])}");
                }
            }
            if (here > 0) fileHits.Add($"{RelU(f)}({here})");
        }

        if (filesOnly)
            return fileHits.Count == 0
                ? $"未命中（扫描 {scanned} 个文件）"
                : $"{fileHits.Count} 个文件命中（扫描 {scanned} 个）: {string.Join(", ", fileHits.Take(60))}";
        if (sb.Length == 0) return $"未命中（扫描 {scanned} 个文件）";

        sb.AppendLine($"— 共 {hits} 行命中{(hits >= limit ? $"（达上限 {limit}，可能不止）" : "")}，涉及 {fileHits.Count} 个文件");
        return sb.ToString().TrimEnd();
    }

    private static string Clip(string s)
    {
        s = s.TrimEnd();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private async Task<string> ReplaceAsync(string[] p)
    {
        // p[0] = 文件或目录，p[1] = 正则，p[2] = 替换串（支持 $1 等分组引用；字面 \n \t 还原），其余为选项
        // **默认只预览不落盘**：改动文件的高危操作，先看清楚再 --apply。
        if (!TryU(p[0], out var target)) return Reject(p[0]);
        
        if (!_sb!.Fs.FileExists(target) && !_sb.Fs.DirectoryExists(target)) return $"不存在: {p[0]}";

        string? glob = null;
        var ignoreCase = false;
        var apply = false;
        var backup = false;
        for (var i = 3; i < p.Length; i++)
        {
            var a = p[i];
            if (a is "--apply") apply = true;
            else if (a is "--backup") backup = true;
            else if (a is "--ignore-case" or "-i") ignoreCase = true;
            else if (a is "--glob" && i + 1 < p.Length) glob = p[++i];
        }

        Regex rx;
        try { rx = new Regex(p[1], RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)); }
        catch (Exception ex) { return $"正则无效: {ex.Message}"; }

        var replacement = p[2].Replace("\\n", "\n").Replace("\\t", "\t");
        var report = new List<string>();
        var filesChanged = 0;
        var totalHits = 0;
        var skipped = 0;

        foreach (var f in EnumerateTargets(target, glob))
        {
            try { if (_sb.Fs.GetFileLength(f) > _maxFileKb * 1024) { skipped++; continue; } } catch { skipped++; continue; }
            if (LooksBinary(f)) { skipped++; continue; }

            string text;
            try { text = _sb.Fs.ReadAllText(f); } catch { skipped++; continue; }
            var m = rx.Matches(text);
            if (m.Count == 0) continue;
            var newText = rx.Replace(text, replacement);
            if (newText == text) continue;
            if (newText.Length > _maxFileKb * 1024)
            {
                skipped++;
                report.Add($"跳过 {RelU(f)}：替换后超过单文件上限 {_maxFileKb}KB");
                continue;
            }

            if (!apply)
            {
                filesChanged++;
                totalHits += m.Count;
                report.Add($"将改 {RelU(f)}：{m.Count} 处");
                continue;
            }

            try
            {
                if (backup)
                {
                    var bak = (UPath)(f.FullName + ".bak");
                    if (!_sb.Fs.FileExists(bak)) _sb.Fs.CopyFile(f, bak, false);
                }
                _sb.Fs.WriteAllText(f, newText);
                filesChanged++;
                totalHits += m.Count;
                report.Add($"已改 {RelU(f)}：{m.Count} 处{(backup ? "（已备份 .bak）" : "")}");
            }
            catch (Exception ex) { report.Add($"失败 {RelU(f)}：{ex.Message}"); skipped++; }
        }

        if (filesChanged == 0)
            return $"未命中（无需替换；跳过大文件/二进制 {skipped} 个）";

        var head = apply
            ? $"[已写入] {filesChanged} 个文件 / {totalHits} 处"
            : $"[预览，未写入] {filesChanged} 个文件 / {totalHits} 处 —— 确认无误后加 --apply 落盘";
        var body = string.Join("\n", report.Take(60));
        var more = report.Count > 60 ? $"\n…还有 {report.Count - 60} 个文件未列出" : "";
        var skipNote = skipped > 0 ? $"\n（跳过大文件/二进制 {skipped} 个）" : "";
        return $"{head}\n{body}{more}{skipNote}";
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
