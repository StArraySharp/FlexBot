using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FlexBot;

/// <summary>
/// 管理外部进程（NapCat、LLBot）的启动和日志隔离。
/// 子进程输出按 UTF-8 解码，控制台带 [NapCat]/[LLBot] 标签与 ANSI 颜色转发。
/// </summary>
sealed class ProcessManager
{
    /// <summary>匹配 ANSI CSI 转义序列（颜色/光标控制等），用于写入日志文件前剥离</summary>
    private static readonly Regex AnsiRegex = new("\u001B\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private readonly string _logDir;
    private Process? _napCatProcess;
    private Process? _llBotProcess;
    private StreamWriter? _napCatLogWriter;
    private StreamWriter? _llBotLogWriter;

    public ProcessManager(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(_logDir);
    }

    /// <summary>启动外部依赖进程（NapCat/LLBot）</summary>
    public async Task StartDependenciesAsync(string napCatCmd, string llBotCmd)
    {
        if (!string.IsNullOrWhiteSpace(napCatCmd))
        {
            Console.WriteLine($"[proc] 启动 NapCat: {napCatCmd}");
            _napCatProcess = StartProcess(napCatCmd, "NapCat", "napcat.log", out _napCatLogWriter);
            await Task.Delay(2000); // 等待 NapCat 初始化
        }

        if (!string.IsNullOrWhiteSpace(llBotCmd))
        {
            Console.WriteLine($"[proc] 启动 LLBot: {llBotCmd}");
            _llBotProcess = StartProcess(llBotCmd, "LLBot", "llbot.log", out _llBotLogWriter);
            await Task.Delay(2000);
        }
    }

    private Process StartProcess(string cmd, string tag, string logFileName, out StreamWriter logWriter)
    {
        var logPath = Path.Combine(_logDir, logFileName);
        // 依赖进程日志会无限增长（实测出现过 119MB 的 llbot.log）⇒ 打开之前先按上限轮转
        LogHousekeeping.RotateChildLog(logPath);
        logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };

        // 跨平台：Windows 走 cmd.exe，Linux/macOS 走 bash（.bat/.sh 由各自 shell 解释）
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c \"{cmd}\"" : $"-c \"{cmd.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // NapCat/LLBot 均为 Node.js 系，管道输出为 UTF-8；不显式指定会按控制台编码（GBK）解码导致中文乱码
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var proc = Process.Start(psi) ?? throw new Exception($"启动进程失败: {cmd}");

        // 异步读取：转写日志文件，并同时打印到宿主控制台（带 [NapCat]/[LLBot] 标签）
        var writer = logWriter;
        _ = Task.Run(() => RedirectOutput(proc.StandardOutput, writer, tag));
        _ = Task.Run(() => RedirectOutput(proc.StandardError, writer, tag));

        return proc;
    }

    private static async Task RedirectOutput(StreamReader reader, StreamWriter writer, string tag)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                // 控制台原样透传（保留 ANSI 颜色），末尾补 reset 防止颜色溢出到宿主后续输出
                Console.WriteLine($"[{tag}] {line}\u001B[0m");
                // 日志文件剥离 ANSI 转义码，保持纯文本可读
                var clean = AnsiRegex.Replace(line, "");
                await writer.WriteLineAsync($"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {clean}");
            }
        }
        catch { }
    }

    /// <summary>单进程启动（WebUI 手动触发；which = napcat / llbot）。已在跑则不重复启动。</summary>
    public bool StartOne(string which, string cmd, out string message)
    {
        var (running, name) = which == "napcat" ? (IsNapCatRunning(), "NapCat") : (IsLlBotRunning(), "LLBot");
        if (string.IsNullOrWhiteSpace(cmd)) { message = $"{name} 启动命令未配置"; return false; }
        if (running) { message = $"{name} 已在运行，无需重复启动"; return true; }
        try
        {
            Console.WriteLine($"[proc] 手动启动 {name}: {cmd}");
            if (which == "napcat")
                _napCatProcess = StartProcess(cmd, "NapCat", "napcat.log", out _napCatLogWriter);
            else
                _llBotProcess = StartProcess(cmd, "LLBot", "llbot.log", out _llBotLogWriter);
            message = $"{name} 启动命令已下发（就绪需几秒）";
            return true;
        }
        catch (Exception ex)
        {
            message = $"{name} 启动失败: {ex.Message}";
            return false;
        }
    }

    public bool IsNapCatRunning() => _napCatProcess is { HasExited: false };
    public bool IsLlBotRunning() => _llBotProcess is { HasExited: false };

    /// <summary>等待所有依赖进程退出</summary>
    public async Task WaitForDependenciesAsync()
    {
        var tasks = new List<Task>();
        if (_napCatProcess is not null)
            tasks.Add(Task.Run(() => _napCatProcess.WaitForExit()));
        if (_llBotProcess is not null)
            tasks.Add(Task.Run(() => _llBotProcess.WaitForExit()));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    /// <summary>清理资源</summary>
    public void Dispose()
    {
        try { _napCatProcess?.Kill(entireProcessTree: true); } catch { }
        try { _llBotProcess?.Kill(entireProcessTree: true); } catch { }
        try { _napCatLogWriter?.Dispose(); } catch { }
        try { _llBotLogWriter?.Dispose(); } catch { }
    }
}
