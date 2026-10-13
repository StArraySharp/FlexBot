using Photino.NET;

namespace FlexBot.WebUi;

/// <summary>
/// Photino 桌面窗口：加载 WebUI 页面（Windows 用 WebView2；Linux/macOS 用系统 WebView）。
///
/// 语义约定：
/// - 返回 <c>true</c> = 窗口正常打开过并已被用户关闭（此时调用方应走正常退出流程）；
/// - 返回 <c>false</c> = 窗口没能创建（没有图形界面、缺 WebView 运行时等）——**不杀进程**，
///   调用方继续以无窗口模式运行（控制台 + 浏览器访问 WebUI）。
/// - 关窗不再直接 Environment.Exit：交给 onClosed 回调走统一的优雅收尾（卸载插件/断连/清依赖进程）。
/// </summary>
static class PhotinoGui
{
    public static bool Run(string url, Action onClosed)
    {
        var opened = false;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new PhotinoWindow()
                    .SetTitle("FlexBot 控制台")
                    .SetUseOsDefaultSize(false)
                    .SetSize(1180, 780)
                    .Center()
                    .SetResizable(true)
                    .Load(url);
                opened = true;
                Console.WriteLine("[gui] Photino 窗口已打开（关窗即退出程序；如需后台无窗口运行请加 --no-gui）");
                window.WaitForClose();
                Console.WriteLine("[gui] 窗口已关闭，开始退出流程");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gui] 打开窗口失败: {ex.Message}（继续以无窗口模式运行，可浏览器打开 WebUI 地址）");
                return;
            }
            try { onClosed(); } catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();   // 阻塞主线程直到窗口关闭（或创建失败）
        return opened;
    }
}
