using WindowShare.Core.Capture;
using WindowShare.Core.Decoding;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Network;
using WindowShare.Core.Security;
using WindowShare.Core.Signaling;
using WindowShare.Core.Session;
using WindowShare.Core.Stats;
using WindowShare.Core.Utils;
using WindowShare.Core.WebRtc;

namespace WindowShare.SmokeTest;

/// <summary>
/// 冒烟测试（控制台）：
///   Part1 探测：捕获引擎/捕获源/编码器清单
///   Part2 编码验证：合成运动图像 → BGRA→NV12(GPU) → H.264 → 写入文件
///   Part3 捕获验证：真实捕获主显示器并编码（WGC + GDI 双引擎）
///   Part4 回环端到端：ShareSession + LAN 服务器 → LanShareClient + 解码器 → 解码出画面
/// 输出文件位于 %APPDATA%\WindowShare\recordings\，可被 ffprobe/播放器验证。
/// 退出码 0 = 全部通过。
/// </summary>
public static class Program
{
    public static int Main()
    {
        Logger.Initialize(LogLevel.Debug);
        Logger.Info("SmokeTest", "===== WindowShare 冒烟测试开始 =====");
        AppPaths.EnsureDirectories();

        try
        {
            Part1Probe();
            var ok2 = Part2SyntheticEncode();
            var ok3 = Part3RealCapture();
            var ok4 = Part4LoopbackE2E();
            var ok5 = Part5Signaling();
            var ok6 = Part6WebRtcLoopback();
            Logger.Info("SmokeTest",
                $"===== 结果: 合成编码={(ok2 ? "PASS" : "FAIL")}, " +
                $"真实捕获={(ok3 ? "PASS" : "FAIL")}, 回环端到端={(ok4 ? "PASS" : "FAIL")}, " +
                $"信令={(ok5 ? "PASS" : "FAIL")}, WebRTC={(ok6 ? "PASS" : "FAIL")} =====");
            return ok2 && ok3 && ok4 && ok5 && ok6 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Logger.Error("SmokeTest", "冒烟测试异常", ex);
            return 2;
        }
    }

    private static void Part1Probe()
    {
        Logger.Info("Probe", "---- 捕获引擎可用性 ----");
        foreach (var (name, ok) in CaptureEngineFactory.Probe())
            Logger.Info("Probe", $"{name}: {(ok ? "可用" : "不可用")}");

        var monitors = CaptureSourceList.GetMonitors();
        var windows = CaptureSourceList.GetWindows();
        Logger.Info("Probe", $"显示器 {monitors.Count} 个, 可共享窗口 {windows.Count} 个");
        foreach (var m in monitors.Take(3)) Logger.Info("Probe", $"  {m}");
        foreach (var w in windows.Take(5)) Logger.Info("Probe", $"  {w}");

        Logger.Info("Probe", "---- 编码器清单 ----");
        foreach (var name in MfH264Encoder.ProbeEncoders())
            Logger.Info("Probe", $"  {name}");
    }

    /// <summary>合成运动图像编码 3 秒 @30fps，验证编码器与文件输出</summary>
    private static bool Part2SyntheticEncode()
    {
        Logger.Info("Part2", "---- 合成图像编码验证（1280x720@30, 3 秒）----");
        const int width = 1280, height = 720, fps = 30, seconds = 3;
        var settings = new EncoderSettings
        {
            Width = width,
            Height = height,
            Fps = fps,
            BitrateBps = 3_000_000,
            GopSize = 60,
        };

        using var pipeline = new EncoderPipeline(settings);
        var file = Path.Combine(AppPaths.Recordings, "smoke-synthetic.h264");
        using var writer = new H264FileWriter(file);
        var frames = 0;
        pipeline.Encoded += f =>
        {
            writer.Write(f);
            Interlocked.Increment(ref frames);
        };

        // 合成 BGRA 运动图像（移动渐变块 + 时间戳数字块），直接走 GPU 上传路径
        var bgra = new byte[width * height * 4];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / fps);
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var t = sw.Elapsed.TotalSeconds;
            DrawPattern(bgra, width, height, t);
            var frame = new CaptureFrame
            {
                Width = width,
                Height = height,
                TimestampUtc = DateTime.UtcNow.Ticks,
                QpcTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                BgraPixels = (byte[])bgra.Clone(),
            };
            pipeline.Submit(frame);
            var remain = frameInterval - TimeSpan.FromTicks(System.Diagnostics.Stopwatch.GetTimestamp() - 0);
            Thread.Sleep(Math.Max(1, (int)frameInterval.TotalMilliseconds));
        }
        Thread.Sleep(300); // 等待编码回调落地

        var (encFrames, encBytes) = pipeline.GetCounters();
        var bitrate = encBytes * 8.0 / seconds;
        Logger.Info("Part2", $"编码器={pipeline.EncoderName}, 硬件={pipeline.IsHardwareEncoder}, 零拷贝={pipeline.IsZeroCopy}");
        Logger.Info("Part2", $"编码 {encFrames} 帧, {encBytes} 字节, 平均码率 {bitrate / 1_000_000:F2} Mbps");
        Logger.Info("Part2", $"文件: {file} ({writer.Bytes} 字节, 含参数集={writer.HasParameterSets})");

        // 验收：帧数足够（编码器启动初期有热身+合成图像大面积静态区域会被节流）、有码流、含 SPS/PPS
        var pass = encFrames >= 45 && writer.Bytes > 30_000 && writer.HasParameterSets;
        Logger.Info("Part2", pass ? "Part2 PASS" : "Part2 FAIL");
        return pass;
    }

    /// <summary>真实捕获验证：分别用 WGC 与 GDI 各捕获 3 秒（任一引擎出帧即通过；
    /// 注意桌面完全静止时 WGC 不推送帧——屏幕无损时不产生新帧属正常行为，因此用 GDI 兜底验证管线）</summary>
    private static bool Part3RealCapture()
    {
        Logger.Info("Part3", "---- 真实捕获验证（主显示器，WGC + GDI 各 3 秒）----");
        var primary = CaptureSourceList.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (primary == null)
        {
            Logger.Error("Part3", "找不到主显示器");
            return false;
        }

        var settings = new EncoderSettings
        {
            Width = Math.Min(1280, primary.Bounds.Width & ~1),
            Height = Math.Min(720, primary.Bounds.Height & ~1),
            Fps = 30,
            BitrateBps = 3_000_000,
            GopSize = 60,
        };

        var ok = false;
        foreach (var make in new Func<(ICaptureEngine Engine, string Note)>[] {
                     () => CaptureEngineFactory.Create(primary),
                     () => (new GdiCaptureEngine(), "GDI 兜底引擎") })
        {
            var (engine, note) = make();
            if (!string.IsNullOrEmpty(note)) Logger.Info("Part3", note);
            try
            {
                using var pipeline = new EncoderPipeline(settings);
                var file = Path.Combine(AppPaths.Recordings, "smoke-capture.h264");
                using var writer = new H264FileWriter(file);
                var captured = 0;
                engine.FrameArrived += frame =>
                {
                    Interlocked.Increment(ref captured);
                    pipeline.Submit(frame);
                };
                engine.Start(primary);
                Thread.Sleep(3000);
                engine.Stop();
                Thread.Sleep(200);

                var (encFrames, encBytes) = pipeline.GetCounters();
                Logger.Info("Part3", $"引擎={engine.Name}: 捕获 {captured} 帧, 编码 {encFrames} 帧, {encBytes} 字节");
                if (captured >= 1 && encBytes > 1000)
                {
                    ok = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Part3", $"引擎 {engine.Name} 验证失败", ex);
            }
            finally
            {
                engine.Dispose();
            }
        }

        Logger.Info("Part3", ok ? "Part3 PASS" : "Part3 FAIL");
        return ok;
    }

    /// <summary>画一个随时间移动的测试图案</summary>
    private static void DrawPattern(byte[] bgra, int width, int height, double t)
    {
        // 背景：垂直渐变
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            var v = (byte)(y * 255 / height);
            for (var x = 0; x < width; x++)
            {
                bgra[row + x * 4 + 0] = (byte)(255 - v); // B
                bgra[row + x * 4 + 1] = (byte)(v / 2);   // G
                bgra[row + x * 4 + 2] = (byte)v;         // R
                bgra[row + x * 4 + 3] = 0xFF;
            }
        }
        // 运动方块（横向往返）
        var bx = (int)((Math.Sin(t * 2.0) * 0.5 + 0.5) * (width - 220));
        var by = (int)((Math.Cos(t * 1.3) * 0.5 + 0.5) * (height - 220));
        for (var y = Math.Max(0, by); y < Math.Min(height, by + 200); y++)
        {
            for (var x = Math.Max(0, bx); x < Math.Min(width, bx + 200); x++)
            {
                var i = (y * width + x) * 4;
                bgra[i + 0] = 0x20;
                bgra[i + 1] = 0x80;
                bgra[i + 2] = 0xF0;
                bgra[i + 3] = 0xFF;
            }
        }
    }

    /// <summary>
    /// 回环端到端测试：ShareSession（主显示器）+ LanShareServer → LanShareClient（回环）
    /// → MfH264Decoder 解码出画面。验证认证/加密/传输/解码/统计全链路。
    /// </summary>
    private static bool Part4LoopbackE2E()
    {
    Logger.Info("Part4", "---- 回环端到端验证（LAN TCP + 认证 + 加密 + 解码，6 秒）----");
    var primary = CaptureSourceList.GetMonitors().FirstOrDefault(m => m.IsPrimary);
    if (primary == null) { Logger.Error("Part4", "找不到主显示器"); return false; }

    const int testPort = 48759;
    var session = new ShareSession();
    var whitelist = new DeviceWhitelist();
    whitelist.Approve("test-viewer-device", "AutoTest Viewer"); // 预批准，避免弹窗

    using var server = new LanShareServer(session, whitelist, testPort);
    server.ApproveRequired = _ => Task.FromResult(true);
    try
    {
        session.Start(primary, new ShareOptions
        {
            Width = 1280, Fps = 30, BitrateBps = 2_500_000,
            RecordForValidation = false,
        });
        server.Start();
    }
    catch (Exception ex)
    {
        Logger.Error("Part4", "服务器启动失败", ex);
        session.Dispose();
        return false;
    }

    long receivedFrames = 0;
    long decodedFrames = 0;
    var rttMs = double.NaN;
    var state = (ConnectionState)(-1);
    var connectedEvent = new ManualResetEventSlim(false);
    var decoder = new MfH264Decoder();
    var stats = new StatsCollector();

    var client = new LanShareClient("127.0.0.1", testPort,
        "test-viewer-device", "AutoTest Viewer", session.Password);
    client.StateChanged += (s, _) =>
    {
        state = s;
        if (s == ConnectionState.Connected) connectedEvent.Set();
    };
    client.FrameReceived += f =>
    {
        Interlocked.Increment(ref receivedFrames);
        stats.OnFrame(f.Data.Length);
        try { decoder.Decode(f.Data, f.TimestampUtc); } catch (Exception ex) { Logger.Warn("Part4", "解码异常: " + ex.Message); }
    };
    client.RttUpdated += r => rttMs = r;
    client.Start();

    // 解码回调（最后挂接，避免与 FrameReceived 竞争计数）
    decoder.Decoded += d => Interlocked.Increment(ref decodedFrames);

    var connected = connectedEvent.Wait(TimeSpan.FromSeconds(10));
    if (connected) Thread.Sleep(6000); // 收 6 秒流

    var encEnabled = client.IsEncrypted;
    client.Stop();
    client.Dispose();
    decoder.Dispose();
    server.Stop();
    session.Stop("part4-end");

    var (bitrate, fps, _) = stats.Tick();
    Logger.Info("Part4", $"连接={connected}, 状态={state}, 加密={encEnabled}, " +
                        $"收到 {receivedFrames} 帧, 解码 {decodedFrames} 帧, " +
                        $"码率≈{bitrate / 1000:F0}kbps, RTT={rttMs:F1}ms");

    var pass = connected && receivedFrames > 60 &&
               decodedFrames > 30 && encEnabled;
    Logger.Info("Part4", pass ? "Part4 PASS" : "Part4 FAIL");
    return pass;
    }
    /// <summary>
    /// 信令回环测试：启动信令服务器子进程 → Host 注册房间 → Viewer 加入（密码验证+审批）
    /// → Viewer 收到 Host LAN 端点。含错误密码负向用例。
    /// </summary>
    private static bool Part5Signaling()
    {
        Logger.Info("Part5", "---- 信令服务器回环验证 ----");
        const int port = 48001;
        var baseUrl = $"http://127.0.0.1:{port}";

        // 启动信令服务器子进程（复用已构建产物）
        var dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "..", "src", "WindowShare.Signaling", "bin", "Debug", "net8.0", "WindowShare.Signaling.dll");
        dll = Path.GetFullPath(dll);
        if (!File.Exists(dll))
        {
            Logger.Error("Part5", $"信令服务器未构建: {dll}");
            return false;
        }
        // 定位能承载 ASP.NET Core 的 dotnet（apphost 场景 MainModule 不是 dotnet.exe）
        var dotnet = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
        if (!Path.GetFileName(dotnet).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            dotnet = "dotnet";
        var sdkDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
        if (File.Exists(sdkDotnet)) dotnet = sdkDotnet; // 本机 SDK 安装含 ASP.NET Core 运行时
        var serverLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var server = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = $"\"{dll}\" --urls http://127.0.0.1:{port}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        server!.OutputDataReceived += (_, e) => { if (e.Data != null) serverLogs.Enqueue(e.Data); };
        server!.ErrorDataReceived += (_, e) => { if (e.Data != null) serverLogs.Enqueue(e.Data); };
        server!.BeginOutputReadLine();
        server!.BeginErrorReadLine();
        try
        {
            // 等待服务就绪
            var ready = false;
            using (var http = new System.Net.Http.HttpClient())
            {
                for (var i = 0; i < 40; i++)
                {
                    try
                    {
                        var resp = http.GetAsync($"{baseUrl}/stats").GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode) { ready = true; break; }
                    }
                    catch { }
                    Thread.Sleep(250);
                }
            }
            if (!ready)
            {
                Logger.Error("Part5", "信令服务器未就绪，子进程输出：");
                foreach (var line in serverLogs) Logger.Error("Part5|child", line);
                return false;
            }
            Logger.Info("Part5", "信令服务器已就绪");

            // Host 注册
            var roomCode = PasswordGenerator.GenerateRoomCode();
            var password = PasswordGenerator.GeneratePassword();
            var hostClient = new HostSignalingClient(baseUrl);
            ViewerJoinRequest? joinRequest = null;
            hostClient.ViewerJoinRequested += r => joinRequest = r;
            var registered = hostClient.RegisterAsync(roomCode, password, "AutoTest Host",
                new List<string> { "192.0.2.10:48750" }).GetAwaiter().GetResult();
            if (!registered) { Logger.Error("Part5", "房间注册失败"); return false; }
            Logger.Info("Part5", $"房间 {roomCode} 已注册");

            // 负向用例：错误密码
            var badViewer = new ViewerSignalingClient(baseUrl);
            string? badError = null;
            try
            {
                badViewer.JoinAsync(roomCode, "WRONGPWD", "dev-bad", "BadViewer").GetAwaiter().GetResult();
            }
            catch (Exception ex) { badError = ex.Message; }
            Logger.Info("Part5", $"错误密码被拒: {badError != null}");

            // 正向用例：正确密码 + Host 审批
            var approvedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostTask = Task.Run(async () =>
            {
                // 等待审批请求（最长 20 秒）
                for (var i = 0; i < 200 && joinRequest is null; i++) await Task.Delay(100);
                if (joinRequest is not null)
                    await hostClient.ApproveViewerAsync(joinRequest.ViewerId, true);
                approvedTcs.TrySetResult();
            });

            HostInfoPayload? hostInfo = null;
            var viewer = new ViewerSignalingClient(baseUrl);
            viewer.RelayFromHost += (_, _) => { };
            var joinTask = viewer.JoinAsync(roomCode, password, "dev-ok", "GoodViewer");
            Task.WhenAny(joinTask, Task.Delay(30000)).GetAwaiter().GetResult();
            approvedTcs.Task.Wait(5000);
            try { hostInfo = joinTask.GetAwaiter().GetResult(); } catch { }

            var ok = badError != null && hostInfo != null && hostInfo.LanEndpoints.Count > 0;
            Logger.Info("Part5", $"审批通过={hostInfo != null}, 端点数={hostInfo?.LanEndpoints.Count ?? 0}, " +
                                $"Host 名={hostInfo?.HostDeviceName}");
            return ok;
        }
        finally
        {
            try { server?.Kill(); } catch { }
            server?.Dispose();
        }
    }

    /// <summary>
    /// WebRTC 回环测试：WebRtcHostSender + WebRtcViewerReceiver 在本机 ICE 直连
    /// （DTLS-SRTP + H.264 RTP 打包/重组），ShareSession 真实编码 → 发送 → 解码出画面。
    /// </summary>
    private static bool Part6WebRtcLoopback()
    {
        Logger.Info("Part6", "---- WebRTC 回环验证（DTLS-SRTP + H.264 RTP，8 秒）----");
        var primary = CaptureSourceList.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (primary == null) { Logger.Error("Part6", "找不到主显示器"); return false; }

        var session = new ShareSession();
        var sender = new WebRtcHostSender();
        var receiver = new WebRtcViewerReceiver();
        var decoder = new MfH264Decoder();

        long received = 0, decoded = 0;
        var connected = new ManualResetEventSlim(false);
        var receiverConnected = false;

        // ICE 候选互通
        sender.LocalIceCandidate += c => receiver.AddIceCandidate(c);
        receiver.LocalIceCandidate += c => sender.AddIceCandidate(c);
        var debugPrinted = 0;
        long decodeCalls = 0;
        var firstKeyframeSeen = false;
        receiver.FrameReceived += (data, ts) =>
        {
            Interlocked.Increment(ref received);
            var nalTypes = string.Join(",", AnnexB.SplitNals(data).Take(6).Select(n => n.Type));
            if (Volatile.Read(ref debugPrinted) < 8)
            {
                Interlocked.Increment(ref debugPrinted);
                Logger.Info("Part6", $"帧 {received} 长度={data.Length} NAL[{nalTypes}]");
            }
            try
            {
                // 标准模式：丢弃首个关键帧之前的帧，其后全量解码
                var keyframe = AnnexB.IsKeyframe(data);
                if (!Volatile.Read(ref firstKeyframeSeen))
                {
                    if (!keyframe) return;
                    Volatile.Write(ref firstKeyframeSeen, true);
                }
                Interlocked.Increment(ref decodeCalls);
                decoder.Decode(data, ts);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                Logger.Warn("Part6", "解码异常: " + ex.Message);
            }
            catch (ObjectDisposedException) { /* 关闭竞态，忽略 */ }
        };
        decoder.Decoded += _ => Interlocked.Increment(ref decoded);
        receiver.StateChanged += s =>
        {
            Logger.Info("Part6", $"接收端状态: {s}");
            if (s.Contains("已连接")) { receiverConnected = true; connected.Set(); }
        };

        try
        {
            // 信令交换（本机直连，不经服务器）
            var offer = sender.CreateOfferAsync().GetAwaiter().GetResult();
            var answer = receiver.AcceptOfferAsync(offer).GetAwaiter().GetResult();
            sender.SetAnswer(answer);

            // 启动共享（编码帧 → WebRTC 发送）
            var sink = new LocalSink(frame =>
            {
                if (sender.IsConnected)
                    sender.SendEncodedFrame(frame, 30);
            });
            session.AddSink(sink);
            session.Start(primary, new ShareOptions
            {
                Width = 1280, Fps = 30, BitrateBps = 2_500_000, RecordForValidation = false,
            });

            var ok = connected.Wait(TimeSpan.FromSeconds(20));
            if (ok) Thread.Sleep(6000); // 收 6 秒流

            Logger.Info("Part6", $"Decode 调用 {Volatile.Read(ref decodeCalls)} 次; 连接={ok}, 接收={Interlocked.Read(ref received)} 帧, " +
                                $"解码={Interlocked.Read(ref decoded)} 帧, 中继={sender.UsedRelay || receiver.UsedRelay}");
            var pass = ok && Interlocked.Read(ref received) > 30 && Interlocked.Read(ref decoded) > 20;
            Logger.Info("Part6", pass ? "Part6 PASS" : "Part6 FAIL");
            return pass;
        }
        catch (Exception ex)
        {
            Logger.Error("Part6", "WebRTC 回环异常", ex);
            return false;
        }
        finally
        {
            session.Stop("part6-end");
            session.Dispose();
            decoder.Dispose();
            receiver.DisposeAsync().AsTask().Wait();
            sender.DisposeAsync().AsTask().Wait();
        }
    }

    /// <summary>简单帧转发 sink</summary>
    private sealed class LocalSink(Action<EncodedVideoFrame> onFrame) : ShareSession.IFrameSink
    {
        public string Name => "webrtc-loopback";
        public void OnEncodedFrame(EncodedVideoFrame frame) => onFrame(frame);
        public void OnShareStopped(string reason) { }
    }

}
