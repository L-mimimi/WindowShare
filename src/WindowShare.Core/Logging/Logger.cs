namespace WindowShare.Core.Logging;

/// <summary>日志级别</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
}

/// <summary>
/// 轻量级线程安全日志：控制台 + 文件（按天滚动，自动清理 7 天前日志）。
/// 通过 <see cref="LogEmitted"/> 事件可把日志转发到 UI 界面。
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static readonly Queue<(DateTime Time, LogLevel Level, string Category, string Message)> Recent =
        new(); // 最近 200 条，供 UI 查询
    private static StreamWriter? _writer;
    private static string? _writerDate;
    private static LogLevel _minLevel = LogLevel.Info;

    /// <summary>每条日志的 UI 转发事件（参数：级别/时间/消息）</summary>
    public static event Action<LogLevel, DateTime, string>? LogEmitted;

    /// <summary>
    /// 初始化日志系统（幂等，重复调用只更新级别）。
    /// 级别可用环境变量 WINDOWSHARE_LOG_LEVEL 覆盖：Trace/Debug/Info/Warn/Error。
    /// </summary>
    public static void Initialize(LogLevel minLevel = LogLevel.Info)
    {
        var env = Environment.GetEnvironmentVariable("WINDOWSHARE_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(env) &&
            Enum.TryParse<LogLevel>(env, true, out var parsed))
        {
            minLevel = parsed;
        }

        lock (Gate)
        {
            _minLevel = minLevel;
            Utils.AppPaths.EnsureDirectories();
        }
    }

    public static void Trace(string category, string message) => Write(LogLevel.Trace, category, message, null);
    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message, null);
    public static void Error(string category, string message) => Write(LogLevel.Error, category, message, null);

    public static void Error(string category, string message, Exception ex) =>
        Write(LogLevel.Error, category, $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex);

    /// <summary>最近日志（UI 展示用）</summary>
    public static IReadOnlyList<string> GetRecent(int count = 100)
    {
        lock (Gate)
        {
            return Recent.TakeLast(count)
                .Select(e => $"{e.Time:HH:mm:ss.fff} [{e.Level}] [{e.Category}] {e.Message}")
                .ToList();
        }
    }

    /// <summary>读取本进程级别（测试用）</summary>
    public static LogLevel CurrentLevel { get { lock (Gate) return _minLevel; } }

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        lock (Gate)
        {
            if (level < _minLevel) return;
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{category}] {message}";
            if (ex != null)
                line += $" || {ex}";

            // 控制台
            try { Console.WriteLine(line); } catch { /* 控制台不可用时忽略 */ }

            // 内存环形缓存
            Recent.Enqueue((now, level, category, message));
            while (Recent.Count > 200) Recent.Dequeue();

            // 文件（按天滚动）
            try
            {
                var date = now.ToString("yyyy-MM-dd");
                if (_writer == null || _writerDate != date)
                {
                    _writer?.Dispose();
                    _writerDate = date;
                    var path = Path.Combine(Utils.AppPaths.Logs, $"windowshare-{date}.log");
                    _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                    CleanupOldLogs();
                }
                _writer.WriteLine(line);
            }
            catch
            {
                // 日志文件写入失败不影响主流程
            }

            // UI 转发（注意：不要在事件里做重活，由订阅方自行调度到 UI 线程）
            try { LogEmitted?.Invoke(level, now, $"[{category}] {message}"); } catch { }
        }
    }

    /// <summary>清理 7 天前的日志文件</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var dir = Utils.AppPaths.Logs;
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.GetFiles(dir, "windowshare-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* 清理失败忽略 */ }
    }
}
