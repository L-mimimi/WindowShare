using System.Windows;
using WindowShare.Core.Logging;
using WindowShare.Core.Utils;

namespace WindowShare.Host;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底最先装：之后的任何崩溃都先留日志
        CrashReporter.Install("Host");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = CrashReporter.HandleDispatcherException(args.Exception, "Host");
            if (args.Handled)
            {
                MessageBox.Show(
                    $"程序遇到内部错误，已尝试继续运行。\n详情见日志：{AppPaths.Logs}",
                    "窗享 Host", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        AppPaths.EnsureDirectories();
        Logger.Initialize(LogLevel.Info);
        Logger.Info("Host", "窗享 Host 已启动（只读屏幕/窗口共享，无远程控制功能）");
        Logger.Info("Host", $"{AppPaths.ModeDescription}：{AppPaths.Root}");

        // --autotest：无 UI 自动验证模式（CI/冒烟）
        if (e.Args.Contains("--autotest"))
        {
            Shutdown(AutoTest.Run());
            return;
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("Host", "Host 已退出");
        base.OnExit(e);
    }
}
