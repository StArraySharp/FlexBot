namespace FlexBot;

/// <summary>
/// 日志与缓存目录的卫生工作（启动时跑一次即可）：
///   · 依赖进程日志（LLBot/NapCat）单文件会无限增长 —— 实测出现过 119MB 的 llbot.log，
///     所以在**打开之前**按上限轮转：超限就改名成 .1，若 .1 也已超限直接删掉（历史大文件随之释放）。
///   · 宿主自己的日志按天分文件（bot_YYYYMMDD.log），这里按保留天数清理旧文件。
/// 只做"删自己产生的日志"，不碰 memory/ 下的业务数据与插件目录。
/// </summary>
static class LogHousekeeping
{
    /// <summary>依赖进程日志单文件上限（超出即轮转）</summary>
    public const long ChildLogMaxBytes = 20 * 1024 * 1024;

    /// <summary>宿主日志保留天数</summary>
    public const int HostLogKeepDays = 14;

    /// <summary>打开依赖进程日志文件之前调用：必要时把它挪走，保证新写的文件从 0 开始</summary>
    public static void RotateChildLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var len = new FileInfo(path).Length;
            if (len <= ChildLogMaxBytes) return;

            var rotated = path + ".1";
            try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }
            try { File.Move(path, rotated); }
            catch
            {
                // 挪不动（例如仍被占用）：退而求其次，把内容清空，至少不让它继续涨
                try { using var fs = new FileStream(path, FileMode.Truncate, FileAccess.Write); } catch { }
                Console.WriteLine($"[logs] {Path.GetFileName(path)} 超过 {ChildLogMaxBytes / 1024 / 1024}MB，已截断（原文件被占用无法改名）");
                return;
            }

            var movedLen = new FileInfo(rotated).Length;
            if (movedLen > ChildLogMaxBytes)
            {
                try { File.Delete(rotated); } catch { }
                Console.WriteLine($"[logs] {Path.GetFileName(path)} 已超上限（{movedLen / 1024 / 1024}MB），旧内容直接丢弃");
            }
            else
            {
                Console.WriteLine($"[logs] {Path.GetFileName(path)} 已轮转为 .1（{movedLen / 1024 / 1024}MB）");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[logs] 轮转 {Path.GetFileName(path)} 失败: {ex.Message}"); }
    }

    /// <summary>启动时清理宿主自己的旧日志（bot_YYYYMMDD.log），保留最近 N 天</summary>
    public static void CleanHostLogs(string logDir, int keepDays = HostLogKeepDays)
    {
        try
        {
            if (!Directory.Exists(logDir)) return;
            var cutoff = DateTime.Now.Date.AddDays(-keepDays);
            var removed = 0;
            long freed = 0;
            foreach (var f in Directory.GetFiles(logDir, "bot_*.log"))
            {
                try
                {
                    // 文件名里的日期比内容更可靠；解析不出来就按文件时间
                    var name = Path.GetFileNameWithoutExtension(f);           // bot_20261014
                    var stamp = name.Length >= 12 && DateTime.TryParseExact(name[4..12], "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var d) ? d : File.GetLastWriteTime(f);
                    if (stamp >= cutoff) continue;
                    freed += new FileInfo(f).Length;
                    File.Delete(f);
                    removed++;
                }
                catch { }
            }
            if (removed > 0)
                Console.WriteLine($"[logs] 已清理 {removed} 个超过 {keepDays} 天的宿主日志（释放 {freed / 1024 / 1024}MB）");
        }
        catch (Exception ex) { Console.WriteLine($"[logs] 清理旧日志失败: {ex.Message}"); }
    }

    /// <summary>顺带清理已废弃的目录（例如早期"浏览器应用窗口"实验留下的浏览器 profile，实测占 221MB）</summary>
    public static void CleanObsoleteDirs(string memoryDir)
    {
        try
        {
            var dead = Path.Combine(memoryDir, "browser-profile");
            if (!Directory.Exists(dead)) return;
            long freed = 0;
            try { freed = new DirectoryInfo(dead).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            Directory.Delete(dead, recursive: true);
            Console.WriteLine($"[logs] 已删除废弃目录 memory/browser-profile（释放 {freed / 1024 / 1024}MB）");
        }
        catch (Exception ex) { Console.WriteLine($"[logs] 清理废弃目录失败: {ex.Message}"); }
    }
}
