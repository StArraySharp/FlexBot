using System.Reflection.Metadata;
using System.Text;
using FlexBot.PluginApi;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using OneBotLib.Events;

namespace PluginApiPlugin;

// ILSpy 反编译查询插件：把契约层（FlexBot.Abstractions / FlexBot.PluginApi 命名空间）与 OneBotLib 公共类型反编译为 C# 源码
public sealed class PluginApiPlugin : IBotPlugin
{
    private const int PageChars = 2200;

    private sealed record TypeEntry(string Asm, string Ns, string Dot, string Slash, string Name, EntityHandle Handle);

    // 成员级索引：只读元数据（不解码方法体），所以建得快、也不吃内存
    private sealed record MemberEntry(string Asm, string TypeDot, string Signature, string Return, string Kind);

    private IBotContext _ctx = null!;
    private readonly List<IDisposable> _commandSubs = [];
    private readonly object _gate = new();
    private List<TypeEntry>? _index;
    private List<MemberEntry>? _memberIndex;
    private readonly Dictionary<string, string> _cache = [];
    private string _hostPath = null!;
    private string _oneBotPath = null!;
    private string _hostAsm = null!;   // 契约层程序集名（运行时取，别硬编码——契约层已从 FlexBot.dll 剥到 FlexBot.Abstractions.dll）

    public string Name => "PluginApi";
    public string Version => "1.2.0";
    public string Description => "ILSpy 反编译查询插件 API 源码：!pluginapi list|all|search <词>|<类型名> [页码]";

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;
        // 契约层现在是独立程序集 FlexBot.Abstractions（命名空间 FlexBot.PluginApi）；
        // 运行时取名字与路径，避免以后再搬/改名就失配
        _hostPath = typeof(IBotPlugin).Assembly.Location;
        _hostAsm = typeof(IBotPlugin).Assembly.GetName().Name ?? "FlexBot.Abstractions";
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
            _memberIndex = null;
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
                return Paginate($"{_hostAsm} 契约层源码", DecompileAll(), page, "all");
            case "search" when parts.Length >= 2:
            {
                var kw = string.Join(' ', parts[1..]);
                return Paginate($"搜索 \"{kw}\"", Search(kw), page, "search " + kw);
            }
            case "member" when parts.Length >= 2:
                return ListMembers(parts[1], parts.Length >= 3 ? string.Join(' ', parts[2..]) : "", page);
        }

        if (parts.Length != 1) return "参数无效。\n" + Help();
        return ResolveAndDecompile(parts[0], page);
    }

    private string Help() =>
        $"""
        PluginApi 源码查询（ILSpy 反编译引擎）
        用法：
          !pluginapi list - 列出全部可查询类型
          !pluginapi <类型名> [页码] - 反编译该类型源码（短名 / 全名 / 嵌套 Outer+Nested）
          !pluginapi member <类型名> [过滤词] [页码] - 列出该类型的公共成员（只看签名，不解码方法体）
          !pluginapi all [页码] - 反编译整个契约层
          !pluginapi search <关键字> [页码] - 搜类型与成员（成员级索引）
        数据源：{_hostAsm ?? "FlexBot.Abstractions"}.dll（FlexBot.PluginApi 命名空间）+ OneBotLib.dll（公共类型）
        示例：!pluginapi IBotContext、!pluginapi member IMessageHub、!pluginapi search OnGroup
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
        // 契约层的命名空间从已知类型推导（现在实际是 FlexBot.PluginApi），
        // 同样别再硬编码——万一以后再挪，这里自动跟随
        Collect(list, _hostAsm, _hostPath, typeof(IBotPlugin).Namespace);
        Collect(list, "OneBotLib", _oneBotPath, null);
        list.Sort((a, b) => string.CompareOrdinal(a.Slash, b.Slash));
        return list;
    }

    private static void Collect(List<TypeEntry> list, string asm, string path, string? namespacePrefix)
    {
        var dc = CreateDecompiler(path);
        foreach (var t in dc.TypeSystem.MainModule.TypeDefinitions)
        {
            if (namespacePrefix is not null &&
                !t.Namespace.StartsWith(namespacePrefix, StringComparison.Ordinal)) continue;
            if (t.Accessibility != Accessibility.Public) continue;
            if (t.Name.IndexOfAny(['<', '>']) >= 0) continue;
            var slash = t.Namespace.Length == 0 ? ChainOf(t, '/') : t.Namespace + "." + ChainOf(t, '/');
            var dot = t.Namespace.Length == 0 ? ChainOf(t, '.') : t.Namespace + "." + ChainOf(t, '.');
            list.Add(new TypeEntry(asm, t.Namespace.Length == 0 ? "-" : t.Namespace, dot, slash, t.Name, t.MetadataToken));
        }
    }

    private static string ChainOf(ITypeDefinition t, char sep) =>
        t.DeclaringTypeDefinition is { } d ? ChainOf(d, sep) + sep + t.Name : t.Name;

    // ===================== 成员索引（成员级搜索用） =====================

    private List<MemberEntry> GetMemberIndex()
    {
        lock (_gate) return _memberIndex ??= BuildMemberIndex();
    }

    private List<MemberEntry> BuildMemberIndex()
    {
        var list = new List<MemberEntry>();
        CollectMembers(list, _hostAsm, _hostPath, typeof(IBotPlugin).Namespace);
        CollectMembers(list, "OneBotLib", _oneBotPath, null);
        return list;
    }

    private static void CollectMembers(List<MemberEntry> list, string asm, string path, string? namespacePrefix)
    {
        var dc = CreateDecompiler(path);
        foreach (var t in dc.TypeSystem.MainModule.TypeDefinitions)
        {
            if (namespacePrefix is not null &&
                !t.Namespace.StartsWith(namespacePrefix, StringComparison.Ordinal)) continue;
            if (t.Accessibility != Accessibility.Public) continue;
            if (t.Name.IndexOfAny(['<', '>']) >= 0) continue;

            var typeDot = t.Namespace.Length == 0 ? ChainOf(t, '.') : t.Namespace + "." + ChainOf(t, '.');
            foreach (var m in t.Members)
            {
                if (m.Accessibility != Accessibility.Public) continue;
                if (m.Name.IndexOfAny(['<', '>']) >= 0) continue;
                // 属性/事件的访问器与运算符重载噪音大，跳过（属性本身会作为成员列出）
                if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")
                    || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) continue;
                list.Add(new MemberEntry(asm, typeDot, MemberSignature(m), MemberReturn(m), MemberKind(m)));
            }
        }
    }

    private static string MemberKind(IMember m) => m switch
    {
        IMethod => "方法",
        IProperty => "属性",
        IField => "字段",
        IEvent => "事件",
        _ => "成员"
    };

    private static string MemberSignature(IMember m) => m switch
    {
        IMethod md => $"{md.Name}({string.Join(", ", md.Parameters.Select(p => $"{p.Type.Name} {p.Name}"))})",
        IProperty p => $"{p.Name} {{ {(p.CanGet ? "get; " : "")}{(p.CanSet ? "set; " : "")}}}",
        IEvent e => $"{e.Name}",
        _ => m.Name
    };

    private static string MemberReturn(IMember m) => m switch
    {
        IMethod md => md.ReturnType.Name,
        IProperty p => p.ReturnType.Name,
        IField f => f.ReturnType.Name,
        IEvent e => $"event {e.ReturnType.Name}",
        _ => ""
    };

    private static CSharpDecompiler CreateDecompiler(string path) =>
        new(path, new DecompilerSettings());

    private string PathFor(string asm) => asm == _hostAsm ? _hostPath : _oneBotPath;

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
            return $"未找到类型 {input}。!pluginapi list 查看全部，或 !pluginapi search <关键字>" + Suggest(input);
        if (matches.Count > 1)
            return "多个类型匹配，请用全名：\n" + string.Join("\n", matches.Select(t => t.Dot));

        var e = matches[0];
        var src = Decompile(e);
        return Paginate($"{e.Dot}（{e.Asm}）", src, page, input);
    }

    /// <summary>未命中时的相近提示（不再只回"没找到"）</summary>
    private string Suggest(string input)
    {
        var key = input.ToLowerInvariant();
        if (key.Length == 0) return "";
        var idx = GetIndex();
        var near = idx.Where(t => t.Name.ToLowerInvariant().Contains(key) || key.Contains(t.Name.ToLowerInvariant()))
            .Select(t => t.Dot).Distinct().Take(6).ToList();
        if (near.Count == 0 && key.Length >= 3)
            near = idx.Where(t => t.Name.Length >= 3 &&
                                  t.Name[..3].Equals(key[..3], StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Dot).Distinct().Take(6).ToList();
        return near.Count == 0 ? "" : "\n相近的类型：" + string.Join("、", near);
    }

    // ===================== 成员级查询 =====================

    private string ListMembers(string typeName, string filter, int page)
    {
        var idx = GetIndex();
        var key = typeName.Replace('+', '/').TrimEnd('/').ToLowerInvariant();
        var t = idx.FirstOrDefault(x => x.Slash.ToLowerInvariant() == key || x.Dot.ToLowerInvariant() == key)
             ?? idx.FirstOrDefault(x => x.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (t is null) return $"未找到类型 {typeName}" + Suggest(typeName);

        var k = (filter ?? "").ToLowerInvariant();
        var members = GetMemberIndex()
            .Where(m => m.TypeDot.Equals(t.Dot, StringComparison.OrdinalIgnoreCase))
            .Where(m => k.Length == 0 || m.Signature.ToLowerInvariant().Contains(k) || m.Return.ToLowerInvariant().Contains(k))
            .ToList();
        if (members.Count == 0)
            return $"{t.Dot} 没有匹配 \"{filter}\" 的公共成员";

        var sb = new StringBuilder($"{t.Dot} 的公共成员（{members.Count} 个）:\n");
        foreach (var m in members)
            sb.AppendLine($"  [{m.Kind}] {m.Return} {m.Signature}".TrimEnd());
        sb.AppendLine($"!pluginapi {t.Name} 查看该类型完整源码");
        return Paginate($"{t.Name} 成员", sb.ToString().TrimEnd(), page,
            filter is { Length: > 0 } ? $"member {typeName} {filter}" : $"member {typeName}");
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
            foreach (var e in idx.Where(t => t.Asm == _hostAsm))
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
        sb.AppendLine($"== 契约层（{_hostAsm}.dll / FlexBot.PluginApi 命名空间）==");
        sb.AppendLine(string.Join(", ", idx.Where(t => t.Asm == _hostAsm).Select(t => t.Name)));
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
        var typeHits = GetIndex().Where(t => t.Slash.ToLowerInvariant().Contains(k)).Take(40).ToList();
        var memberHits = GetMemberIndex()
            .Where(m => m.Signature.ToLowerInvariant().Contains(k) || m.Return.ToLowerInvariant().Contains(k))
            .Take(40).ToList();

        if (typeHits.Count == 0 && memberHits.Count == 0)
            return $"未找到名称包含 \"{keyword}\" 的类型或成员" + Suggest(keyword)
                 + (SuggestMembers(keyword) is { Length: > 0 } sm ? "\n" + sm : "");

        var sb = new StringBuilder();
        if (typeHits.Count > 0)
        {
            sb.AppendLine($"== 类型（{typeHits.Count}）==");
            foreach (var t in typeHits) sb.AppendLine(t.Dot);
        }
        if (memberHits.Count > 0)
        {
            sb.AppendLine($"== 成员（{memberHits.Count}）==");
            foreach (var m in memberHits) sb.AppendLine($"{m.TypeDot}.{m.Signature}  [{m.Kind}]");
        }
        sb.AppendLine("!pluginapi <类型名> 看类型源码；!pluginapi member <类型名> [词] 列出某类型全部成员");
        return sb.ToString().TrimEnd();
    }

    /// <summary>成员名的前缀相近提示（应对拼错，例如 OnGrup → OnGroup…）</summary>
    private string SuggestMembers(string keyword)
    {
        var key = keyword.ToLowerInvariant();
        if (key.Length < 2) return "";
        var head = key[..Math.Min(3, key.Length)];
        var near = GetMemberIndex()
            .Where(m => m.Signature.ToLowerInvariant().StartsWith(head, StringComparison.Ordinal))
            .Select(m => $"{m.TypeDot}.{m.Signature}").Distinct().Take(8).ToList();
        return near.Count == 0 ? "" : "相近的成员：\n  " + string.Join("\n  ", near) + "\n";
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
