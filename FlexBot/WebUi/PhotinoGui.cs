using Photino.NET;

namespace FlexBot.WebUi;

/// <summary>窗口模式的结局，决定主流程接下来怎么走</summary>
enum GuiResult
{
    /// <summary>窗口正常打开并被用户关闭 ⇒ 按"关窗即退出"走收尾</summary>
    WindowClosed,
    /// <summary>窗口没能创建（没图形界面/缺 WebView 运行时）⇒ 继续无窗口常驻</summary>
    Unavailable,
    /// <summary>窗口开了但 WebView 没渲染页面，已改用浏览器应用窗口 ⇒ 程序继续常驻</summary>
    FellBackToBrowser,
}

/// <summary>
/// Photino 桌面窗口：加载 WebUI 页面。必须在调用线程（STA 主线程）上建，故不再另起线程。
///
/// 自检与兜底：窗口起来后 10 秒内若 WebUI 一个页面请求都没收到，说明本机 WebView2 环境创建失败
/// （实测特征：窗口纯黑、无页面请求、连 WebView2 用户数据目录都不生成）。此时自动用系统浏览器以
/// 「应用窗口」打开同一地址（<see cref="BrowserWindow"/>），并写下标记文件，让下次启动直接走浏览器窗口。
/// 用 <c>--gui</c> 可强制重试 Photino（会清掉该标记）。
/// </summary>
static class PhotinoGui
{
    public static GuiResult Run(string url, Action onClosed, string? fallbackMarkerPath = null)
    {
        try
        {
            var fellBack = 0;
            var window = new PhotinoWindow()
                .SetLogVerbosity(2)          // Photino/WebView2 日志：窗口类问题只看这个
                .SetDevToolsEnabled(true)    // 页面异常时可按 F12 自看
                // WebView2 启动参数：--disable-gpu 是"黑窗"最经典的对策（软件渲染兜底）；
                // --no-proxy-server 排掉本机代理/加速器对本地页面的干扰
                .SetBrowserControlInitParameters("--disable-gpu --no-proxy-server")
                .SetTitle("FlexBot 控制台")
                .SetUseOsDefaultSize(false)
                .SetSize(1180, 780)
                .Center()
                .SetResizable(true)
                .Load(url);

            Console.WriteLine("[gui] Photino 窗口已打开（关窗即退出程序；如需后台无窗口运行请加 --no-gui）");

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (WebUi.HasServedPage) return;                       // 页面已渲染，一切正常
                if (Interlocked.Exchange(ref fellBack, 1) != 0) return; // 只兜底一次
                Console.WriteLine("[gui] 窗口未渲染出页面：本机 WebView2 环境创建失败，自动改用浏览器应用窗口");
                BrowserWindow.Open(url);
                MarkBroken(fallbackMarkerPath);
                try { window.Close(); } catch { }                     // 尽量关掉那个空白窗口
            });

            window.WaitForClose();
            Console.WriteLine("[gui] 窗口已关闭");
            if (Volatile.Read(ref fellBack) == 1) return GuiResult.FellBackToBrowser;   // 已切浏览器 ⇒ 不退出程序
            try { onClosed(); } catch { }
            return GuiResult.WindowClosed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gui] 打开窗口失败: {ex.Message}（继续以无窗口模式运行，可浏览器打开 WebUI 地址）");
            MarkBroken(fallbackMarkerPath);
            return GuiResult.Unavailable;
        }
    }

    private static void MarkBroken(string? path)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch { }
    }
}
