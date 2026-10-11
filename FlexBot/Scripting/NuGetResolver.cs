using System.IO.Compression;
using System.Xml.Linq;

namespace FlexBot.Scripting;

/// <summary>
/// 极简 NuGet 解析器：把插件 plugin.json 里 <c>NuGet</c> 字段声明的包（含传递依赖）铺进它的 libs/。
///
/// 只用 BCL：flat-container HTTP 源 + ZipArchive + XML —— 不引入 NuGet 客户端库，
/// 也不需要任何外部工具/脚本（部署机无需 dotnet SDK）。
/// 作者的写法只有一行：<c>"NuGet": ["Newtonsoft.Json@13.0.4"]</c>
///
/// 产物落到 plugins/&lt;名&gt;/libs/，随后：
///   · 编译期：宿主把它当 MetadataReference 交给 Roslyn
///   · 运行期：PluginLoadContext 从影子目录的 libs/ 探测同名程序集
/// 下载缓存放在 plugins/.cache/nuget/，多插件复用、离线可重复使用。
/// </summary>
public static class NuGetResolver
{
    /// <summary>可接受的 TFM 优先级（高→低）；只取 lib/ 下的托管资产</summary>
    private static readonly string[] TfmPreference =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0",
        "netcoreapp3.1", "netstandard2.1", "netstandard2.0", "netstandard1.6", "netstandard1.3", "netstandard1.0"
    ];

    private const int MaxPackages = 40; // 传递闭包上限（防依赖环 / 防爆）

    public sealed record Result(int Resolved, List<string> Added, List<string> Notes);

    /// <summary>确保声明的包都已铺进 libs/（已存在的按文件大小跳过）</summary>
    public static async Task<Result> EnsureLibsAsync(
        string pluginDir,
        IReadOnlyList<string> specs,
        string feed,
        HttpClient http,
        Action<string> log,
        CancellationToken ct = default)
    {
        var added = new List<string>();
        var notes = new List<string>();
        var resolved = 0;
        var libsDir = Path.Combine(pluginDir, "libs");
        var cacheRoot = Path.Combine(pluginDir, ".cache", "nuget");

        var queue = new Queue<(string Id, string Ver)>();
        foreach (var spec in specs)
        {
            var parts = spec.Split('@', 2);
            if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
            {
                notes.Add($"跳过非法声明「{spec}」（应写 Name@Version，如 Newtonsoft.Json@13.0.4）");
                continue;
            }
            queue.Enqueue((parts[0].Trim(), LowerBound(parts[1].Trim())));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            if (seen.Count >= MaxPackages) { notes.Add($"依赖闭包达上限 {MaxPackages} 个包，已截断"); break; }

            var (id, ver) = queue.Dequeue();
            if (id.Length == 0 || ver.Length == 0) continue;
            if (!seen.Add($"{id}@{ver}")) continue;

            try
            {
                log($"NuGet 解析 {id}@{ver} …");
                var nupkg = await DownloadAsync(cacheRoot, id, ver, feed, http, ct);
                if (nupkg is null) { notes.Add($"{id}@{ver} 下载失败（包名/版本或源不可达）"); continue; }

                var (files, deps) = Extract(nupkg, libsDir, notes);
                resolved++;
                added.AddRange(files);
                foreach (var d in deps) queue.Enqueue(d);
            }
            catch (Exception ex)
            {
                notes.Add($"{id}@{ver} 解析失败: {ex.Message}");
            }
        }

        return new Result(resolved, added.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), notes);
    }

    // 下载 .nupkg 到缓存；命中则直接用（支持离线复跑）
    private static async Task<string?> DownloadAsync(
        string cacheRoot, string id, string ver, string feed, HttpClient http, CancellationToken ct)
    {
        var lid = id.ToLowerInvariant();
        var lver = ver.ToLowerInvariant();
        var dir = Path.Combine(cacheRoot, lid, lver);
        var path = Path.Combine(dir, $"{lid}.{lver}.nupkg");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        Directory.CreateDirectory(dir);
        var url = $"{feed.TrimEnd('/')}/{lid}/{lver}/{lid}.{lver}.nupkg";
        var bytes = await http.GetByteArrayAsync(url, ct);
        if (bytes.Length == 0) return null;

        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, true);
        return path;
    }

    // 从 nupkg 里取出 lib/<最优TFM>/** 的文件，并读取 nuspec 的传递依赖
    private static (List<string> Files, List<(string Id, string Ver)> Deps) Extract(
        string nupkgPath, string libsDir, List<string> notes)
    {
        var files = new List<string>();
        var deps = new List<(string, string)>();

        using var zip = ZipFile.OpenRead(nupkgPath);

        // ---- 1) 选 lib/ 下最优 TFM ----
        var tfms = zip.Entries
            .Select(e => e.FullName.Split('/'))
            .Where(seg => seg.Length >= 3 && seg[0].Equals("lib", StringComparison.OrdinalIgnoreCase))
            .Select(seg => seg[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chosen = tfms.OrderByDescending(TfmRank).FirstOrDefault(t => TfmRank(t) > 0);

        if (chosen is null)
        {
            notes.Add($"{Path.GetFileNameWithoutExtension(nupkgPath)} 没有可用的 lib/ 托管资产"
                + (tfms.Count > 0 ? $"（只有 {string.Join("/", tfms)}）" : "（纯 native 包）"));
        }
        else
        {
            var prefix = $"lib/{chosen}/";
            var parent = Path.GetDirectoryName(libsDir)!;
            Directory.CreateDirectory(libsDir);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = e.FullName[prefix.Length..];
                if (rel.Length == 0 || rel.EndsWith('/')) continue;
                // 只要运行期真正用得上的文件：跳过 XML 文档等附属物（否则 Newtonsoft 会多带 700KB 注释）
                if (!rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;

                var dest = Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest) && new FileInfo(dest).Length == e.Length) continue; // 已就位
                    e.ExtractToFile(dest, overwrite: true);
                    files.Add(Path.GetRelativePath(parent, dest));
                }
                catch (Exception ex)
                {
                    notes.Add($"解出 {rel} 失败: {ex.Message}");
                }
            }
        }

        // ---- 2) 传递依赖：读 nuspec 的 dependencies（优先与所选 TFM 匹配的 group）----
        var nuspec = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        if (nuspec is null) return (files, deps);

        try
        {
            using var s = nuspec.Open();
            var doc = XDocument.Load(s);
            var root = doc.Root;
            if (root is null) return (files, deps);

            var ns = root.Name.Namespace;
            var depsNode = root.Element(ns + "metadata")?.Element(ns + "dependencies");
            if (depsNode is null) return (files, deps);

            var groups = depsNode.Elements(ns + "group").ToList();
            IEnumerable<XElement> depEls;
            if (groups.Count == 0)
            {
                depEls = depsNode.Elements(ns + "dependency");
            }
            else
            {
                // 选目标框架最匹配的一组；无 targetFramework 的那组作为兜底
                var best = groups
                    .OrderByDescending(g => TfmRank(g.Attribute("targetFramework")?.Value))
                    .ThenBy(g => g.Attribute("targetFramework") is null ? 1 : 0)
                    .First();
                depEls = best.Elements(ns + "dependency");
            }

            foreach (var d in depEls)
            {
                var did = d.Attribute("id")?.Value?.Trim();
                var dver = d.Attribute("version")?.Value?.Trim();
                if (string.IsNullOrEmpty(did) || string.IsNullOrEmpty(dver)) continue;
                deps.Add((did, LowerBound(dver)));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"解析 nuspec 依赖失败: {ex.Message}");
        }

        return (files, deps);
    }

    // ".NETStandard2.0" / "net8.0-windows" / ".NETFramework4.6.1" → 统一小写短名
    private static string NormalizeTfm(string? tfm)
    {
        var t = (tfm ?? string.Empty).Trim().TrimStart('.');
        var dash = t.IndexOf('-');
        if (dash > 0) t = t[..dash];
        t = t.ToLowerInvariant();
        if (t.StartsWith("netframework")) t = "net4" + t["netframework".Length..];
        return t;
    }

    private static int TfmRank(string? tfm)
    {
        var t = NormalizeTfm(tfm);
        for (var i = 0; i < TfmPreference.Length; i++)
            if (t == TfmPreference[i]) return TfmPreference.Length - i;
        return 0;
    }

    // 版本范围 → 取下界："[13.0.1, )" → 13.0.1；"(, 13.0.0]" → 13.0.0；"13.0.4" → 13.0.4
    private static string LowerBound(string range)
    {
        var t = range.Trim();
        if (t.StartsWith('[') || t.StartsWith('(')) t = t[1..];
        if (t.EndsWith(']') || t.EndsWith(')')) t = t[..^1];
        var comma = t.IndexOf(',');
        if (comma >= 0) t = t[..comma];
        t = t.Trim();
        return t.Contains('*') ? "" : t; // 通配版本不猜（跳过该依赖）
    }
}
