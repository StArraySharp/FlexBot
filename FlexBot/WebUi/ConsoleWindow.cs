using System.Runtime.InteropServices;

namespace FlexBot.WebUi;

/// <summary>
/// 控制台窗口的显示/隐藏（仅 Windows 有效；Linux/macOS 下启动它的终端不属于本进程，不做处理）。
///
/// 用途：GUI 模式（原生窗口或浏览器应用窗口）下把控制台窗口收起来，任务栏只留应用窗口，
/// 观感上就是一个普通桌面程序；日志照样进环形缓冲与日志文件，WebUI 日志页可看。
/// 需要看控制台时：加 <c>--console</c> 启动，或直接看日志文件。
/// </summary>
static class ConsoleWindow
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>是否支持（仅 Windows 有"属于本进程的控制台窗口"这个概念）</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    public static bool Hide()
    {
        if (!Supported) return false;
        try
        {
            var h = GetConsoleWindow();
            if (h == IntPtr.Zero) return false;      // 没有控制台（被 --no-web 之外的方式隐藏/无控制台）
            return ShowWindow(h, SW_HIDE);
        }
        catch { return false; }
    }

    public static bool Show()
    {
        if (!Supported) return false;
        try
        {
            var h = GetConsoleWindow();
            if (h == IntPtr.Zero) return false;
            ShowWindow(h, SW_SHOW);
            return true;
        }
        catch { return false; }
    }
}
