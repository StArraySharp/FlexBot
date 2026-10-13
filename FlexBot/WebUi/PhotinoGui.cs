using Photino.NET;

namespace FlexBot.WebUi;

/// <summary>
/// 原生应用窗口：严格按官方示例的写法（[STAThread] Main → new PhotinoWindow().…Load(url) → WaitForClose()），
/// 在**调用线程**（STA 主线程）上创建，不另起线程、不做任何"聪明"的包装。
/// 参考：https://github.com/tryphotino/photino.Samples（Photino.HelloPhotino.NET/Program.cs）
///
/// 返回值：
///   true  = 窗口正常打开并被关闭（调用方按"关窗即退出"走收尾）
///   false = 窗口没能用（创建失败，或起来了但始终没渲染出页面）⇒ 调用方退回"控制台 + WebUI"
///
/// "没渲染出来"的自检为什么留着：WebView2 环境在某些机器上会静默创建失败，表现是窗口纯黑、
/// 连一个页面请求都没有。判定方式是看 WebUI 有没有收到过页面请求（/ 或 /login），不需要猜。
/// 自检窗口 5 秒（本地页面正常情况下一瞬即达）。
/// </summary>
static class PhotinoGui
{
    public static bool TryRun(string url, Action onClosed, int healthSeconds = 5)
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

            Console.WriteLine("[gui] 原生窗口已打开（关窗即退出；后台无窗口运行请加 --no-gui）");

            var rendered = false;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(healthSeconds));
                if (WebUi.HasServedPage) { rendered = true; return; }
                Console.WriteLine("[gui] 窗口没有渲染出页面（本机 WebView2 环境异常），改用控制台 + 浏览器 WebUI");
                try { window.Close(); } catch { }   // 尽力关掉那个空白窗口
            });

            window.WaitForClose();   // 启动事件循环，阻塞到关窗

            if (!rendered && !WebUi.HasServedPage)
                return false;        // 没能渲染 ⇒ 让调用方退回控制台
            Console.WriteLine("[gui] 原生窗口已关闭，退出程序");
            try { onClosed(); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gui] 原生窗口打开失败: {ex.Message}");
            return false;
        }
    }
}
