using System.Windows;
using System.Windows.Threading;

namespace WindowShare.Host;

/// <summary>设备审批结果</summary>
public enum ApprovalDecision
{
    ApprovedOnce,
    ApprovedAndRemembered,
    Denied,
}

/// <summary>
/// 设备审批弹窗：首次连接的观看者需要 Host 用户明确批准（防未授权连接）。
/// 限时 60 秒无操作自动拒绝：审批等待发生在认证线程上（同步等待），弹窗无超时会让
/// 认证线程与观看端的自动重连无限期悬挂（v1.5.0 Q7）。
/// </summary>
public partial class ApprovalDialog : Window
{
    /// <summary>无操作自动拒绝时限（秒）</summary>
    private const int TimeoutSeconds = 60;

    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Denied;

    private readonly DispatcherTimer _timer;
    private int _remaining = TimeoutSeconds;

    public ApprovalDialog(string deviceName, string deviceId, string remoteAddress)
    {
        InitializeComponent();
        TxtDevice.Text = $"{deviceName}（{remoteAddress}）";
        TxtDeviceId.Text = $"设备 ID：{deviceId}";
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        TxtCountdown.Text = $"{TimeoutSeconds} 秒内未处理将自动拒绝。";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                DialogResult = true; // Decision 保持 Denied
                return;
            }
            TxtCountdown.Text = $"{_remaining} 秒内未处理将自动拒绝。";
        };
        _timer.Start();
    }

    private void BtnRemember_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Decision = ApprovalDecision.ApprovedAndRemembered;
        DialogResult = true;
    }

    private void BtnOnce_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Decision = ApprovalDecision.ApprovedOnce;
        DialogResult = true;
    }

    private void BtnDeny_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Decision = ApprovalDecision.Denied;
        DialogResult = true;
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
