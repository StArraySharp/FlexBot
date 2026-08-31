using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FlexBot.PluginApi;

namespace PCControlPlugin;

// 电脑控制插件：通过 !pc 命令（管理员）或 AI 的 run_command 工具控制宿主 Windows
// exec 走 PowerShell（-EncodedCommand，UTF-8 输出，超时强杀）；危险操作受设置开关限制
public sealed class PCControlPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;
    private readonly List<IDisposable> _commandSubs = [];

    public string Name => "PCControl";
    public string Version => "1.1.0";
    public string Description => "电脑控制：!pc status|exec|ps|kill|open|screenshot|clip|mouse|key|lock|sleep|shutdown|restart|cancel";

    public IReadOnlyList<PluginSettingDef> SettingDefs =>
    [
        new("AllowShellExec", "允许执行 PowerShell", "bool", "true", "关闭后 pc exec / pc clip 拒绝执行"),
        new("AllowPowerOps", "允许电源操作", "bool", "true", "关闭后 sleep/shutdown/restart/lock 拒绝执行"),
        new("AllowInput", "允许鼠标键盘控制", "bool", "true", "关闭后 pc mouse / pc key 拒绝执行"),
        new("ShellTimeoutSec", "命令超时（秒）", "number", "30", "pc exec 的最长等待时间，超时强制结束进程"),
        new("MaxOutputChars", "输出截断长度", "number", "2500", "命令回显的最大字符数"),
    ];

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;
        _commandSubs.Add(context.RegisterCommand(
            "pc", "控制宿主电脑（仅管理员）",
            RunAsync,
            "pc <status|exec|ps|kill|open|screenshot|clip|mouse|key|lock|sleep|shutdown|restart|cancel> [参数]"));
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync()
    {
        foreach (var sub in _commandSubs) sub.Dispose();
        _commandSubs.Clear();
        _ctx = null!;
        return Task.CompletedTask;
    }

    private async Task<string> RunAsync(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.TrimEntries);
        var sub = (parts.Length > 0 ? parts[0] : "").ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";
        try
        {
            return sub switch
            {
                "" or "help" => Help(),
                "status" => Status(),
                "exec" => GateShell() ? await ExecAsync(rest) : "已禁用：AllowShellExec=false",
                "ps" => Ps(rest),
                "kill" => Kill(rest),
                "open" => Open(rest),
                "screenshot" => Screenshot(),
                "clip" => GateShell() ? await ClipAsync(rest) : "已禁用：AllowShellExec=false",
                "mouse" => GateInput() ? await MouseAsync(rest) : "已禁用：AllowInput=false",
                "key" => GateInput() ? await KeyAsync(rest) : "已禁用：AllowInput=false",
                "lock" => GatePower() ? Lock() : "已禁用：AllowPowerOps=false",
                "sleep" => GatePower() ? Power("sleep") : "已禁用：AllowPowerOps=false",
                "shutdown" => GatePower() ? Power("shutdown", rest) : "已禁用：AllowPowerOps=false",
                "restart" => GatePower() ? Power("restart", rest) : "已禁用：AllowPowerOps=false",
                "cancel" => GatePower() ? Power("cancel") : "已禁用：AllowPowerOps=false",
                _ => $"未知子命令 {sub}\n" + Help()
            };
        }
        catch (Exception ex)
        {
            return $"执行失败: {ex.Message}";
        }
    }

    private static string Help() =>
        """
        电脑控制命令：
          pc status - 电脑状态（运行时间/内存/磁盘）
          pc exec <命令> - 执行 PowerShell 并回显输出（如 pc exec Get-Date）
          pc ps [过滤词] - 进程列表（可按名称过滤）
          pc kill <pid|名称> - 结束进程
          pc open <URL|路径|程序> - 打开网页/文件/程序
          pc screenshot - 截屏保存并返回路径
          pc clip get|set <文本> - 读/写剪贴板
          pc mouse pos|move x y|click [按钮] [x y]|wheel <±格数> - 鼠标控制
          pc key type <文本>|press <组合键> - 键盘输入（如 pc key press ctrl+c、win+r）
          pc lock - 锁屏
          pc sleep - 睡眠
          pc shutdown [秒] - 关机（默认 60 秒）
          pc restart [秒] - 重启（默认 60 秒）
          pc cancel - 取消已计划的关机/重启
        """;

    private bool GateShell() => _ctx.GetSetting("AllowShellExec", true);
    private bool GatePower() => _ctx.GetSetting("AllowPowerOps", true);
    private bool GateInput() => _ctx.GetSetting("AllowInput", true);
    private int TimeoutSec => Math.Clamp(_ctx.GetSetting("ShellTimeoutSec", 30), 5, 300);
    private int MaxOut => Math.Clamp(_ctx.GetSetting("MaxOutputChars", 2500), 200, 8000);

    // ===================== PowerShell 执行 =====================

    private async Task<string> ExecAsync(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return "用法: pc exec <PowerShell 命令>";
        var (code, out1, err, ms) = await RunPowerShellAsync(script, TimeoutSec);
        var sb = new StringBuilder();
        sb.AppendLine($"[退出码 {code}，{ms}ms]");
        if (out1.Length > 0) sb.AppendLine(Truncate(out1.Trim()));
        if (err.Length > 0) sb.AppendLine("[stderr] " + Truncate(err.Trim()));
        return sb.ToString().TrimEnd();
    }

    private static async Task<(int Code, string StdOut, string StdErr, int Ms)> RunPowerShellAsync(string script, int timeoutSec)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var sw = Stopwatch.StartNew();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "（超时，进程已被强制结束）", "", timeoutSec * 1000);
        }
        var stdout = await outTask;
        var stderr = CleanStdErr(await errTask);
        sw.Stop();
        return (p.ExitCode, stdout, stderr, (int)sw.ElapsedMilliseconds);
    }

    // PowerShell 重定向时把进度/警告记录序列化成 CLIXML 写入 stderr：剥掉 XML 噪声，仅保留真实错误文本
    private static string CleanStdErr(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var plain = s;
        var xml = "";
        var idx = s.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (idx >= 0)
        {
            plain = s[..idx];
            xml = s[(idx + 9)..];
        }
        var msgs = new List<string>();
        if (plain.Trim().Length > 0) msgs.Add(plain.Trim());
        foreach (Match m in Regex.Matches(xml, @"<S S=""Error"">([\s\S]*?)</S>"))
            msgs.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim());
        return string.Join("\n", msgs);
    }

    private string Truncate(string s)
    {
        s = s.Replace("\r\n", "\n").TrimEnd();
        return s.Length > MaxOut ? s[..MaxOut] + $"\n…（已截断，共 {s.Length} 字符）" : s;
    }

    // ===================== 子命令实现 =====================

    private string Status()
    {
        var mem = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(mem);
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var sb = new StringBuilder();
        sb.AppendLine($"主机: {Environment.MachineName}（{Environment.OSVersion.VersionString}）");
        sb.AppendLine($"运行时间: {(int)uptime.TotalDays}天 {uptime.Hours}小时 {uptime.Minutes}分");
        sb.AppendLine($"内存: 已用 {mem.dwMemoryLoad}%（可用 {mem.ullAvailPhys / 1048576:N0} / 共 {mem.ullTotalPhys / 1048576:N0} MB）");
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Take(4))
            sb.AppendLine($"磁盘 {d.Name} {d.TotalFreeSpace / 1073741824.0:F0}/{d.TotalSize / 1073741824.0:F0} GB 可用");
        return sb.ToString().TrimEnd();
    }

    private string Ps(string filter)
    {
        var procs = Process.GetProcesses()
            .Where(p => string.IsNullOrEmpty(filter) || p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } })
            .Take(30)
            .ToList();
        if (procs.Count == 0) return $"没有名称包含「{filter}」的进程。";
        var sb = new StringBuilder($"共 {procs.Count} 个进程（按内存排序，前 30）：");
        foreach (var p in procs)
        {
            var mem = 0L;
            var title = "";
            try { mem = p.WorkingSet64; } catch { }
            try { title = p.MainWindowTitle; } catch { }
            sb.AppendLine($"\n{p.Id,7}  {p.ProcessName,-28} {mem / 1048576.0,7:F0}MB  {title}");
        }
        return Truncate(sb.ToString());
    }

    private string Kill(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "用法: pc kill <pid|进程名>";
        var killed = new List<string>();
        if (int.TryParse(target, out var pid))
        {
            try { var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); p.WaitForExit(5000); killed.Add(p.ProcessName); }
            catch (Exception ex) { return $"结束 {pid} 失败: {ex.Message}"; }
        }
        else
        {
            var procs = Process.GetProcessesByName(target);
            if (procs.Length == 0) return $"没有名为「{target}」的进程。";
            foreach (var p in procs)
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); killed.Add(p.ProcessName); }
                catch (Exception ex) { return $"结束 {p.ProcessName}({p.Id}) 失败: {ex.Message}"; }
            }
        }
        return $"已结束: {string.Join(", ", killed)}";
    }

    private string Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "用法: pc open <URL|文件路径|程序名>";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return $"已打开: {target}";
        }
        catch (Exception ex) { return $"打开失败: {ex.Message}"; }
    }

    private string Screenshot()
    {
        var sx = GetSystemMetrics(76); var sy = GetSystemMetrics(77);
        var w = GetSystemMetrics(78); var h = GetSystemMetrics(79);
        if (w <= 0 || h <= 0) return "获取屏幕尺寸失败。";
        var dir = Path.Combine(_ctx.DataDir, "screenshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        using var bmp = new System.Drawing.Bitmap(w, h);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(sx, sy, 0, 0, new Size(w, h));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return $"已截屏（{w}x{h}，含全部显示器）: {path}";
    }

    private async Task<string> ClipAsync(string rest)
    {
        var parts = rest.Split(' ', 2, StringSplitOptions.TrimEntries);
        var op = (parts.Length > 0 ? parts[0] : "").ToLowerInvariant();
        switch (op)
        {
            case "get":
            {
                var (_, stdout, _, _) = await RunPowerShellAsync("Get-Clipboard", TimeoutSec);
                var text = stdout.Trim();
                return text.Length == 0 ? "剪贴板为空（或非文本内容）。" : Truncate(text);
            }
            case "set":
            {
                if (parts.Length < 2 || parts[1].Length == 0) return "用法: pc clip set <文本>";
                var escaped = parts[1].Replace("'", "''").Replace("\n", "`n");
                var (_, _, stderr, _) = await RunPowerShellAsync($"Set-Clipboard -Value '{escaped}'", TimeoutSec);
                return stderr.Length == 0 ? "剪贴板已设置。" : "设置失败: " + Truncate(stderr.Trim());
            }
            default:
                return "用法: pc clip get | pc clip set <文本>";
        }
    }

    private string Lock()
    {
        return LockWorkStation() ? "已锁定。" : "锁屏失败。";
    }

    // ===================== 鼠标 / 键盘 =====================

    private static async Task<string> MouseAsync(string rest)
    {
        var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sub = t.Length > 0 ? t[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "pos":
                return GetCursorPos(out var pt) ? $"当前光标: {pt.X}, {pt.Y}" : "获取光标位置失败。";
            case "move":
            {
                if (t.Length < 3 || !int.TryParse(t[1], out var x) || !int.TryParse(t[2], out var y))
                    return "用法: pc mouse move <x> <y>";
                if (!SetCursorPos(x, y)) return "移动光标失败。";
                await Task.Delay(30);
                return $"光标已移动到 {x}, {y}。";
            }
            case "click":
            {
                var button = "left";
                var idx = 1;
                if (t.Length > idx && t[idx] is "left" or "right" or "middle" or "double")
                {
                    button = t[idx];
                    idx++;
                }
                int? x = null, y = null;
                if (t.Length >= idx + 2 && int.TryParse(t[idx], out var cx) && int.TryParse(t[idx + 1], out var cy))
                {
                    x = cx;
                    y = cy;
                }
                if (x.HasValue) SetCursorPos(x.Value, y!.Value);
                await Task.Delay(60);
                if (button == "double")
                {
                    await TapMouseAsync(2, 4); // LEFTDOWN/LEFTUP
                    await Task.Delay(40);
                    await TapMouseAsync(2, 4);
                }
                else
                {
                    var (down, up) = button switch { "right" => (8u, 16u), "middle" => (32u, 64u), _ => (2u, 4u) };
                    await TapMouseAsync(down, up);
                }
                var where = x.HasValue ? $"点击 {x.Value},{y!.Value} " : "点击";
                return $"已{where}（{button}）。";
            }
            case "wheel":
            {
                if (t.Length < 2 || !int.TryParse(t[1], out var delta) || delta == 0)
                    return "用法: pc mouse wheel <格数>（正数向上，负数向下，如 3 或 -3）";
                mouse_event(0x0800, 0, 0, (uint)(Math.Sign(delta) * Math.Min(Math.Abs(delta), 20) * 120), UIntPtr.Zero);
                return $"已滚动 {delta} 格（正=向上）。";
            }
            default:
                return "用法: pc mouse pos | pc mouse move <x> <y> | pc mouse click [left|right|middle|double] [x y] | pc mouse wheel <±格数>";
        }
    }

    private static async Task TapMouseAsync(uint down, uint up)
    {
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(15);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    private static async Task<string> KeyAsync(string rest)
    {
        var sp = rest.IndexOf(' ');
        var sub = (sp < 0 ? rest : rest[..sp]).ToLowerInvariant();
        var arg = sp < 0 ? "" : rest[(sp + 1)..].Trim();
        switch (sub)
        {
            case "type":
            {
                if (arg.Length == 0) return "用法: pc key type <文本>（\\n 表示换行）";
                if (arg.Length > 800) arg = arg[..800];
                arg = arg.Replace("\\n", "\n").Replace("\\t", "\t");
                foreach (var ch in arg)
                {
                    SendKey(0, ch, 4);          // KEYEVENTF_UNICODE
                    SendKey(0, ch, 4 | 2);      // + KEYEVENTF_KEYUP
                    await Task.Delay(8);
                }
                return $"已输入 {arg.Length} 个字符。";
            }
            case "press":
            {
                if (arg.Length == 0) return "用法: pc key press <组合键>（如 ctrl+c、win+r、enter、alt+f4）";
                var names = arg.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (names.Length == 0) return "组合键为空。";
                if (names.Any(n => VkFromName(n) == 0)) return "无法识别的键名: " + string.Join(", ", names.Where(n => VkFromName(n) == 0));
                // 末位是主键，其余是修饰键：按住修饰 → 敲主键 → 反序松开
                var keys = names.Select(VkFromName).ToList();
                for (var i = 0; i < keys.Count - 1; i++) SendKey(keys[i], 0, 0);
                await Task.Delay(30);
                SendKey(keys[^1], 0, 0);
                await Task.Delay(15);
                SendKey(keys[^1], 0, 2);
                await Task.Delay(30);
                for (var i = keys.Count - 2; i >= 0; i--) SendKey(keys[i], 0, 2);
                return $"已按下 {arg}。";
            }
            default:
                return "用法: pc key type <文本> | pc key press <组合键>（ctrl+c / win+r / enter / alt+f4 …）";
        }
    }

    // 键名 → 虚拟键码；单字母/数字直接取 ASCII；返回 0 表示无法识别
    private static ushort VkFromName(string name)
    {
        name = name.Trim().ToLowerInvariant();
        if (name.Length == 1)
        {
            var c = name[0];
            if (c is >= 'a' and <= 'z') return (ushort)(c - 'a' + 'A');
            if (c is >= '0' and <= '9') return c;
        }
        if (name.Length == 2 && name[0] == 'f' && name[1] is >= '1' and <= '9')
            return (ushort)(0x70 + name[1] - '1');
        return name switch
        {
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "space" => 0x20,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "ctrl" or "control" => 0x11,
            "alt" => 0x12,
            "shift" => 0x10,
            "win" or "windows" => 0x5B,
            "capslock" => 0x14,
            "numlock" => 0x90,
            "scrolllock" => 0x91,
            "printscreen" or "prtsc" => 0x2C,
            "pause" => 0x13,
            "plus" or "=" => 0xBB,
            "minus" or "-" => 0xBD,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            _ => 0
        };
    }

    private static uint SendKey(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT
        {
            type = 1, // INPUT_KEYBOARD
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags }
        };
        return SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private string Power(string op, string rest = "")
    {
        switch (op)
        {
            case "sleep":
                Process.Start("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
                return "已下发睡眠。";
            case "cancel":
                Process.Start("shutdown.exe", "/a");
                return "已取消计划中的关机/重启。";
            case "shutdown" or "restart":
            {
                var delay = 60;
                if (rest.Length > 0 && (!int.TryParse(rest, out delay) || delay < 0))
                    return "延迟秒数无效（应为非负整数）。";
                var arg = op == "shutdown" ? "/s /t " : "/r /t ";
                Process.Start("shutdown.exe", arg + delay + " /c \"FlexBot PCControl\"");
                return $"已计划{(op == "shutdown" ? "关机" : "重启")}，{delay} 秒后执行（pc cancel 可取消）。";
            }
            default:
                return "未知操作。";
        }
    }

    // ===================== Win32 =====================

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }

    // 显式布局并固定 Size=40（x64/ARM64 的真实 INPUT 大小：4 类型 + 4 填充 + 32 MOUSEINPUT 联合体）
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
