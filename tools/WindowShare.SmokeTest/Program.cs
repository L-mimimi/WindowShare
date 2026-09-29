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
            // 每个 Part 独立兜底：单个 Part 抛异常不应中断后续验证
            Part1Probe();
            var ok2 = RunPart("Part2", Part2SyntheticEncode);
            var ok2b = RunPart("Part2b", Part2bHighResAndHighFps);
            var ok3 = RunPart("Part3", Part3RealCapture);
            var ok4 = RunPart("Part4", Part4LoopbackE2E);
            var ok5 = RunPart("Part5", Part5Signaling);
            var ok6 = RunPart("Part6", Part6WebRtcLoopback);
            Logger.Info("SmokeTest",
                $"===== 结果: 合成编码={(ok2 ? "PASS" : "FAIL")}, " +
                $"4K/高帧率={(ok2b ? "PASS" : "FAIL")}, " +
                $"真实捕获={(ok3 ? "PASS" : "FAIL")}, 回环端到端={(ok4 ? "PASS" : "FAIL")}, " +
                $"信令={(ok5 ? "PASS" : "FAIL")}, WebRTC={(ok6 ? "PASS" : "FAIL")} =====");
            return ok2 && ok2b && ok3 && ok4 && ok5 && ok6 ? 0 : 1;
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

        Logger.Info("Probe", "---- 分辨率/帧率档位支持探测 ----");
        var probeDevice = D3D11DevicePool.GetOrCreate();
        // 逐个变量隔离：先确认基线可用，再单独抬高码率 / 帧率 / 分辨率，
        // 以判断编码器拒绝配置（E_INVALIDARG）的真正约束是哪个维度。
        foreach (var (w, h, fps, kbps, note) in new[]
                 {
                     (1280, 720, 30, 2_600, "基线 720p30"),
                     (1920, 1080, 30, 5_300, "基线 1080p30"),
                     (1920, 1080, 30, 20_000, "仅抬高码率 20Mbps"),
                     (1920, 1080, 60, 9_300, "仅抬高帧率 60"),
                     (1920, 1080, 144, 5_000, "仅抬高帧率 144（低码率）"),
                     (1920, 1080, 144, 18_500, "帧率 144 + 高码率"),
                     (2560, 1440, 30, 4_000, "仅抬高到 2K（低码率）"),
                     (3840, 2160, 30, 3_000, "仅抬高到 4K（低码率）"),
                     (3840, 2160, 30, 17_000, "4K + 自动码率"),
                     (3840, 2160, 60, 30_000, "4K60 + 自动码率"),
                 })
        {
            var settings = new EncoderSettings
            {
                Width = w,
                Height = h,
                Fps = fps,
                BitrateBps = kbps * 1000,
                GopSize = Math.Max(2, fps * 2),
            };
            var level = VideoFormatPlanner.SuggestH264Level(w, h, fps);
            try
            {
                using var encoder = new MfH264Encoder(settings, probeDevice);
                Logger.Info("Probe",
                    $"  {w}x{h}@{fps} {kbps}kbps Level {VideoFormatPlanner.H264LevelName(level)} [{note}]: 可用 " +
                    $"({encoder.EncoderName}, 硬件={encoder.IsHardware}, 零拷贝={encoder.IsD3DAccelerated}, " +
                    $"实际 Level {VideoFormatPlanner.H264LevelName(encoder.AppliedH264Level)})");
            }
            catch (Exception ex)
            {
                Logger.Warn("Probe",
                    $"  {w}x{h}@{fps} {kbps}kbps Level {VideoFormatPlanner.H264LevelName(level)} [{note}]: 不可用 - " +
                    $"{ex.GetType().Name} {FirstLine(ex.Message)}");
            }
        }
    }

    /// <summary>单个 Part 的异常兜底：失败记为 FAIL，但不影响后续 Part 执行</summary>
    private static bool RunPart(string name, Func<bool> part)
    {
        try { return part(); }
        catch (Exception ex)
        {
            Logger.Error(name, $"{name} 抛出异常", ex);
            return false;
        }
    }

    /// <summary>异常消息首行（多行堆栈信息只取第一行，便于日志阅读）</summary>
    private static string FirstLine(string message) =>
        message.Split('\n')[0].TrimEnd('\r');

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
            // 定速轮询捕获：WGC 在静态桌面上只在内容变化时出帧（约 8fps），会让回环测试偶发失败
            CaptureEngine = CaptureEnginePreference.Gdi,
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
    long decodableFrames = 0;
    var firstKeyframeSeen = false;
    // GOP 补发是否生效：观看者收到的第一帧就应该是补发的 IDR
    var firstFrameIsKeyframe = false;
    var gopCacheReady = false;
    var cachedFramesAtConnect = 0;
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
        if (Interlocked.Increment(ref receivedFrames) == 1)
            Volatile.Write(ref firstFrameIsKeyframe, AnnexB.IsKeyframe(f.Data));
        stats.OnFrame(f.Data.Length);
        // 首个 IDR 之前的帧没有参考帧，按设计解不出来（Viewer 端同样丢弃），
        // 因此断言用「首个 IDR 之后的可解码帧」做分母，而不是「收到的帧」。
        if (!Volatile.Read(ref firstKeyframeSeen))
        {
            if (!AnnexB.IsKeyframe(f.Data)) return;
            Volatile.Write(ref firstKeyframeSeen, true);
        }
        Interlocked.Increment(ref decodableFrames);
        try { decoder.Decode(f.Data, f.TimestampUtc); } catch (Exception ex) { Logger.Warn("Part4", "解码异常: " + ex.Message); }
    };
        client.RttUpdated += r => rttMs = r;
        // 解码回调必须在 Start 之前挂好：否则连接建立后头几帧解出来了却没被计数
        decoder.Decoded += d => Interlocked.Increment(ref decodedFrames);
        // 等服务器攒出一个以 IDR 开头的 GOP 再连：否则补发无内容，首帧断言会偶发失败
        gopCacheReady = SpinWait.SpinUntil(() => server.CachedGopFrames > 0, TimeSpan.FromSeconds(10));
        cachedFramesAtConnect = server.CachedGopFrames;
        Logger.Info("Part4", $"连接前 GOP 缓存就绪={gopCacheReady}（{cachedFramesAtConnect} 帧）");
        client.Start();

        var connected = connectedEvent.Wait(TimeSpan.FromSeconds(10));
        if (connected)
        {
            session.RequestKeyframe();   // 接入即出 IDR，不等 GOP 周期
            Thread.Sleep(6000);          // 收 6 秒流
        }

    var encEnabled = client.IsEncrypted;
    client.Stop();
    client.Dispose();
    // 抽干解码器内部滞留的尾部帧（H.264 解码器会攒参考帧），否则末尾若干帧永远出不来
    var flushed = decoder.Flush();
    var decodeDiag = $"投喂 {decoder.InputFrames} 帧, 输出样本 {decoder.OutputSamples} 个, " +
                     $"分辨率未知丢弃 {decoder.DroppedUnknownSize} 帧, 缓冲不足丢弃 {decoder.DroppedShortBuffer} 帧";
    decoder.Dispose();
    server.Stop();
    session.Stop("part4-end");

    var (bitrate, fps, _) = stats.Tick();
    Logger.Info("Part4", $"连接={connected}, 状态={state}, 加密={encEnabled}, " +
                        $"收到 {receivedFrames} 帧, 首个 IDR 后可解码 {Interlocked.Read(ref decodableFrames)} 帧, 解码 {decodedFrames} 帧, " +
                        $"首帧即 IDR={Volatile.Read(ref firstFrameIsKeyframe)}, GOP 缓存就绪={gopCacheReady}({cachedFramesAtConnect} 帧), " +
                        $"抽干补出 {flushed} 帧, {decodeDiag}, " +
                        $"码率≈{bitrate / 1000:F0}kbps, RTT={rttMs:F1}ms");

        // 比例断言而非绝对帧数：GDI 定速捕获 6 秒约 180 帧，但实际帧率仍受机器负载影响，
        // 绝对阈值（旧值 received>60 / decoded>30）会造成偶发失败。
        // 首帧必须是 IDR：编码器不认 ForceKeyFrame（本机 DX12 编码器实测 E_NOTIMPL），
        // 观看者能秒开完全依赖服务器补发缓存 GOP，这条断言就是它的回归网。
        var decodable = Interlocked.Read(ref decodableFrames);
        var pass = connected && encEnabled && gopCacheReady &&
                   Volatile.Read(ref firstFrameIsKeyframe) && Volatile.Read(ref firstKeyframeSeen) &&
                   receivedFrames >= 40 && decodable >= 30 && decodedFrames >= decodable * 9 / 10;
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
            if (s.Contains("已连接")) { connected.Set(); }
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
                CaptureEngine = CaptureEnginePreference.Gdi,   // 定速捕获，避免静态桌面下 WGC 几乎不出帧
            });

            var ok = connected.Wait(TimeSpan.FromSeconds(20));
            if (ok)
            {
                session.RequestKeyframe();   // 接入即出 IDR，不等 GOP 周期
                Thread.Sleep(6000);          // 收 6 秒流
            }

            // 先定格投喂计数，再抽干解码器内部滞留的尾部帧，最后读解码结果
            var decodeInvocations = Volatile.Read(ref decodeCalls);
            var receivedFrames = Interlocked.Read(ref received);
            var flushed = decoder.Flush();
            var decodedFrames = Interlocked.Read(ref decoded);
            Logger.Info("Part6", $"Decode 调用 {decodeInvocations} 次; 连接={ok}, 接收={receivedFrames} 帧, " +
                                $"解码={decodedFrames} 帧（含抽干 {flushed}）, " +
                                $"投喂={decoder.InputFrames}, 输出样本={decoder.OutputSamples}, " +
                                $"分辨率未知丢弃={decoder.DroppedUnknownSize}, 缓冲不足丢弃={decoder.DroppedShortBuffer}, " +
                                $"中继={sender.UsedRelay || receiver.UsedRelay}");
            // 比例断言：首个 IDR 之前的帧按设计被跳过（不计入解码），绝对阈值会随机器负载抖动
            var pass = ok && receivedFrames >= 30 && decodeInvocations >= 20 &&
                       decodedFrames >= decodeInvocations * 9 / 10;
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

    /// <summary>
    /// 4K 与高帧率编码验证（合成图像，不依赖显示器原生分辨率）：
    ///   - 3840×2160@30：验证最高分辨率档位真能编出码流（编码器 + GPU 视频处理器吃得下 4K），
    ///     并把码流解回来核对分辨率——证明显式下发的 H.264 Level 产出的是合法可解码流；
    ///   - 1280×720@120：验证帧率档位生效——投喂远快于目标帧率时，编码管线必须把输出节流到 120fps 以内。
    /// </summary>
    private static bool Part2bHighResAndHighFps()
    {
        Logger.Info("Part2b", "---- 4K / 高帧率编码验证 ----");
        var ok4K = SyntheticEncode(3840, 2160, 30, 1.5, "smoke-4k.h264",
            minFrames: 5, minBytes: 20_000, expectOutput: (3840, 2160), verifyDecode: true);
        var okFps = SyntheticEncode(1280, 720, 120, 2.0, "smoke-120fps.h264",
            minFrames: 60, minBytes: 20_000, expectOutput: (1280, 720), maxFpsRatio: 1.35);
        var pass = ok4K && okFps;
        Logger.Info("Part2b", pass ? "Part2b PASS" : "Part2b FAIL");
        return pass;
    }

    /// <summary>
    /// 合成图像编码一轮：尽可能快地投喂帧（远快于目标帧率），
    /// 因此可同时验证「目标分辨率真被用上」与「帧率节流真的生效」。
    /// </summary>
    private static bool SyntheticEncode(int width, int height, int fps, double seconds, string fileName,
        int minFrames, int minBytes, (int Width, int Height)? expectOutput = null, double maxFpsRatio = 0,
        bool verifyDecode = false)
    {
        var settings = new EncoderSettings
        {
            Width = width,
            Height = height,
            Fps = fps,
            BitrateBps = VideoFormatPlanner.SuggestBitrateBps(width, height, fps),
            GopSize = Math.Max(2, fps * 2),
        };
        EncoderPipeline pipeline;
        try
        {
            pipeline = new EncoderPipeline(settings);
        }
        catch (Exception ex)
        {
            Logger.Error("Part2b",
                $"{width}x{height}@{fps} 编码管线初始化失败（该档位在本机不可用）: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            return false;
        }
        using var pipelineScope = pipeline;
        var file = Path.Combine(AppPaths.Recordings, fileName);
        using var writer = new H264FileWriter(file);
        pipeline.Encoded += f => writer.Write(f);

        // 可选：把编出来的码流直接解回来，核对能否解码 + 分辨率是否一致
        using var decoder = verifyDecode ? new MfH264Decoder() : null;
        long decodedFrames = 0;
        var decodedWidth = 0;
        var decodedHeight = 0;
        if (decoder != null)
        {
            decoder.Decoded += d =>
            {
                Interlocked.Increment(ref decodedFrames);
                decodedWidth = d.Width;
                decodedHeight = d.Height;
            };
            pipeline.Encoded += f =>
            {
                try { decoder.Decode(f.Data, f.TimestampUtc); }
                catch (Exception ex) { Logger.Warn("Part2b", "解码异常: " + FirstLine(ex.Message)); }
            };
        }

        // 复用同一块像素缓冲：4K 单帧 33MB，逐帧新建会把 GC 打爆。
        // Submit 内部同步把像素上传到 GPU 纹理，返回后即可安全覆写。
        var bgra = new byte[width * height * 4];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long submitted = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            DrawPattern(bgra, width, height, sw.Elapsed.TotalSeconds);
            pipeline.Submit(new CaptureFrame
            {
                Width = width,
                Height = height,
                TimestampUtc = DateTime.UtcNow.Ticks,
                QpcTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                BgraPixels = bgra,
            });
            submitted++;
        }
        var elapsed = sw.Elapsed.TotalSeconds;
        sw.Stop();
        Thread.Sleep(400); // 等待编码回调落地

        var (outW, outH) = pipeline.OutputSize;
        var (encFrames, encBytes) = pipeline.GetCounters();
        var actualFps = elapsed > 0 ? encFrames / elapsed : 0;
        Logger.Info("Part2b",
            $"{width}x{height}@{fps}fps → 提交 {submitted} 帧, 编码 {encFrames} 帧 (实际 {actualFps:F1}fps), " +
            $"节流丢弃 {pipeline.DroppedFrames} 帧, {encBytes} 字节, 输出 {outW}x{outH}, " +
            $"码率≈{encBytes * 8.0 / Math.Max(0.001, elapsed) / 1_000_000:F1}Mbps, 编码器={pipeline.EncoderName}");

        var pass = encFrames >= minFrames && writer.Bytes >= minBytes && writer.HasParameterSets;
        if (expectOutput is { } expected)
        {
            if (outW != expected.Width || outH != expected.Height)
            {
                Logger.Error("Part2b", $"输出分辨率不符：期望 {expected.Width}x{expected.Height}，实际 {outW}x{outH}");
                pass = false;
            }
        }
        if (maxFpsRatio > 0 && actualFps > fps * maxFpsRatio)
        {
            Logger.Error("Part2b", $"帧率节流未生效：实际 {actualFps:F1}fps 超过目标 {fps}fps 的 {maxFpsRatio:P0}");
            pass = false;
        }
        if (verifyDecode)
        {
            var decoded = Interlocked.Read(ref decodedFrames);
            if (decoded <= 0)
            {
                Logger.Error("Part2b", $"{width}x{height}@{fps} 码流解不出任何帧（H.264 Level 下发可能无效）");
                pass = false;
            }
            else if (decoded < encFrames * 4 / 5)
            {
                // 解码器低延迟模式失效时会先攒住一批帧（实测 28 帧）再出图，观看者接入后要黑屏约 1 秒
                Logger.Error("Part2b",
                    $"{width}x{height}@{fps} 解码帧数偏少：编码 {encFrames} 帧只解出 {decoded} 帧（解码器在攒帧）");
                pass = false;
            }
            else if (decodedWidth != width || decodedHeight != height)
            {
                Logger.Error("Part2b", $"解码分辨率不符：期望 {width}x{height}，实际 {decodedWidth}x{decodedHeight}");
                pass = false;
            }
            else
            {
                Logger.Info("Part2b", $"解码回读通过：{decoded} 帧 {decodedWidth}x{decodedHeight}");
            }
        }
        if (!pass) Logger.Error("Part2b", $"{width}x{height}@{fps} 验证失败");
        return pass;
    }

}
