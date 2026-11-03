namespace FlexBot;

/// <summary>
/// 命令行选项的**唯一**解析入口。
///
/// 为什么单独立一个类型：宿主是 WinExe 子系统，进程默认没有控制台 —— 凡是"要给人看"的调用
/// （--help / --version / 参数写错）都必须先开控制台再输出，否则输出直接丢进虚空，
/// 表现就是"敲了参数没任何反应"。把判定顺序和"是否需要控制台"一起收在这里，主流程只照做。
///
/// 判定顺序（同时给多个时）：帮助 &gt; 版本 &gt; 冲突 &gt; 未知参数。
/// 参数名一律大小写不敏感（Windows 用户习惯 <c>--HELP</c> / <c>--Gui</c>，不该因此报未知参数）。
/// </summary>
public sealed record CliOptions(
    bool Help, bool Version, bool ForceGui, bool NoGui, bool Conflict, IReadOnlyList<string> Unknown)
{
    /// <summary>帮助文本（<c>--help</c> 原样输出；增减参数时只改这一处，并有测试盯着它与实现不漂移）。</summary>
    public const string Usage = """
        FlexBot —— QQ 机器人宿主（WebUI + 热插拔插件）

        用法:
          FlexBot [选项]

        选项:
          (无参数)     默认：开原生窗口（Photino + WebView2），控制台收起
                      环境开不了窗口时自动退回"控制台 + WebUI"
          --gui        强制原生窗口：环境开不了窗口就报错退出（退出码 3），不静默退化
          --no-gui     不开窗口：控制台常驻，用浏览器打开打印出的 WebUI 地址
          --help, -h   显示本帮助后退出
          --version    显示版本（含 .NET 运行时版本）后退出

        退出码:
          0  正常退出（含 --help / --version）
          2  参数错误（未知参数，或 --gui 与 --no-gui 同时给）
          3  指定了 --gui，但原生窗口无法创建
          4  已有实例在运行（同一目录只允许一个宿主）

        示例:
          FlexBot                 桌面：开窗口
          FlexBot --no-gui        服务器/后台：控制台 + WebUI
          FlexBot --gui           必须开窗（开不了就失败，便于脚本/计划任务判断）
        """;

    /// <summary>这次调用是否需要控制台（帮助、版本、参数错误、或本身就是控制台模式）。</summary>
    public bool NeedsConsole => Help || Version || Conflict || Unknown.Count > 0 || NoGui;

    /// <summary>解析命令行。不抛异常：不认识的参数收进 <see cref="Unknown"/>，由主流程统一提示。</summary>
    public static CliOptions Parse(string[]? args)
    {
        var unknown = new List<string>();
        bool help = false, version = false, forceGui = false, noGui = false;

        foreach (var raw in args ?? [])
        {
            var a = (raw ?? "").Trim();
            if (a.Length == 0) continue;
            switch (a.ToLowerInvariant())
            {
                case "--help" or "-h" or "-?" or "/?":
                    help = true;
                    break;
                case "--version" or "-v":
                    version = true;
                    break;
                case "--gui":
                    forceGui = true;
                    break;
                case "--no-gui":
                    noGui = true;
                    break;
                default:
                    unknown.Add(a);
                    break;
            }
        }

        // 冲突只在"两个都真的要执行"时才算错：--help 时不因为别的开关组合而额外报错
        var conflict = forceGui && noGui;
        return new CliOptions(help, version, forceGui, noGui, conflict, unknown);
    }
}
