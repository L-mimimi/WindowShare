using System.IO;
using WindowShare.Core.Capture;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Session;
using WindowShare.Core.Utils;

namespace WindowShare.Host;

/// <summary>
/// 无 UI 自动验证模式（--autotest）：
///   自动共享主显示器 4 秒，验证 ShareSession 编排（凭据生成/预览/编码分发/一键停止/录制文件）。
///   退出码 0 = 通过。不会注入任何输入，不会打断用户操作。
/// </summary>
public static class AutoTest
{
    public static int Run()
    {
        Logger.Initialize(LogLevel.Debug);
        Logger.Info("AutoTest", "===== Host 自动验证开始 =====");
        AppPaths.EnsureDirectories();

        try
        {
            var primary = CaptureSourceList.GetMonitors().FirstOrDefault(m => m.IsPrimary);
            if (primary == null)
            {
                Logger.Error("AutoTest", "找不到主显示器");
                return 1;
            }

            using var session = new ShareSession();
            var encodedFrames = 0;
            var encodedBytes = 0;
            var previewFrames = 0;
            var stopped = false;

            session.AddSink(new CountingSink(f =>
            {
                Interlocked.Increment(ref encodedFrames);
                Interlocked.Add(ref encodedBytes, f.PayloadSize);
            }));
            session.PreviewArrived += _ => Interlocked.Increment(ref previewFrames);
            session.Stopped += _ => stopped = true;

            var recordFile = Path.Combine(AppPaths.Recordings, $"autotest-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
            session.Start(primary, new ShareOptions
            {
                Width = 1280,
                Fps = 30,
                BitrateBps = 2_500_000,
                RecordForValidation = true,
                RecordFilePath = recordFile,
            });

            Logger.Info("AutoTest", $"房间号={session.RoomCode}, 密码={session.Password}");
            Logger.Info("AutoTest", $"编码器={session.EncoderName}, 硬件={session.IsHardwareEncoder}, 零拷贝={session.IsZeroCopy}");

            Thread.Sleep(4000);

            var stopReason = "autotest";
            session.Stop(stopReason);
            Thread.Sleep(300);

            var roomOk = session.RoomCode.Length == 6 && session.Password.Length == 8;
            var fileOk = File.Exists(recordFile) && new FileInfo(recordFile).Length > 10_000;
            Logger.Info("AutoTest", $"凭据={roomOk}, 预览帧={previewFrames}, 编码帧={encodedFrames}, " +
                                    $"字节={encodedBytes}, 录制文件={fileOk}, 停止回调={stopped}");

            var pass = roomOk && previewFrames > 0 && encodedFrames > 30 && encodedBytes > 10_000 &&
                       fileOk && stopped;
            Logger.Info("AutoTest", $"===== AutoTest {(pass ? "PASS" : "FAIL")} =====");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Logger.Error("AutoTest", "自动验证异常", ex);
            return 2;
        }
    }

    /// <summary>统计用 sink</summary>
    private sealed class CountingSink(Action<EncodedVideoFrame> onFrame) : ShareSession.IFrameSink
    {
        public string Name => "autotest-counter";
        public void OnEncodedFrame(EncodedVideoFrame frame) => onFrame(frame);
        public void OnShareStopped(string reason) { }
    }
}
