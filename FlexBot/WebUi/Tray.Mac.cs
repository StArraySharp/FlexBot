namespace FlexBot.WebUi;

/// <summary>
/// macOS 托盘：**当前刻意不实现**，返回 false 让调用方保持"关窗即退出"。
///
/// 原因（不是偷懒，是接口层面的硬约束）：
///   · 托盘图标本身能做：NSStatusBar.systemStatusBar → statusItemWithLength:，再给 button 设 image/target/action。
///   · 但"关窗收进托盘"要求拦住关闭：Cocoa 里唯一的挂点是 NSWindow 的 delegate
///     （windowShouldClose: 返回 NO，再 orderOut:）。而 Photino **自己就是那个 delegate** ——
///     抢过来会让它的尺寸/移动/焦点等回调全部失效，属于"看起来能用、实际埋雷"。
///   · Photino 暴露的 WindowClosing 只是通知事件（没有 Cancel），窗口照样销毁；
///     销毁后再重建窗口在 Photino 里没有受支持的路径。
///
/// 所以 macOS 上正确的产品形态是：要么等 Photino 支持可取消的关闭/托盘，
/// 要么用 --no-gui + 浏览器 WebUI 常驻（macOS 用户本来就习惯"关窗不退出应用"，
/// 但我们的宿主没有 NSApplication 生命周期，无法照搬这套约定）。
///
/// 如果将来要强上：objc_msgSend 接管 delegate 并**把未知 selector 转发回原 delegate**
/// （forwardingTargetForSelector:），这需要真机验证，不能在 Windows 上盲写。
/// </summary>
static class TrayMac
{
    internal static bool TryInstall(IntPtr hwnd, string tooltip, Func<Task> onOpen, Action onQuit, out string? reason)
    {
        reason = "macOS 需接管 NSWindow delegate 才能拦关闭，与 Photino 冲突；请用 --no-gui + 浏览器 WebUI 常驻";
        return false;
    }

    /// <summary>macOS 下没有装载托盘，恢复窗口无事可做（保留签名以便平台分发统一调用）。</summary>
    internal static void ShowAppWindow() { }

    /// <summary>macOS 下没有托盘图标需要清理。</summary>
    internal static void Remove() { }
}
