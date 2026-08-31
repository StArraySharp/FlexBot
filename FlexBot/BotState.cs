namespace FlexBot;

// 全局运行状态（WebUI 展示用）
static class BotState
{
    public static readonly DateTime StartedAt = DateTime.Now;
    public static string WsUrl = "";
    public static volatile bool Connected;
    public static long SelfId;
}

// 日志环形缓冲：TimestampWriter 捕获每行输出，WebUI 轮询读取；同时落盘 logs/bot_YYYYMMDD.log
static class LogStore
{
    private readonly static object _lock = new();
    private readonly static List<(int Id, string Text)> _lines = [];
    private static int _nextId = 1;

    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "logs");
    private static string LogFile => Path.Combine(LogDir, $"bot_{DateTime.Now:yyyyMMdd}.log");
    private static StreamWriter? _writer;
    private static DateTime _writerDate;

    public const int MaxLines = 3000;
    public const int MaxLineChars = 4000;

    public static void AppendLine(string line)
    {
        lock (_lock)
        {
            _lines.Add((_nextId++, line.Length > MaxLineChars ? line[..MaxLineChars] + "…" : line));
            while (_lines.Count > MaxLines) _lines.RemoveAt(0);
        }
        WriteToFile(line);
    }

    // 追加写入当日日志文件（跨天自动切换；失败静默，不影响控制台/WebUI）
    private static void WriteToFile(string line)
    {
        try
        {
            var today = DateTime.Now.Date;
            if (_writer is null || _writerDate != today)
            {
                _writer?.Dispose();
                Directory.CreateDirectory(LogDir);
                _writer = new StreamWriter(LogFile, append: true) { AutoFlush = true };
                _writerDate = today;
            }
            _writer.WriteLine(line);
        }
        catch
        {
            // 落盘失败不阻断主流程
        }
    }

    public static (int Next, List<(int Id, string Text)> Added) GetAfter(int id)
    {
        lock (_lock)
        {
            var added = _lines.Where(l => l.Id > id).ToList();
            return (_nextId, added);
        }
    }

    public static void Clear()
    {
        lock (_lock) _lines.Clear();
    }
}
