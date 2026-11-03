using Photino.NET;

namespace FlexBot.WebUi;

/// <summary>
/// 原生应用窗口：严格按官方示例的写法（[STAThread] Main → new PhotinoWindow().…Load(url) → WaitForClose()），
/// 在**调用线程**（STA 主线程）上创建，不另起线程、不做任何"聪明"的包装。
/// 参考：https://github.com/tryphotino/photino.Samples（Photino.HelloPhotino.NET/Program.cs）
///
/// 托盘（见 Tray.cs）：装上托盘后"关窗"被翻译成"隐藏"，进程与 WebUI 都还在；
/// 单击托盘图标恢复窗口，右键菜单里可以真正退出。托盘装不上时保持老行为（关窗即退出）。
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
            // WebView2 的硬性约束：**不同程序不能共用同一个用户数据目录（UDF）**，
            // 并发访问会导致环境创建失败（窗口纯黑或初始化卡死）。
            // Photino 的默认 UDF 是 %LOCALAPPDATA%\Photino —— 同一台机器上跑第二个 Photino 程序就会互抢，
            // 实测现象就是"FlexBot 一开，另一个 Photino 应用黑屏/崩溃；关掉就好"。
            // 官方给的开关是环境变量 WEBVIEW2_USER_DATA_FOLDER，必须在创建窗口之前设置。
            var udf = Path.Combine(AppContext.BaseDirectory, "memory", "webview2");
            try
            {
                Directory.CreateDirectory(udf);
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", udf);
            }
            catch { }
            var window = new PhotinoWindow()
                .SetTitle("FlexBot 控制台");
            // 窗口图标：读 Assets/flexbot.ico（随输出目录发布）。
            // 图标缺失/不支持都不该影响窗口本身，因此单独包裹，不让它拖垮整个启动链。
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "flexbot.ico");
                if (File.Exists(iconPath)) window.SetIconFile(iconPath);
                else Console.WriteLine($"[gui] 未找到窗口图标: {iconPath}");
            }
            catch (Exception ex) { Console.WriteLine($"[gui] 设置窗口图标失败（忽略）: {ex.Message}"); }
            window
                .SetUseOsDefaultSize(false)
                .SetSize(1180, 780)
                .Center()
                .SetResizable(true)
                .Load(url);

            // 托盘必须等原生窗口真正创建出来才有句柄：Photino 的 WindowHandle 在初始化完成前
            // 会抛 "The Photino window is not initialized yet"（实测过），所以挂到 WindowCreated 上。
            // 装不上就保持"关窗即退出"，绝不因为托盘失败影响主流程。
            var trayReady = false;
            window.WindowCreatedHandler += (_, _) =>
            {
                try
                {
                    // Photino 的 WindowHandle 只在 Windows 提供，Linux/macOS 会抛
                    // "WindowHandle is only supported on Windows."（实测）——
                    // 拿不到就传 0，让各平台自己去枚举原生窗口。
                    var handle = IntPtr.Zero;
                    try { handle = window.WindowHandle; } catch { }

                    trayReady = Tray.TryInstall(
                        handle,
                        "FlexBot 控制台",
                        onOpen: () => { Tray.ShowWindow(); return Task.CompletedTask; },
                        onQuit: () => { try { window.Close(); } catch { } });
                }
                catch (Exception ex) { Console.WriteLine($"[tray] 初始化异常（忽略）: {ex.Message}"); }

                Console.WriteLine(trayReady
                    ? "[gui] 原生窗口已打开（关窗收进托盘，单击托盘图标恢复；右键→退出）"
                    : "[gui] 原生窗口已打开（关窗即退出；后台无窗口运行请加 --no-gui）");
            };

            var rendered = false;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(healthSeconds));
                if (WebUi.HasServedPage) { rendered = true; return; }
                Console.WriteLine("[gui] 窗口没有渲染出页面（本机 WebView2 环境异常），改用控制台 + 浏览器 WebUI");
                try { window.Close(); } catch { }   // 尽力关掉那个空白窗口
            });

            window.WaitForClose();   // 启动事件循环，阻塞到真正关窗（托盘模式下"隐藏"不会走到这里）

            try { Tray.RemoveTray(); } catch { }   // 托盘图标不清理会残留到鼠标划过为止

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
