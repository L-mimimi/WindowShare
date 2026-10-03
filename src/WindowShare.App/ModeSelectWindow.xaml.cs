using System.Windows;
using WindowShare.Core.Utils;

namespace WindowShare.App;

/// <summary>模式选择窗：无启动参数且未记住选择时出现</summary>
public partial class ModeSelectWindow : Window
{
    private readonly AppSettings _settings;

    public ModeSelectWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ChkRemember.IsChecked = _settings.Remember;
    }

    private void BtnHost_Click(object sender, RoutedEventArgs e) => Enter("host");

    private void BtnViewer_Click(object sender, RoutedEventArgs e) => Enter("viewer");

    private void Enter(string mode)
    {
        _settings.Remember = ChkRemember.IsChecked == true;
        _settings.LastMode = _settings.Remember ? mode : "";
        _settings.Save();
        ((App)Application.Current).OpenMode(mode);
        Close();
    }
}
