using System.Diagnostics;

namespace FlexBot;

/// <summary>
/// 管理外部进程（NapCat、LLBot）的启动和日志隔离。
/// </summary>
sealed class ProcessManager
{
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
            _napCatProcess = StartProcess(napCatCmd, "napcat.log", out _napCatLogWriter);
            await Task.Delay(2000); // 等待 NapCat 初始化
        }

        if (!string.IsNullOrWhiteSpace(llBotCmd))
        {
            Console.WriteLine($"[proc] 启动 LLBot: {llBotCmd}");
            _llBotProcess = StartProcess(llBotCmd, "llbot.log", out _llBotLogWriter);
            await Task.Delay(2000);
        }
    }

    private Process StartProcess(string cmd, string logFileName, out StreamWriter logWriter)
    {
        var logPath = Path.Combine(_logDir, logFileName);
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
            CreateNoWindow = true
        };

        var proc = Process.Start(psi) ?? throw new Exception($"启动进程失败: {cmd}");

        // 异步读取并转向日志文件
        var writer = logWriter;
        _ = Task.Run(() => RedirectOutput(proc.StandardOutput, writer));
        _ = Task.Run(() => RedirectOutput(proc.StandardError, writer));

        return proc;
    }

    private static async Task RedirectOutput(StreamReader reader, StreamWriter writer)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                await writer.WriteLineAsync($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
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
                _napCatProcess = StartProcess(cmd, "napcat.log", out _napCatLogWriter);
            else
                _llBotProcess = StartProcess(cmd, "llbot.log", out _llBotLogWriter);
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
