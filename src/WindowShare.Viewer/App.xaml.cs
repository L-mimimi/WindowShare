using System.Windows;
using WindowShare.Core.Logging;
using WindowShare.Core.Utils;

namespace WindowShare.Viewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureDirectories();
        Logger.Initialize(LogLevel.Info);
        Logger.Info("Viewer", "窗享 Viewer 已启动（只读观看端）");
        Logger.Info("Viewer", $"{AppPaths.ModeDescription}：{AppPaths.Root}");
        new MainWindow().Show();
    }
}
