using System.Runtime.InteropServices;
using System.Text;

namespace FlexBot.WebUi;

/// <summary>
/// 控制台窗口的按需开通（配合 <c>&lt;OutputType&gt;WinExe&lt;/OutputType&gt;</c>）。
///
/// 为什么这样：官方 Photino 示例的注释写得很清楚 —— 要隐藏控制台就把输出类型改成 WinExe。
/// 用 WinExe 后进程**天生没有控制台窗口**，不会出现"GUI 窗口旁边还挂一个黑框"的问题，
/// 也就不需要事后 ShowWindow 去藏它（那套做法反而添乱）。
/// 需要看控制台的场景（<c>--no-gui</c>、窗口不可用退回时）在这里按需 AllocConsole 开一个。
///
/// Linux/macOS 没有"子系统"这回事（启动它的终端不属于本进程），这里全是空操作。
/// </summary>
static class ConsoleWindow
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint dwProcessId);

    private static bool _done;

    /// <summary>确保当前有控制台可用（Windows：没有就分配一个并重接标准输出）；可重复调用</summary>
    public static void EnsureConsole()
    {
        if (_done || !OperatingSystem.IsWindows()) { _done = true; return; }
        _done = true;
        try
        {
            // 从既有终端启动（WinExe 不会自动附着）时优先附着父进程控制台，拿不到再新开
            if (!AttachConsole(0xFFFFFFFF) && !AllocConsole()) return;

            var stdout = Console.OpenStandardOutput();
            Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
            var stderr = Console.OpenStandardError();
            Console.SetError(new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true });
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        }
        catch { /* 拿不到控制台也不影响运行：日志仍在文件与 WebUI 日志页 */ }
    }
}
