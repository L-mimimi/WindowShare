namespace WindowShare.Core.Logging;

/// <summary>
/// 全局异常兜底：任何未处理异常都先落盘，尽量不静默消失。
///
/// 三层覆盖：
///   1) UI 线程（WPF Dispatcher）：由 App 订阅 DispatcherUnhandledException 后调用
///      <see cref="HandleDispatcherException"/>——记录并短暂吞掉，弹窗提示用户看日志；
///      短时间连续大量异常（>10 次/分钟）视为不可恢复的循环崩溃，放行退出。
///   2) 后台线程 / 主线程致命异常：AppDomain.UnhandledException——只记录，进程随后退出。
///   3) 未观察的任务异常：TaskScheduler.UnobservedTaskException——记录并标记已观察，
///      避免 GC 终结时把进程带崩。
///
/// 本类不依赖 UI 框架；弹窗由各 App 自己做（Core 保持无 UI 引用）。
/// </summary>
public static class CrashReporter
{
    private static int _installed;
    private static long _faultWindowStartQpc;
    private static int _faultsInWindow;

    private static readonly long QpcFrequency = System.Diagnostics.Stopwatch.Frequency;

    /// <summary>在进程入口调用一次（幂等）</summary>
    public static void Install(string appName)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        // 后台线程未捕获异常：记录后进程必然退出，这里只负责留下线索
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Logger.Error(appName, e.IsTerminating ? "致命异常，进程即将退出" : "未处理异常", ex ?? new($"非 CLR 异常: {e.ExceptionObject}"));
        };

        // 被丢弃任务的异常（GC 终结线程触发）：记录并吞掉，不让进程陪葬
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error(appName, "未观察的任务异常（已吞掉）", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// UI 线程未处理异常：记录并请求继续运行。
    /// 返回 true 表示调用方应把异常标记为 Handled（进程继续）；false 表示异常过于频繁，应放行退出。
    /// </summary>
    public static bool HandleDispatcherException(Exception ex, string appName)
    {
        Logger.Error(appName, "UI 线程未处理异常", ex);

        // 滑动窗口防循环：1 分钟内超过 10 次说明主流程已经坏了，继续吞只会让用户更困惑
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (typeof(CrashReporter))
        {
            if (now - _faultWindowStartQpc > QpcFrequency)
            {
                _faultWindowStartQpc = now;
                _faultsInWindow = 0;
            }
            return ++_faultsInWindow <= 10;
        }
    }
}
