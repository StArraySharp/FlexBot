using Photino.NET;

namespace FlexBot.WebUi;

// Photino 桌面窗口：加载 WebUI 页面（WebView2），关窗即退出整个程序
static class PhotinoGui
{
    public static void Run(string url)
    {
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
                Console.WriteLine("[gui] Photino 窗口已打开");
                window.WaitForClose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gui] Photino 异常: {ex.Message}（可改用浏览器打开 WebUI 地址）");
                return; // 窗口失败不杀进程，控制台/WebUI 继续可用
            }
            Console.WriteLine("[gui] 窗口已关闭，退出程序");
            Environment.Exit(0);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(); // 阻塞主线程直到窗口关闭
    }
}
