using System.Diagnostics;

namespace FlexBot.WebUi;

/// <summary>运行形态：有应用窗口，还是纯控制台</summary>
enum RunMode
{
    /// <summary>开一个应用窗口（默认）：浏览器以 --app 模式加载 WebUI，控制台收起</summary>
    AppWindow,
    /// <summary>只跑控制台：不开窗口，日志直接在控制台看</summary>
    ConsoleOnly,
}

/// <summary>
/// 启动形态的唯一决策与执行点（重写后：**没有自适应魔法、没有标记文件、没有自检等待**）。
///
/// 规则（一句话版本）
///   默认        → 开应用窗口：用系统 Chrome/Edge 以 --app=URL 打开 WebUI（专属 profile ⇒ 复用同一个窗口），控制台收起
///   --native    → 改用进程内的 Photino 原生窗口（依赖 WebView2；失败则提示改用默认模式，不做静默兜底）
///   --no-gui    → 不开窗口，控制台常驻
///   --console   → 即使开窗口也保留控制台
///   --no-web    → 连 WebUI 都不开（纯机器人）
///
/// 为什么默认不用 Photino：本机 WebView2 环境可能创建失败（窗口纯黑、无任何页面请求），
/// 而系统浏览器一定能渲染。默认走浏览器应用窗口 ⇒ 启动即可见、行为稳定、无需猜测。
/// </summary>
static class AppShell
{
    /// <summary>纯函数式判定：只看参数与"WebUI 是否可用"，便于阅读与测试</summary>
    public static RunMode ResolveMode(string[] args, bool webUiAvailable)
    {
        if (!webUiAvailable) return RunMode.ConsoleOnly;                 // 没开 WebUI 就没窗口可开
        if (args.Contains("--no-gui") || args.Contains("--headless")) return RunMode.ConsoleOnly;
        return RunMode.AppWindow;                                        // 默认
    }

    /// <summary>是否需要收起控制台（Windows 有效）：开了窗口且没要求保留控制台</summary>
    public static bool ShouldHideConsole(string[] args, RunMode mode) =>
        mode == RunMode.AppWindow && !args.Contains("--console");

    /// <summary>
    /// 打开应用窗口。专属 profile 让 Chrome/Edge 复用同一个窗口（反复启动不会堆窗口），
    /// 并顺手清掉历史遗留的同类窗口（早期版本没带 profile，会越开越多）。
    /// </summary>
    public static bool OpenAppWindow(string url, string profileDir)
    {
        try
        {
            Directory.CreateDirectory(profileDir);

            var exe = FindChromium();
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe, $"--app={url} --user-data-dir=\"{profileDir}\"")
                {
                    UseShellExecute = false,
                });
                Console.WriteLine($"[gui] 应用窗口已打开: {Path.GetFileName(exe)} --app={url}（profile: {profileDir}）");
                return true;
            }

            // 没有 Chrome/Edge：交给系统默认浏览器（会带地址栏，但至少能用）
            if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsLinux()) Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Console.WriteLine($"[gui] 未找到 Chrome/Edge，已用系统默认浏览器打开: {url}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gui] 打开应用窗口失败: {ex.Message}（可手动浏览器访问 {url}，或用 --no-gui 启动）");
            return false;
        }
    }

    private static string? FindChromium()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            candidates.AddRange(
            [
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            ]);
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.AddRange(
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            ]);
        }
        else
        {
            foreach (var n in new[] { "google-chrome", "chromium", "chromium-browser", "microsoft-edge" })
            {
                var hit = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                    .Select(dir => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
                if (hit is not null) candidates.Add(hit);
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
