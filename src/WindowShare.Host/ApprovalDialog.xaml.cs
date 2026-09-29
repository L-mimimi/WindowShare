using System.Windows;

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
/// </summary>
public partial class ApprovalDialog : Window
{
    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Denied;

    public ApprovalDialog(string deviceName, string deviceId, string remoteAddress)
    {
        InitializeComponent();
        TxtDevice.Text = $"{deviceName}（{remoteAddress}）";
        TxtDeviceId.Text = $"设备 ID：{deviceId}";
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
    }

    private void BtnRemember_Click(object sender, RoutedEventArgs e)
    {
        Decision = ApprovalDecision.ApprovedAndRemembered;
        DialogResult = true;
    }

    private void BtnOnce_Click(object sender, RoutedEventArgs e)
    {
        Decision = ApprovalDecision.ApprovedOnce;
        DialogResult = true;
    }

    private void BtnDeny_Click(object sender, RoutedEventArgs e)
    {
        Decision = ApprovalDecision.Denied;
        DialogResult = true;
    }
}
