namespace FlexBot.WebUi;

/// <summary>
/// 托盘：把"关窗"变成"收进托盘"，单击托盘图标再把窗口叫回来。
///
/// 为什么必须自己实现：Photino.NET 没有托盘 API，<c>WindowClosing</c> 也只是通知事件
/// （没有 Cancel 成员），拦不住关闭。所以关闭拦截只能在**原生层**做，各平台手段不同：
///   · Windows：子类化窗口过程，吞掉 WM_CLOSE 改成 ShowWindow(SW_HIDE)
///   · Linux：监听 GTK 的 delete-event，返回 TRUE 并 gtk_widget_hide
///   · macOS：接管 NSWindow 的 delegate，windowShouldClose 返回 NO 并 orderOut
///
/// 三条路径都做到"窗口对象还在、进程常驻、状态不丢"，比"关掉再重建窗口"好
/// （重建会重新加载页面、丢滚动位置与登录态）。
///
/// 失败策略：拿不到原生句柄、或平台互操作失败，就**退回原来的行为**（关窗即退出），
/// 只打一行可读原因 —— 不允许因为托盘装不上把宿主拖死。
/// </summary>
static class Tray
{
    /// <summary>用户选了"退出"：此时关闭要真的放行。</summary>
    public static bool Quitting { get; private set; }

    /// <summary>
    /// 尝试装载托盘。<paramref name="hwnd"/> 是原生窗口句柄（Windows: HWND / Linux: GtkWindow* / macOS: NSWindow*）。
    /// 返回 false 时调用方应保持"关窗即退出"。
    /// </summary>
    /// <param name="onOpen">单击托盘图标：把窗口叫回来</param>
    /// <param name="onQuit">托盘菜单"退出"：真正关闭窗口</param>
    public static bool TryInstall(IntPtr hwnd, string tooltip, Func<Task> onOpen, Action onQuit)
    {
        // 注意：hwnd 允许为 0 —— Photino 的 WindowHandle 只在 Windows 可用（Linux/macOS 会抛异常），
        // 所以各平台实现可以自己去找原生窗口（Linux 用 gtk_window_list_toplevels 枚举）。
        string? reason;
        try
        {
            bool ok;
            if (OperatingSystem.IsWindows())
                ok = TrayWindows.TryInstall(hwnd, tooltip, onOpen, onQuit, out reason);
            else if (OperatingSystem.IsLinux())
                ok = TrayLinux.TryInstall(hwnd, tooltip, onOpen, onQuit, out reason);
            else if (OperatingSystem.IsMacOS())
                ok = TrayMac.TryInstall(hwnd, tooltip, onOpen, onQuit, out reason);
            else
            {
                reason = "未知平台";
                ok = false;
            }
            Console.WriteLine(ok
                ? "[tray] 已启用：关窗收进托盘，单击图标恢复（右键可退出）"
                : $"[tray] 未启用（{reason}）：关窗即退出");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[tray] 装载失败（{ex.GetType().Name}: {ex.Message}），保持“关窗即退出”");
            return false;
        }
    }

    /// <summary>托盘"退出"：置位后关闭窗口，让原生关闭路径放行。</summary>
    public static void RequestQuit(Action onQuit)
    {
        Quitting = true;
        try { onQuit(); } catch (Exception ex) { Console.WriteLine($"[tray] 退出失败: {ex.Message}"); }
    }

    /// <summary>把窗口叫回前台（按平台分发；未装托盘时是空操作）。</summary>
    public static void ShowWindow()
    {
        if (OperatingSystem.IsWindows()) TrayWindows.ShowAppWindow();
        else if (OperatingSystem.IsLinux()) TrayLinux.ShowAppWindow();
        else if (OperatingSystem.IsMacOS()) TrayMac.ShowAppWindow();
    }

    /// <summary>收尾：移除托盘图标并还原窗口过程（不清理会让图标残留到鼠标划过为止）。</summary>
    public static void RemoveTray()
    {
        if (OperatingSystem.IsWindows()) TrayWindows.Remove();
        else if (OperatingSystem.IsLinux()) TrayLinux.Remove();
        else if (OperatingSystem.IsMacOS()) TrayMac.Remove();
    }
}
