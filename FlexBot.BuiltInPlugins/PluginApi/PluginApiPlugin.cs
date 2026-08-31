using System.Reflection.Metadata;
using System.Text;
using FlexBot.PluginApi;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using OneBotLib.Events;

namespace PluginApiPlugin;

// ILSpy 反编译查询插件：把宿主契约层（PluginApi 命名空间）与 OneBotLib 公共类型反编译为 C# 源码
public sealed class PluginApiPlugin : IBotPlugin
{
    private const int PageChars = 2200;

    private sealed record TypeEntry(string Asm, string Ns, string Dot, string Slash, string Name, EntityHandle Handle);

    private IBotContext _ctx = null!;
    private readonly List<IDisposable> _commandSubs = [];
    private readonly object _gate = new();
    private List<TypeEntry>? _index;
    private readonly Dictionary<string, string> _cache = [];
    private string _hostPath = null!;
    private string _oneBotPath = null!;

    public string Name => "PluginApi";
    public string Version => "1.0.0";
    public string Description => "ILSpy 反编译查询插件 API 源码：!pluginapi list|all|search <词>|<类型名> [页码]";

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;
        _hostPath = typeof(IBotPlugin).Assembly.Location;
        _oneBotPath = typeof(GroupMessageEventArgs).Assembly.Location;
        _commandSubs.Add(context.RegisterCommand(
            "pluginapi", "ILSpy 反编译查询插件 API 源码",
            args => Task.FromResult(Run(args)),
            "pluginapi [list|all|search <词>|<类型名>] [页码]"));
        _commandSubs.Add(context.RegisterCommand(
            "papi", "同 pluginapi",
            args => Task.FromResult(Run(args)),
            "papi ..."));
        return Task.CompletedTask;
    }

    public async Task OnUnloadAsync()
    {
        foreach (var sub in _commandSubs) sub.Dispose();
        _commandSubs.Clear();
        lock (_gate)
        {
            _index = null;
            _cache.Clear();
        }
        _ctx = null!;
        await Task.CompletedTask;
    }

    private string Run(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Help();

        var page = 1;
        if (parts.Length >= 2 && int.TryParse(parts[^1], out var p) && p >= 1)
        {
            page = p;
            parts = parts[..^1];
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                return Help();
            case "list":
                return Paginate("类型列表", BuildList(), page, "list");
            case "all" when parts.Length == 1:
                return Paginate("PluginApi 契约层源码", DecompileAll(), page, "all");
            case "search" when parts.Length >= 2:
            {
                var kw = string.Join(' ', parts[1..]);
                return Paginate($"搜索 \"{kw}\"", Search(kw), page, "search " + kw);
            }
        }

        if (parts.Length != 1) return "参数无效。\n" + Help();
        return ResolveAndDecompile(parts[0], page);
    }

    private static string Help() =>
        """
        PluginApi 源码查询（ILSpy 反编译引擎）
        用法：
          !pluginapi list - 列出全部可查询类型
          !pluginapi <类型名> [页码] - 反编译该类型源码（短名 / 全名 / 嵌套 Outer+Nested）
          !pluginapi all [页码] - 反编译整个 PluginApi 契约层
          !pluginapi search <关键字> [页码] - 按名称搜索类型
        数据源：FlexBot.dll（PluginApi 命名空间）+ OneBotLib.dll（公共类型）
        示例：!pluginapi IBotContext、!pluginapi OneBotLib.Events.GroupMessageEventArgs
        别名：!papi
        """;

    // ===================== 类型索引 =====================

    private List<TypeEntry> GetIndex()
    {
        lock (_gate) return _index ??= BuildIndex();
    }

    private List<TypeEntry> BuildIndex()
    {
        var list = new List<TypeEntry>();
        Collect(list, "FlexBot", _hostPath, "PluginApi");
        Collect(list, "OneBotLib", _oneBotPath, null);
        list.Sort((a, b) => string.CompareOrdinal(a.Slash, b.Slash));
        return list;
    }

    private static void Collect(List<TypeEntry> list, string asm, string path, string? onlyNamespace)
    {
        var dc = CreateDecompiler(path);
        foreach (var t in dc.TypeSystem.MainModule.TypeDefinitions)
        {
            if (onlyNamespace is not null && t.Namespace != onlyNamespace) continue;
            if (t.Accessibility != Accessibility.Public) continue;
            if (t.Name.IndexOfAny(['<', '>']) >= 0) continue;
            var slash = t.Namespace.Length == 0 ? ChainOf(t, '/') : t.Namespace + "." + ChainOf(t, '/');
            var dot = t.Namespace.Length == 0 ? ChainOf(t, '.') : t.Namespace + "." + ChainOf(t, '.');
            list.Add(new TypeEntry(asm, t.Namespace.Length == 0 ? "-" : t.Namespace, dot, slash, t.Name, t.MetadataToken));
        }
    }

    private static string ChainOf(ITypeDefinition t, char sep) =>
        t.DeclaringTypeDefinition is { } d ? ChainOf(d, sep) + sep + t.Name : t.Name;

    private static CSharpDecompiler CreateDecompiler(string path) =>
        new(path, new DecompilerSettings());

    private string PathFor(string asm) => asm == "FlexBot" ? _hostPath : _oneBotPath;

    // ===================== 反编译 =====================

    private string ResolveAndDecompile(string input, int page)
    {
        var idx = GetIndex();
        var key = input.Replace('+', '/').TrimEnd('/').ToLowerInvariant();

        var matches = idx.Where(t => t.Slash.ToLowerInvariant() == key || t.Dot.ToLowerInvariant() == key).ToList();
        if (matches.Count == 0)
            matches = idx.Where(t =>
                t.Name.Equals(input, StringComparison.OrdinalIgnoreCase) ||
                t.Slash.ToLowerInvariant().EndsWith("/" + key, StringComparison.Ordinal)).ToList();

        if (matches.Count == 0)
            return $"未找到类型 {input}。!pluginapi list 查看全部，或 !pluginapi search <关键字>";
        if (matches.Count > 1)
            return "多个类型匹配，请用全名：\n" + string.Join("\n", matches.Select(t => t.Dot));

        var e = matches[0];
        var src = Decompile(e);
        return Paginate($"{e.Dot}（{e.Asm}）", src, page, input);
    }

    private string Decompile(TypeEntry e)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(e.Slash, out var cached)) return cached;
            var dc = CreateDecompiler(PathFor(e.Asm));
            return _cache[e.Slash] = dc.DecompileAsString(e.Handle).TrimEnd();
        }
    }

    private string DecompileAll()
    {
        lock (_gate)
        {
            if (_cache.TryGetValue("*all*", out var all)) return all;
            var idx = _index ??= BuildIndex();
            var sb = new StringBuilder();
            foreach (var e in idx.Where(t => t.Asm == "FlexBot"))
            {
                if (!_cache.TryGetValue(e.Slash, out var src))
                {
                    var dc = CreateDecompiler(PathFor(e.Asm));
                    _cache[e.Slash] = src = dc.DecompileAsString(e.Handle).TrimEnd();
                }
                sb.AppendLine("// ============================== " + e.Dot + " ==============================");
                sb.AppendLine(src);
                sb.AppendLine();
            }
            return _cache["*all*"] = sb.ToString().TrimEnd();
        }
    }

    // ===================== 列表 / 搜索 / 分页 =====================

    private string BuildList()
    {
        var idx = GetIndex();
        var sb = new StringBuilder();
        sb.AppendLine("== PluginApi 契约层（FlexBot.dll）==");
        sb.AppendLine(string.Join(", ", idx.Where(t => t.Asm == "FlexBot").Select(t => t.Name)));
        sb.AppendLine();
        sb.AppendLine("== OneBotLib（OneBotLib.dll）==");
        foreach (var g in idx.Where(t => t.Asm == "OneBotLib").GroupBy(t => t.Ns).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"[{g.Key}] {string.Join(", ", g.Select(t => t.Name))}");
        sb.AppendLine();
        sb.AppendLine("!pluginapi <类型名> 查看源码，!pluginapi all 查看整个契约层");
        return sb.ToString().TrimEnd();
    }

    private string Search(string keyword)
    {
        var k = keyword.ToLowerInvariant();
        var hits = GetIndex().Where(t => t.Slash.ToLowerInvariant().Contains(k)).Take(40).ToList();
        if (hits.Count == 0) return $"未找到名称包含 \"{keyword}\" 的类型";
        return string.Join("\n", hits.Select(t => t.Dot)) +
               $"\n共 {hits.Count} 个，!pluginapi <类型名> 查看源码";
    }

    private static string Paginate(string title, string body, int page, string retryArg)
    {
        var pages = SplitPages(body, PageChars);
        if (page > pages.Count) return $"{title}：只有 {pages.Count} 页";
        var sb = new StringBuilder();
        sb.AppendLine($"【{title}】第 {page}/{pages.Count} 页");
        sb.Append(pages[page - 1]);
        if (page < pages.Count) sb.AppendLine($"\n(下一页: !pluginapi {retryArg} {page + 1})");
        return sb.ToString().TrimEnd();
    }

    private static List<string> SplitPages(string body, int limit)
    {
        var pages = new List<string>();
        var cur = new StringBuilder();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (cur.Length + line.Length + 1 > limit && cur.Length > 0)
            {
                pages.Add(cur.ToString().TrimEnd());
                cur.Clear();
            }
            cur.AppendLine(line);
        }
        if (cur.Length > 0) pages.Add(cur.ToString().TrimEnd());
        return pages.Count > 0 ? pages : [""];
    }
}
