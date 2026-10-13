using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlexBot.WebUi;

/// <summary>运行形态</summary>
enum RunMode
{
    /// <summary>原生应用窗口（Photino + WebView2），默认</summary>
    AppWindow,
    /// <summary>纯控制台：不开窗口，浏览器手动打开 WebUI</summary>
    ConsoleOnly,
}

/// <summary>
/// 启动形态判定（唯一参数：<c>--no-gui</c>）。
///
/// 规则
///   默认      → 原生应用窗口（Photino 加载 WebUI 页面），进程用 WinExe 子系统 ⇒ 没有多余的控制台窗口
///   --no-gui  → 不开窗口：按需开通控制台，浏览器手动打开控制台打印的地址
///
/// 什么时候不走窗口（自动退化为控制台 + WebUI）：
///   · 没有图形界面：Linux 未设置 DISPLAY/WAYLAND_DISPLAY；Windows 处于非交互会话（服务/会话 0）
///   · Windows 上没有可用的 WebView2 运行时（注册表里查不到版本）
///   · 窗口起来了但始终渲染不出页面（见 PhotinoGui 的自检）
/// </summary>
static class AppShell
{
    public static RunMode ResolveMode(string[] args) =>
        args.Contains("--no-gui") ? RunMode.ConsoleOnly : RunMode.AppWindow;

    /// <summary>当前环境能否开原生窗口；不能时给出可读原因（写进日志）</summary>
    public static bool PreferWindow(out string reason)
    {
        reason = "";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!Environment.UserInteractive) { reason = "Windows 非交互会话（服务/会话 0），没有桌面"; return false; }
                var v = WebView2Version();
                if (string.IsNullOrEmpty(v)) { reason = "未安装 WebView2 运行时"; return false; }
                return true;
            }

            if (OperatingSystem.IsMacOS()) return true;

            // Linux 等：必须有显示会话
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                reason = "未检测到 X11/Wayland 显示会话";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = $"环境探测失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>已安装的 WebView2 运行时版本（读注册表；查不到返回空串）</summary>
    private static string WebView2Version()
    {
        const string id = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var sub in new[]
                 {
                     $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{id}",
                     $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{id}",
                 })
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("pv") is string pv && pv.Length > 0) return pv;
            }
            catch { }
        }
        return "";
    }
}
