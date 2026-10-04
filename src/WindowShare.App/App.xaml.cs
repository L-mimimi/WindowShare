using System.Windows;
using WindowShare.Core.Decoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Utils;

namespace WindowShare.App;

public partial class App : Application
{
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        CrashReporter.Install("App");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = CrashReporter.HandleDispatcherException(args.Exception, "App");
            if (args.Handled)
            {
                MessageBox.Show(
                    $"程序遇到内部错误，已尝试继续运行。\n详情见日志：{AppPaths.Logs}",
                    "窗享", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        AppPaths.EnsureDirectories();
        Logger.Initialize(LogLevel.Info);

        // HEVC 解码能力探针模式（由主进程以子进程方式拉起；结果走退出码）
        if (e.Args.Contains(HevcDecodeProbe.ArgProbe))
        {
            Environment.Exit(HevcDecodeProbe.TryProbe()
                ? HevcDecodeProbe.ExitOk
                : HevcDecodeProbe.ExitUnsupported);
        }

        Logger.Info("App", "窗享 合并入口已启动");
        Logger.Info("App", $"{AppPaths.ModeDescription}：{AppPaths.Root}");

        _settings = AppSettings.Load();
        var args = e.Args;
        var mode = args.Contains("--host") ? "host"
            : args.Contains("--viewer") ? "viewer"
            : args.Contains("--select") ? null
            : _settings.Remember ? _settings.LastMode
            : null;

        if (mode is "host" or "viewer")
        {
            OpenMode(mode);
        }
        else
        {
            new ModeSelectWindow(_settings).Show();
        }
    }

    /// <summary>打开指定模式的主窗口（由模式选择窗或启动参数调用）</summary>
    public void OpenMode(string mode)
    {
        if (mode == "host")
        {
            Logger.Info("App", "进入共享端模式");
            new WindowShare.Host.MainWindow().Show();
        }
        else
        {
            Logger.Info("App", "进入观看端模式");
            new WindowShare.Viewer.MainWindow().Show();
        }
    }
}
