using System.Runtime.InteropServices;

namespace FlexBot.WebUi;

/// <summary>
/// 让控制台认识 ANSI 转义序列（颜色、加粗等）。
///
/// 背景：LLBot / NapCat 这类子进程会输出 <c>\x1b[32m…\x1b[0m</c> 形式的彩色日志。
/// Windows 控制台默认**不解释**这些序列，于是屏幕上就出现刺眼的 <c>[0m</c>、<c>[32m</c> 字面量。
/// 解决办法是按官方方式给本进程的控制台打开 ENABLE_VIRTUAL_TERMINAL_PROCESSING（Win10 1511+ 支持）。
///
/// 日志文件里依然保留原始转义序列（这是通行做法，支持颜色的终端/编辑器能直接渲染）；
/// WebUI 日志页由前端解析成彩色 span（见 js/logs.js 的 ansiToHtml）。
/// Linux/macOS 的现代终端本就支持 ANSI，这里全是空操作。
/// </summary>
static class AnsiConsole
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static bool _tried;
    private static bool _enabled;

    /// <summary>打开 VT 处理（可重复调用；没有控制台时静默跳过）</summary>
    public static bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return true;   // Unix 终端原生支持
        if (_tried) return _enabled;
        _tried = true;
        try
        {
            var h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
            if (!GetConsoleMode(h, out var mode)) return false;
            if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0) { _enabled = true; return true; }
            _enabled = SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
            return _enabled;
        }
        catch { return false; }
    }

    /// <summary>去 ANSI 转义序列（给不适合带颜色的地方用，例如写入不含色值的纯文本）</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // CSI（\x1b[ … 字母）与 OSC（\x1b] … BEL/ST）都清掉
        var s = System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?]*[ -/]*[@-~]", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\][^\x07\x1b]*(\x07|\x1b\\)", "");
    }
}
