using Photino.NET;

namespace FlexBot.WebUi;

/// <summary>
/// 进程内的原生窗口（`--native`）：Photino + WebView2。
///
/// 规则：**必须在 STA 主线程上创建**（Program.Main 已标 [STAThread]），否则 WebView2 环境创建会卡住。
/// 失败只报告并返回 false，由调用方决定怎么办 —— **不做静默兜底**（以前那套"开黑窗 10 秒再自动切浏览器"
/// 反而是麻烦的来源）。
///
/// 默认不用它：WebView2 环境在部分机器上创建失败时，表现是"窗口纯黑、连一个页面请求都没有"，
/// 而系统浏览器一定能渲染 —— 所以默认走浏览器应用窗口（见 AppShell）。
/// </summary>
static class PhotinoGui
{
    /// <summary>打开原生窗口并阻塞到关闭；返回 false 表示窗口没能创建</summary>
    public static bool TryRun(string url)
    {
        try
        {
            var window = new PhotinoWindow()
                .SetTitle("FlexBot 控制台")
                .SetUseOsDefaultSize(false)
                .SetSize(1180, 780)
                .Center()
                .SetResizable(true)
                .SetDevToolsEnabled(true)
                .Load(url);

            Console.WriteLine("[gui] 原生窗口已打开（关窗即退出；如需后台无窗口运行请加 --no-gui）");
            // 无魔法提示：WebView2 环境异常的机器上，窗口会一直空白且不产生任何页面请求。
            // 不做自动切换（那套兜底反而添乱），只在这里把话说明白。
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (!WebUi.HasServedPage)
                    Console.WriteLine("[gui] 提示：窗口已开但页面始终没加载 ⇒ 本机 WebView2 环境异常（窗口可能纯黑）。" +
                                      "请改用默认模式（去掉 --native）——那会用系统浏览器开应用窗口；" +
                                      "或加 --no-gui 用浏览器手动访问 WebUI 地址。");
            });
            window.WaitForClose();
            Console.WriteLine("[gui] 原生窗口已关闭");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gui] 原生窗口打开失败: {ex.Message}");
            return false;
        }
    }
}
