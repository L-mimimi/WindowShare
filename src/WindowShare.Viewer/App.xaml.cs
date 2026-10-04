using System.Windows;
using WindowShare.Core.Decoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Utils;

namespace WindowShare.Viewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // HEVC 解码能力探针模式（由主进程以子进程方式拉起；结果走退出码）：
        // 部分平台的 HEVC 解码 MFT 会在初始化时原生崩溃，隔离在子进程里主进程零风险。
        if (e.Args.Contains(HevcDecodeProbe.ArgProbe))
        {
            Environment.Exit(HevcDecodeProbe.TryProbe()
                ? HevcDecodeProbe.ExitOk
                : HevcDecodeProbe.ExitUnsupported);
        }

        // 全局异常兜底最先装：之后的任何崩溃都先留日志
        CrashReporter.Install("Viewer");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = CrashReporter.HandleDispatcherException(args.Exception, "Viewer");
            if (args.Handled)
            {
                MessageBox.Show(
                    $"程序遇到内部错误，已尝试继续运行。\n详情见日志：{AppPaths.Logs}",
                    "窗享 Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        AppPaths.EnsureDirectories();
        Logger.Initialize(LogLevel.Info);
        Logger.Info("Viewer", "窗享 Viewer 已启动（只读观看端）");
        Logger.Info("Viewer", $"{AppPaths.ModeDescription}：{AppPaths.Root}");
        new MainWindow().Show();
    }
}
