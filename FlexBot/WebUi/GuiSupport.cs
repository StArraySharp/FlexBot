namespace FlexBot.WebUi;

/// <summary>
/// 判断当前环境有没有可用的图形界面（决定默认是开窗口还是退化成 WebUI）。
/// 只做保守探测：不确定时当作"没有"，让调用方回退到无窗口模式 —— 反之误判会让程序在服务器上白起一个窗口。
/// </summary>
static class GuiSupport
{
    /// <summary>强制要求开窗口（--gui）：此时不再探测，起不来就由调用方回退</summary>
    public static bool Available
    {
        get
        {
            try
            {
                // Windows：服务/Session 0 里 UserInteractive 为 false，没桌面可画
                if (OperatingSystem.IsWindows()) return Environment.UserInteractive;
                // macOS：用户会话通常都有 Aqua；SSH 场景由窗口创建失败兜底
                if (OperatingSystem.IsMacOS()) return true;
                // Linux 等：看有没有显示服务（X11 / Wayland）
                return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            }
            catch { return false; }
        }
    }

    /// <summary>描述探测结果，用于启动日志（方便排查"为什么没窗口"）</summary>
    public static string Describe()
    {
        if (OperatingSystem.IsWindows()) return Environment.UserInteractive ? "Windows 用户会话" : "Windows 非交互会话（服务/会话 0）";
        if (OperatingSystem.IsMacOS()) return "macOS";
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!string.IsNullOrEmpty(display)) return $"X11 DISPLAY={display}";
        if (!string.IsNullOrEmpty(wayland)) return $"Wayland WAYLAND_DISPLAY={wayland}";
        return "未设置 DISPLAY/WAYLAND_DISPLAY";
    }
}
