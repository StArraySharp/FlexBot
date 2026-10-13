using System.Diagnostics;

namespace FlexBot.WebUi;

/// <summary>
/// 用系统里的 Chromium 浏览器（Chrome/Edge）以「应用窗口」（<c>--app=</c>，无地址栏、无标签栏）打开 WebUI。
///
/// 为什么需要它：Photino 依赖 WebView2 运行时，而 WebView2 的环境创建在部分机器上会静默失败
/// （实测某机：窗口能开、画面纯黑、连一个页面请求都没有，且不生成用户数据目录；原生库、运行时、
/// 策略、代理都已排除）。此时最稳的"有窗口"方案就是复用用户机器上已经能正常渲染的浏览器。
///
/// 失败（找不到浏览器）返回 false，调用方回退到纯控制台 + 浏览器手动访问。
/// </summary>
static class BrowserWindow
{
    /// <summary>尝试以应用窗口方式打开；返回是否成功发起</summary>
    public static bool Open(string url)
    {
        try
        {
            var exe = FindChromium();
            var args = $"--app={url} --new-window";
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                Console.WriteLine($"[gui] 已用浏览器应用窗口打开: {Path.GetFileName(exe)} --app={url}");
                return true;
            }

            // 退而求其次：交给系统默认浏览器（会带地址栏，但至少能看到界面）
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            Console.WriteLine($"[gui] 未找到 Chrome/Edge，已用系统默认浏览器打开: {url}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gui] 打开浏览器失败: {ex.Message}（请手动访问上面的 WebUI 地址）");
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
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
            ]);
        }
        else
        {
            foreach (var name in new[] { "google-chrome", "chromium", "chromium-browser", "microsoft-edge", "brave-browser" })
            {
                var found = Which(name);
                if (found is not null) candidates.Add(found);
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? Which(string name)
    {
        try
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            return paths.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }
}
