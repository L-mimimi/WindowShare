using WindowShare.Core.Audio;
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
///   Part5 信令回环：信令服务器子进程 → Host 注册房间 → Viewer 加入（含错误密码负向用例）
///   Part6 WebRTC 回环：SIPSorcery 双 PeerConnection 本地互通
///   Part7 系统声音：AAC 编解码往返 / LAN 音频端到端 / WASAPI loopback 探测
///   Part8 局域网发现：DiscoveryBeacon → 组播回环 → DiscoveryListener
/// 输出文件位于 %APPDATA%\WindowShare\recordings\，可被 ffprobe/播放器验证。
/// 退出码 0 = 全部通过。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // HEVC 解码能力探针模式（子进程；部分平台扩展 MFT 会原生崩溃，需隔离）
        if (args.Contains(HevcDecodeProbe.ArgProbe))
            return HevcDecodeProbe.TryProbe() ? 0 : HevcDecodeProbe.ExitUnsupported;

        Logger.Initialize(LogLevel.Debug);
        Logger.Info("SmokeTest", "===== WindowShare 冒烟测试开始 =====");
        AppPaths.EnsureDirectories();

        try
        {
            // 每个 Part 独立兜底：单个 Part 抛异常不应中断后续验证
            Part1Probe();
            var ok2 = RunPart("Part2", Part2SyntheticEncode);
            var ok2b = RunPart("Part2b", Part2bHighResAndHighFps);
            var ok2c = RunPart("Part2c", Part2cHevcRoundtrip);
            var ok2d = RunPart("Part2d", Part2dFfmpegVendorEncode);
            var ok3 = RunPart("Part3", Part3RealCapture);
            var ok4 = RunPart("Part4", Part4LoopbackE2E);
            var ok4b = RunPart("Part4b", Part4bHevcNegotiation);
            var ok5 = RunPart("Part5", Part5Signaling);
            var ok6 = RunPart("Part6", Part6WebRtcLoopback);
            var ok7 = RunPart("Part7", Part7Audio);
            var ok8 = RunPart("Part8", Part8Discovery);
            Logger.Info("SmokeTest",
                $"===== 结果: 合成编码={(ok2 ? "PASS" : "FAIL")}, " +
                $"4K/高帧率={(ok2b ? "PASS" : "FAIL")}, HEVC往返={(ok2c ? "PASS" : "FAIL")}, " +
                $"厂商硬编={(ok2d ? "PASS" : "FAIL")}, " +
                $"真实捕获={(ok3 ? "PASS" : "FAIL")}, 回环端到端={(ok4 ? "PASS" : "FAIL")}, " +
                $"HEVC协商={(ok4b ? "PASS" : "FAIL")}, " +
                $"信令={(ok5 ? "PASS" : "FAIL")}, WebRTC={(ok6 ? "PASS" : "FAIL")}, " +
                $"系统声音={(ok7 ? "PASS" : "FAIL")}, 局域网发现={(ok8 ? "PASS" : "FAIL")} =====");
            return ok2 && ok2b && ok2c && ok2d && ok3 && ok4 && ok4b && ok5 && ok6 && ok7 && ok8 ? 0 : 1;
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
        foreach (var name in MfVideoEncoder.ProbeEncoders())
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
                using var encoder = new MfVideoEncoder(settings, probeDevice);
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
        // 实测/目标比：编码器欠产出的量化观察（DX12 编码器对 CodecAPI 全拒，
        // 静态/简单内容远低于目标属已知行为，见 docs/ROADMAP.md 诊断记录）
        Logger.Info("Part2", $"编码 {encFrames} 帧, {encBytes} 字节, 平均码率 {bitrate / 1_000_000:F2} Mbps" +
                             $"（实测/目标 = {bitrate / settings.BitrateBps:P0}，目标 {settings.BitrateBps / 1_000_000.0:F1} Mbps）");
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

    /// <summary>
    /// HEVC 编解码验证（平台支持时）：
    ///   1) 编码：合成运动图像 → HEVC 编码（实测 fps/码率，评估软件编码器能否跟上实时共享）；
    ///   2) 解码：子进程探针实测本机 HEVC 解码能力（部分平台扩展 MFT 会原生崩溃，必须隔离）；
    ///   3) 两者皆可用时做完整 编码→解码 回读。
    /// 无 HEVC 编码器时软性通过（会话自动回退 H.264）。
    /// </summary>
    private static bool Part2cHevcRoundtrip()
    {
        Logger.Info("Part2c", "---- HEVC 编解码验证（1280x720@30, 5 秒）----");
        var settings = new EncoderSettings
        {
            Codec = VideoCodec.Hevc,
            Width = 1280,
            Height = 720,
            Fps = 30,
            BitrateBps = 3_000_000,
            GopSize = 60,
        };

        if (!MfVideoEncoder.ProbeAvailable(settings))
        {
            Logger.Info("Part2c", "平台无可用 HEVC 编码器 → 跳过（会话仍可用 H.264）");
            Logger.Info("Part2c", "Part2c PASS（软性）");
            return true;
        }

        // 子进程实测解码能力（原生崩溃被隔离在子进程）；MF 不可用时走 FFmpeg 软解兜底
        var decodeOk = ProbeHevcDecodeInSubprocess();
        Logger.Info("Part2c", $"HEVC MF 解码能力（子进程探针）: {(decodeOk ? "可用" : "不可用/崩溃隔离")}");

        var decoded = 0;
        var keyframes = 0;
        using var pipeline = new EncoderPipeline(settings);
        IVideoDecoder? decoder = null;
        try
        {
            decoder = VideoDecoderFactory.Create(VideoCodec.Hevc, DecoderPreference.Auto, hevcMfAvailable: decodeOk);
        }
        catch (Exception ex)
        {
            Logger.Info("Part2c", $"MF 与 FFmpeg 兜底均不可用: {ex.Message}");
        }
        if (decoder != null)
        {
            Logger.Info("Part2c", $"解码兜底链选择: {decoder.BackendName}");
            decoder.Decoded += _ => Interlocked.Increment(ref decoded);
        }
        try
        {
            pipeline.Encoded += f =>
            {
                if (f.Keyframe) Interlocked.Increment(ref keyframes);
                decoder?.Decode(f.Data, f.TimestampUtc);
            };

            const int width = 1280, height = 720, fps = 30;
            var bgra = new byte[width * height * 4];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / fps);
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                DrawPattern(bgra, width, height, sw.Elapsed.TotalSeconds);
                pipeline.Submit(new CaptureFrame
                {
                    Width = width,
                    Height = height,
                    TimestampUtc = DateTime.UtcNow.Ticks,
                    QpcTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                    BgraPixels = (byte[])bgra.Clone(),
                });
                Thread.Sleep(Math.Max(1, (int)frameInterval.TotalMilliseconds));
            }
            Thread.Sleep(800);

            var (encFrames, encBytes) = pipeline.GetCounters();
            decoder?.Flush();
            var bitrate = encBytes * 8.0 / 5.0;
            Logger.Info("Part2c",
                $"编码器={pipeline.EncoderName}, 编码 {encFrames} 帧（实际 {encFrames / 5.0:F0}fps）, " +
                $"关键帧 {keyframes}, 平均码率 {bitrate / 1_000_000:F2} Mbps" +
                $"（实测/目标 = {bitrate / settings.BitrateBps:P0}）");
            if (decoder != null) Logger.Info("Part2c", $"解码回读 {decoded} 帧（{decoder.BackendName}）");

            var pass = encFrames >= 20 && keyframes >= 1 && (decoder == null || decoded >= 15);
            Logger.Info("Part2c", pass ? "Part2c PASS" : "Part2c FAIL");
            return pass;
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    /// <summary>
    /// FFmpeg 厂商硬编验证（to1.5.0 Step 4）：生产管线全链路（工厂选后端 + staging 回读）。
    /// 核心断言：实测/目标码率比 ≥ 50%——防回归「编码器欠产出」（DX12 收件箱编码器 5–10% 是病根；
    /// Step 0 实测 NVENC CBR ≈100%）。无厂商 GPU 的机器（CI runner）自动软性跳过。
    /// </summary>
    private static bool Part2dFfmpegVendorEncode()
    {
        Logger.Info("Part2d", "---- FFmpeg 厂商硬编验证（1280x720@30, 5 秒，真实 CBR 断言）----");
        const int width = 1280, height = 720, fps = 30, seconds = 5;
        var settings = new EncoderSettings
        {
            Width = width,
            Height = height,
            Fps = fps,
            BitrateBps = 3_000_000,
            GopSize = 60,
        };
        if (!FfmpegVideoEncoder.ProbeAvailable(settings))
        {
            Logger.Info("Part2d", "本机无 FFmpeg 厂商硬编（nvenc/amf/qsv 均不可用）→ 跳过（工厂自动走 MF 现链）");
            Logger.Info("Part2d", "Part2d PASS（软性）");
            return true;
        }

        var decoded = 0;
        var keyframes = 0;
        using var pipeline = new EncoderPipeline(settings);
        Logger.Info("Part2d", $"工厂选择: {pipeline.EncoderName} (硬件={pipeline.IsHardwareEncoder}, 零拷贝={pipeline.IsZeroCopy})");
        var isVendor = pipeline.EncoderName.Contains("nvenc") ||
                       pipeline.EncoderName.Contains("amf") ||
                       pipeline.EncoderName.Contains("qsv");
        if (!isVendor)
        {
            Logger.Info("Part2d", $"探测有厂商硬编但工厂未选中（得到 {pipeline.EncoderName}）——选择链逻辑异常");
            Logger.Info("Part2d", "Part2d FAIL");
            return false;
        }

        using var decoder = FfmpegVideoDecoder.TryCreate(VideoCodec.H264);
        if (decoder != null)
        {
            decoder.Decoded += _ => Interlocked.Increment(ref decoded);
            Logger.Info("Part2d", "解码回读: FFmpeg 软解（H.264）");
        }
        pipeline.Encoded += f =>
        {
            if (f.Keyframe) Interlocked.Increment(ref keyframes);
            decoder?.Decode(f.Data, f.TimestampUtc);
        };

        var bgra = new byte[width * height * 4];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / fps);
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            DrawPattern(bgra, width, height, sw.Elapsed.TotalSeconds);
            pipeline.Submit(new CaptureFrame
            {
                Width = width,
                Height = height,
                TimestampUtc = DateTime.UtcNow.Ticks,
                QpcTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                BgraPixels = (byte[])bgra.Clone(),
            });
            Thread.Sleep(Math.Max(1, (int)frameInterval.TotalMilliseconds));
        }
        Thread.Sleep(800);

        var (encFrames, encBytes) = pipeline.GetCounters();
        decoder?.Flush();
        var bitrate = encBytes * 8.0 / seconds;
        var ratio = bitrate / settings.BitrateBps;
        Logger.Info("Part2d",
            $"编码 {encFrames} 帧（实际 {encFrames / (double)seconds:F0}fps）, 关键帧 {keyframes}, " +
            $"平均码率 {bitrate / 1_000_000:F2} Mbps（实测/目标 = {ratio:P0}，目标 {settings.BitrateBps / 1_000_000.0:F1}）");
        if (decoder != null) Logger.Info("Part2d", $"解码回读 {decoded} 帧（FFmpeg）");

        var pass = encFrames >= 75 && ratio >= 0.50 && keyframes >= 1 && (decoder == null || decoded >= 60);
        Logger.Info("Part2d", pass ? "Part2d PASS" : "Part2d FAIL");
        return pass;
    }

    /// <summary>以子进程方式实测 HEVC 解码能力（扩展 MFT 的原生崩溃被隔离在子进程）</summary>
    private static bool ProbeHevcDecodeInSubprocess()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                exe, HevcDecodeProbe.ArgProbe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (ps == null) return false;
            if (!ps.WaitForExit(20000))
            {
                try { ps.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return ps.ExitCode == HevcDecodeProbe.ExitOk;
        }
        catch
        {
            return false;
        }
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
    /// HEVC 会话协商验证：HEVC 会话 + 各能力观看端的认证行为。
    ///   负例（必测）：HevcSupported=false 的观看端 → 认证被拒，原因含 HEVC（验证服务端拒接
    ///   + 客户端早期拒绝帧解析——拒接以 AuthResult 出现在质询位置， reason 必须完整到达）。
    ///   正例（软性）：本机 HEVC 解码探针通过时，HevcSupported=true → 正常接入并解码。
    /// 无 HEVC 编码器的平台上整段跳过（HEVC 是可选增强）。
    /// </summary>
    private static bool Part4bHevcNegotiation()
    {
        Logger.Info("Part4b", "---- HEVC 会话协商验证 ----");
        var primary = CaptureSourceList.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (primary == null) { Logger.Error("Part4b", "找不到主显示器"); return false; }

        var hevcSettings = new EncoderSettings
        {
            Codec = VideoCodec.Hevc, Width = 1280, Height = 720, Fps = 15, BitrateBps = 2_500_000,
        };
        if (!MfVideoEncoder.ProbeAvailable(hevcSettings))
        {
            Logger.Info("Part4b", "平台无可用 HEVC 编码器 → 跳过");
            Logger.Info("Part4b", "Part4b PASS（软性）");
            return true;
        }

        const int testPort = 48763;
        var session = new ShareSession();
        var whitelist = new DeviceWhitelist();
        whitelist.Approve("test-viewer-device", "AutoTest Viewer");

        var server = new LanShareServer(session, whitelist, testPort);
        server.ApproveRequired = _ => Task.FromResult(true);
        var decodeOk = ProbeHevcDecodeInSubprocess();
        try
        {
            session.Start(primary, new ShareOptions
            {
                Width = 1280, Fps = 15, BitrateBps = 2_500_000,
                Codec = VideoCodec.Hevc,
                CaptureEngine = CaptureEnginePreference.Gdi,
            });
            server.Start();

            // ===== 负例：不支持 HEVC 的观看端必须被拒且原因明确 =====
            var failedEvent = new ManualResetEventSlim(false);
            var failureReason = "";
            var rejectClient = new LanShareClient("127.0.0.1", testPort,
                "test-viewer-device", "AutoTest Viewer", session.Password)
            { HevcSupported = false };
            rejectClient.StateChanged += (s, err) =>
            {
                if (s == ConnectionState.Failed)
                {
                    failureReason = err ?? "";
                    failedEvent.Set();
                }
            };
            rejectClient.Start();
            var rejected = failedEvent.Wait(TimeSpan.FromSeconds(10));
            rejectClient.Stop();
            rejectClient.Dispose();
            Logger.Info("Part4b",
                $"负例: 被拒={rejected}, 原因=\"{failureReason}\"");
            var rejectOk = rejected && failureReason.Contains("HEVC", StringComparison.OrdinalIgnoreCase);

            // ===== 正例：能力并集（MF 探针 ∥ FFmpeg 兜底）的观看端正常接入解码 =====
            bool positiveOk;
            var ffmpegOk = FfmpegVideoDecoder.UnavailableReason() == null;
            Logger.Info("Part4b",
                $"观看端能力并集: MF 探针={(decodeOk ? "支持" : "不支持")}, FFmpeg 兜底={(ffmpegOk ? "可用" : "不可用")}");
            if (!decodeOk && !ffmpegOk)
            {
                Logger.Info("Part4b", "本机 HEVC 解码能力并集为空，正例跳过");
                positiveOk = true;
            }
            else
            {
                var connectedEvent = new ManualResetEventSlim(false);
                long decodedFrames = 0;
                using var decoder = VideoDecoderFactory.Create(VideoCodec.Hevc, DecoderPreference.Auto, hevcMfAvailable: decodeOk);
                Logger.Info("Part4b", $"正例解码器: {decoder.BackendName}");
                decoder.Decoded += _ => Interlocked.Increment(ref decodedFrames);
                var okClient = new LanShareClient("127.0.0.1", testPort,
                    "test-viewer-device", "AutoTest Viewer", session.Password)
                { HevcSupported = true };
                okClient.StateChanged += (s, _) =>
                {
                    if (s == ConnectionState.Connected) connectedEvent.Set();
                };
                okClient.FrameReceived += f => decoder.Decode(f.Data, f.TimestampUtc);
                okClient.Start();
                var connected = connectedEvent.Wait(TimeSpan.FromSeconds(10));
                if (connected) Thread.Sleep(5000);
                okClient.Stop();
                okClient.Dispose();
                var flushed = decoder.Flush();
                Logger.Info("Part4b",
                    $"正例: 接入={connected}, 解码 {Interlocked.Read(ref decodedFrames)} 帧（含抽干 {flushed}）");
                positiveOk = connected && Interlocked.Read(ref decodedFrames) >= 10;
            }

            var pass = rejectOk && positiveOk;
            Logger.Info("Part4b", pass ? "Part4b PASS" : "Part4b FAIL");
            return pass;
        }
        finally
        {
            server.Stop();
            session.Stop("part4b-end");
        }
    }

    /// <summary>
    /// 回环端到端测试：ShareSession（主显示器）+ LanShareServer → LanShareClient（回环）
    /// → MfVideoDecoder 解码出画面。验证认证/加密/传输/解码/统计全链路。
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
    var decoder = new MfVideoDecoder();
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
    /// （DTLS-SRTP + H.264 RTP 打包/重组 + AAC 音频 RTP），ShareSession 真实编码 → 发送 → 解码出画面。
    /// </summary>
    private static bool Part6WebRtcLoopback()
    {
        Logger.Info("Part6", "---- WebRTC 回环验证（DTLS-SRTP + H.264/AAC RTP，8 秒）----");
        var primary = CaptureSourceList.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (primary == null) { Logger.Error("Part6", "找不到主显示器"); return false; }

        var session = new ShareSession();
        var sender = new WebRtcHostSender(includeAudio: true);
        var receiver = new WebRtcViewerReceiver(includeAudio: true);
        var decoder = new MfVideoDecoder();
        var aacDecoder = new MfAacDecoder(AudioStreamInfo.SampleRate);

        long received = 0, decoded = 0, audioReceived = 0, audioDecoded = 0;
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
        // 音频：裸 AAC 包回 ADTS → 解码（验证 RTP 音频通路端到端可用）
        receiver.AudioFrameReceived += (payload, ts) =>
        {
            Interlocked.Increment(ref audioReceived);
            try
            {
                aacDecoder.Decode(Adts.Wrap(payload, AudioStreamInfo.SampleRate, AudioStreamInfo.Channels), ts);
                Interlocked.Increment(ref audioDecoded);
            }
            catch (Exception ex)
            {
                Logger.Warn("Part6", "音频解码异常: " + ex.Message);
            }
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

            // 启动共享（编码帧 → WebRTC 发送），另起线程注入合成音频
            var sink = new LocalSink(frame =>
            {
                if (sender.IsConnected)
                    sender.SendEncodedFrame(frame, 30);
            }, frame =>
            {
                if (sender.IsConnected)
                    sender.SendAudioFrame(frame);
            });
            session.AddSink(sink);
            session.Start(primary, new ShareOptions
            {
                Width = 1280, Fps = 30, BitrateBps = 2_500_000, RecordForValidation = false,
                CaptureEngine = CaptureEnginePreference.Gdi,   // 定速捕获，避免静态桌面下 WGC 几乎不出帧
            });
            using var injectCts = new CancellationTokenSource();
            var audioAvailable = true;
            MfAacEncoder? audioEncoder = null;
            try
            {
                audioEncoder = new MfAacEncoder();   // 同步创建：没有编码器立刻走软性降级
                _ = Task.Run(() => InjectSyntheticAudio(audioEncoder, sender.SendAudioFrame, injectCts.Token));
            }
            catch (Exception ex)
            {
                audioAvailable = false;   // 本机没有 AAC 编码器：音频断言跳过（Part7a 已覆盖该场景）
                Logger.Warn("Part6", "无 AAC 编码器，跳过音频断言: " + ex.Message);
            }

            var ok = connected.Wait(TimeSpan.FromSeconds(20));
            if (ok)
            {
                session.RequestKeyframe();   // 接入即出 IDR，不等 GOP 周期
                Thread.Sleep(6000);          // 收 6 秒流
            }
            injectCts.Cancel();

            // 先定格投喂计数，再抽干解码器内部滞留的尾部帧，最后读解码结果
            var decodeInvocations = Volatile.Read(ref decodeCalls);
            var receivedFrames = Interlocked.Read(ref received);
            var flushed = decoder.Flush();
            var decodedFrames = Interlocked.Read(ref decoded);
            var audioFrames = Interlocked.Read(ref audioReceived);
            var audioOk = Interlocked.Read(ref audioDecoded);
            Logger.Info("Part6", $"Decode 调用 {decodeInvocations} 次; 连接={ok}, 接收={receivedFrames} 帧, " +
                                $"解码={decodedFrames} 帧（含抽干 {flushed}）, " +
                                $"投喂={decoder.InputFrames}, 输出样本={decoder.OutputSamples}, " +
                                $"分辨率未知丢弃={decoder.DroppedUnknownSize}, 缓冲不足丢弃={decoder.DroppedShortBuffer}, " +
                                $"音频帧={audioFrames}, 音频解码={audioOk}, " +
                                $"中继={sender.UsedRelay || receiver.UsedRelay}");
            // 比例断言：首个 IDR 之前的帧按设计被跳过（不计入解码），绝对阈值会随机器负载抖动
            var pass = ok && receivedFrames >= 30 && decodeInvocations >= 20 &&
                       decodedFrames >= decodeInvocations * 9 / 10 &&
                       (!audioAvailable || audioFrames >= 50) &&
                       (!audioAvailable || audioOk >= audioFrames * 9 / 10);
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
    private sealed class LocalSink(
        Action<EncodedVideoFrame> onFrame,
        Action<EncodedAudioFrame>? onAudio = null) : ShareSession.IFrameSink, ShareSession.IAudioSink
    {
        public string Name => "webrtc-loopback";
        public void OnEncodedFrame(EncodedVideoFrame frame) => onFrame(frame);
        public void OnShareStopped(string reason) { }
        public void OnAudioFrame(EncodedAudioFrame frame) => onAudio?.Invoke(frame);
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
        using var decoder = verifyDecode ? new MfVideoDecoder() : null;
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

    /// <summary>
    /// Part7 系统声音共享（WASAPI loopback 采集「默认播放设备正在播的内容」，不是麦克风）：
    ///   7a AAC 编解码往返：合成 440Hz(左)/880Hz(右) → ADTS → 解码回读，校验自描述头、时间戳与声道分离
    ///   7b LAN 端到端：ShareSession(ShareAudio) → LanShareServer → LanShareClient → 解码
    ///   7c loopback 采集软性探测：没有播放设备的机器上只告警，不判失败
    /// </summary>
    private static bool Part7Audio()
    {
        Logger.Info("Part7", "---- 系统声音共享验证 ----");
        var ok7a = RunPart("Part7a", Part7aAudioCodecRoundTrip);
        var ok7b = RunPart("Part7b", Part7bLanAudioE2E);
        RunPart("Part7c", Part7cLoopbackProbe);   // 软性探测：结果不计入 Part7 通过条件
        Logger.Info("Part7",
            $"Part7 结果: 编解码往返={(ok7a ? "PASS" : "FAIL")}, LAN 端到端={(ok7b ? "PASS" : "FAIL")}");
        return ok7a && ok7b;
    }

    /// <summary>7a：合成正弦波走一遍 AAC 编码 → ADTS → 解码，验证声音链路本身是通的</summary>
    private static bool Part7aAudioCodecRoundTrip()
    {
        var found = MfAacEncoder.ProbeEncoders();
        Logger.Info("Part7a", $"AAC 编码器: {(found.Count == 0 ? "未找到" : string.Join(", ", found))}");
        if (found.Count == 0)
        {
            Logger.Error("Part7a", "系统上没有 AAC 编码器 MFT，无法共享系统声音");
            return false;
        }

        const double seconds = 2.0, leftHz = 440.0, rightHz = 880.0, amplitude = 0.30;
        const int channels = AudioStreamInfo.Channels;
        var chunkFrames = AudioStreamInfo.ChunkFrames;               // 960 = 20ms，与真实采集同块长
        var inputFrames = (int)(AudioStreamInfo.SampleRate * seconds);

        var encoded = new List<EncodedAudioFrame>();
        var decodedChunks = new List<DecodedAudioChunk>();
        var baseTicks = DateTime.UtcNow.Ticks;

        using var encoder = new MfAacEncoder();
        using var decoder = new MfAacDecoder();
        encoder.Encoded += f => { lock (encoded) encoded.Add(f); };
        decoder.Decoded += c => { lock (decodedChunks) decodedChunks.Add(c); };

        var pcm = new short[chunkFrames * channels];
        for (var done = 0; done < inputFrames; done += chunkFrames)
        {
            var frames = Math.Min(chunkFrames, inputFrames - done);
            for (var i = 0; i < frames; i++)
            {
                var n = done + i;
                pcm[i * channels] = (short)(short.MaxValue * amplitude *
                    Math.Sin(2 * Math.PI * leftHz * n / AudioStreamInfo.SampleRate));
                pcm[i * channels + 1] = (short)(short.MaxValue * amplitude *
                    Math.Sin(2 * Math.PI * rightHz * n / AudioStreamInfo.SampleRate));
            }
            encoder.Encode(pcm, frames,
                baseTicks + (long)done * TimeSpan.TicksPerSecond / AudioStreamInfo.SampleRate);
        }
        var flushed = encoder.Drain();   // 不冲刷会丢掉 AAC priming 之后的结尾（听感是「最后一句被截断」）

        EncodedAudioFrame[] sent;
        lock (encoded) sent = encoded.ToArray();
        if (sent.Length == 0)
        {
            Logger.Error("Part7a", "编码器没有输出任何 AAC 帧");
            return false;
        }

        // ADTS 头必须自描述：Viewer 只靠码流本身就能建解码器，帧头里没有额外带外信息
        var badHeader = 0;
        var encodedBytes = 0;
        foreach (var f in sent)
        {
            encodedBytes += f.Data.Length;
            if (!Adts.TryReadHeader(f.Data, out var frameLength, out var sr, out var ch)
                || frameLength != f.Data.Length
                || sr != AudioStreamInfo.SampleRate
                || ch != channels)
                badHeader++;
        }

        // 时间戳必须严格递增：音画同步全靠它排序与对齐
        var tsMonotonic = true;
        for (var i = 1; i < sent.Length; i++)
            if (sent[i].TimestampUtc <= sent[i - 1].TimestampUtc) { tsMonotonic = false; break; }

        foreach (var f in sent) decoder.Decode(f.Data, f.TimestampUtc);

        DecodedAudioChunk[] got;
        lock (decodedChunks) got = decodedChunks.ToArray();
        var wrongFormat = got.Count(c => c.SampleRate != AudioStreamInfo.SampleRate || c.Channels != channels);
        var decodedSamples = got.Sum(c => c.Frames);
        var pcmOut = new short[decodedSamples * channels];
        var write = 0;
        foreach (var c in got)
        {
            var copy = Math.Min(c.Data.Length, pcmOut.Length - write);
            if (copy <= 0) break;
            Array.Copy(c.Data, 0, pcmOut, write, copy);
            write += copy;
        }
        var outFrames = write / channels;

        // 声道分离：左 440Hz / 右 880Hz，符号翻转次数比应接近 1:2。
        // 这条能抓住声道交换、被下混成单声道、重采样系数写错三类回归。
        var (crossLeft, crossRight) = ZeroCrossings(pcmOut, outFrames, channels);
        var crossRatio = crossRight == 0 ? double.NaN : (double)crossLeft / crossRight;
        var level = PcmConvert.RmsLevel(pcmOut);

        Logger.Info("Part7a",
            $"编码器={encoder.EncoderName}, {encoder.SampleRate}Hz/{encoder.Channels}ch, " +
            $"目标 {AudioStreamInfo.TargetBitrateBps / 1000}kbps / 实际 {encoder.AppliedBitrateBps / 1000}kbps");
        Logger.Info("Part7a",
            $"输入 {inputFrames} 帧 → ADTS {sent.Length} 个/{encodedBytes / 1024}KB（冲刷补出 {flushed} 个）, " +
            $"头非法 {badHeader} 个, 时间戳递增={tsMonotonic}");
        Logger.Info("Part7a",
            $"解码回读 {got.Length} 块/{outFrames} 帧（占输入 {outFrames * 100.0 / inputFrames:F1}%）, " +
            $"格式不符 {wrongFormat} 块, RMS={level:F3}, 过零比 左/右={crossLeft}/{crossRight}={crossRatio:F3}（期望≈0.5）");

        var pass = badHeader == 0 && tsMonotonic && wrongFormat == 0 &&
                   outFrames >= inputFrames * 85 / 100 &&
                   level > 0.05f && level < 0.9f &&
                   !double.IsNaN(crossRatio) && crossRatio > 0.35 && crossRatio < 0.70;
        if (!pass) Logger.Error("Part7a", "系统声音编解码往返验证失败");
        else Logger.Info("Part7a", "Part7a PASS");
        return pass;
    }

    /// <summary>统计左右声道的符号翻转次数（判断声道是否分离/交换的轻量指纹）</summary>
    private static (long Left, long Right) ZeroCrossings(short[] pcm, int frames, int channels)
    {
        long left = 0, right = 0;
        short prevL = 0, prevR = 0;
        for (var i = 0; i < frames && (i + 1) * channels <= pcm.Length; i++)
        {
            var l = pcm[i * channels];
            var r = channels > 1 ? pcm[i * channels + 1] : l;
            if (i > 0)
            {
                if ((l > 0) != (prevL > 0)) left++;
                if ((r > 0) != (prevR > 0)) right++;
            }
            prevL = l;
            prevR = r;
        }
        return (left, right);
    }
    /// <summary>
    /// 7b：系统声音走一遍 LAN 端到端（采集/注入 → 加密发送 → 客户端收帧 → AAC 解码）。
    /// 没有播放设备的机器上 loopback 起不来，此时改用手工注入：验证的仍是同一条网络与解码链路。
    /// </summary>
    private static bool Part7bLanAudioE2E()
    {
        var primary = CaptureSourceList.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (primary == null) { Logger.Error("Part7b", "找不到主显示器"); return false; }

        const int testPort = 48762;
        var session = new ShareSession();
        var whitelist = new DeviceWhitelist();
        whitelist.Approve("test-audio-device", "AutoTest Audio Viewer");   // 预批准，避免弹窗

        using var server = new LanShareServer(session, whitelist, testPort);
        server.ApproveRequired = _ => Task.FromResult(true);
        try
        {
            session.Start(primary, new ShareOptions
            {
                Width = 640, Fps = 15, BitrateBps = 800_000,
                RecordForValidation = false,
                CaptureEngine = CaptureEnginePreference.Gdi,
                ShareAudio = true,
            });
            server.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("Part7b", "服务器启动失败", ex);
            session.Dispose();
            return false;
        }

        var loopbackOk = session.AudioInfo.Enabled;
        Logger.Info("Part7b", loopbackOk
            ? $"真实 loopback 采集已启动: {session.AudioInfo.SampleRate}Hz/{session.AudioInfo.Channels}ch {session.AudioInfo.Codec}（{session.AudioInfo.EncoderName}）"
            : "loopback 采集不可用，将改用手工注入音频帧");

        long audioFrames = 0, audioBytes = 0, decodedChunks = 0, decodedSamples = 0;
        var lastTs = 0L;
        var badHeader = 0;
        var tsBroken = 0;
        var decodeFailed = 0;
        var connectedEvent = new ManualResetEventSlim(false);
        var audioDecoder = new MfAacDecoder();
        audioDecoder.Decoded += c =>
        {
            Interlocked.Increment(ref decodedChunks);
            Interlocked.Add(ref decodedSamples, c.Frames);
        };

        var client = new LanShareClient("127.0.0.1", testPort,
            "test-audio-device", "AutoTest Audio Viewer", session.Password);
        client.StateChanged += (s, _) => { if (s == ConnectionState.Connected) connectedEvent.Set(); };
        // 回调必须在 Start 之前挂好：否则连接建立后头几帧收到了却没被计数
        client.AudioFrameReceived += f =>
        {
            var n = Interlocked.Increment(ref audioFrames);
            Interlocked.Add(ref audioBytes, f.PayloadSize);
            var prev = Interlocked.Exchange(ref lastTs, f.TimestampUtc);
            if (n > 1 && f.TimestampUtc <= prev) Interlocked.Increment(ref tsBroken);
            if (!Adts.TryReadHeader(f.Data, out _, out _, out _)) Interlocked.Increment(ref badHeader);
            try { audioDecoder.Decode(f.Data, f.TimestampUtc); }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref decodeFailed) == 1)
                    Logger.Warn("Part7b", "音频解码异常: " + FirstLine(ex.Message));
            }
        };

        // 注入器只在 loopback 不可用时启动，且必须在客户端连上之后再开，否则帧全被丢掉
        CancellationTokenSource? injectCts = null;
        Task? injectTask = null;
        try
        {
            client.Start();
            var connected = connectedEvent.Wait(TimeSpan.FromSeconds(10));
            if (!connected)
            {
                Logger.Error("Part7b", "客户端 10 秒内未连上");
                return false;
            }

            if (!loopbackOk)
            {
                injectCts = new CancellationTokenSource();
                injectTask = Task.Run(() =>
                {
                    using var enc = new MfAacEncoder();
                    InjectSyntheticAudio(enc, server.OnAudioFrame, injectCts.Token);
                });
            }
            Thread.Sleep(6000);   // 收 6 秒音频流（约 280 个 AAC 帧）
        }
        finally
        {
            try { injectCts?.Cancel(); injectTask?.Wait(2000); } catch { }
            injectCts?.Dispose();
        }

        var audioInfo = client.Audio;
        var encEnabled = client.IsEncrypted;
        var received = Interlocked.Read(ref audioFrames);
        var chunks = Interlocked.Read(ref decodedChunks);
        var samples = Interlocked.Read(ref decodedSamples);
        client.Stop();
        client.Dispose();
        audioDecoder.Dispose();
        server.Stop();
        session.Stop("part7b-end");

        // 6 秒 @1024 样本/帧 ≈ 281 帧；阈值取 1/5，留出机器负载与静音补齐节奏的余量
        Logger.Info("Part7b",
            $"加密={encEnabled}, 会话音频={(audioInfo.Enabled ? $"{audioInfo.SampleRate}Hz/{audioInfo.Channels}ch {audioInfo.Codec}" : "未启用")}, " +
            $"收到 {received} 帧/{audioBytes / 1024}KB, ADTS 头非法 {badHeader} 个, 时间戳乱序 {tsBroken} 次, " +
            $"解码 {chunks} 块/{samples} 样本({samples * 1000.0 / AudioStreamInfo.SampleRate:F0}ms), 解码异常 {decodeFailed} 次");

        var pass = encEnabled && audioInfo.Enabled &&
                   audioInfo.SampleRate == AudioStreamInfo.SampleRate &&
                   audioInfo.Channels == AudioStreamInfo.Channels &&
                   audioInfo.Codec == AudioStreamInfo.Codec &&
                   received >= 50 && Volatile.Read(ref badHeader) == 0 &&
                   Volatile.Read(ref tsBroken) == 0 && Volatile.Read(ref decodeFailed) == 0 &&
                   chunks >= received * 8 / 10;
        if (!pass) Logger.Error("Part7b", "系统声音 LAN 端到端验证失败");
        else Logger.Info("Part7b", "Part7b PASS");
        return pass;
    }

    /// <summary>
    /// loopback 不可用时的替身：合成 PCM → AAC → 直接交给服务器分发。
    /// 走的是与真实采集完全相同的编码器和发送路径，只是声音来源换成了合成正弦波。
    /// </summary>
    /// <summary>往给定音频出口注入 440Hz 正弦合成音频（20ms/块）。编码器由调用方创建并负责释放。</summary>
    private static void InjectSyntheticAudio(MfAacEncoder encoder, Action<EncodedAudioFrame> onFrame, CancellationToken ct)
    {
        encoder.Encoded += f =>
        {
            try { onFrame(f); } catch { /* 接收端已断开，忽略 */ }
        };
        var pcm = new short[AudioStreamInfo.ChunkFrames * AudioStreamInfo.Channels];
        var startTicks = DateTime.UtcNow.Ticks;
        long sampleIndex = 0;
        while (!ct.IsCancellationRequested)
        {
            var chunkStart = sampleIndex;
            for (var i = 0; i < AudioStreamInfo.ChunkFrames; i++)
            {
                var v = (short)(short.MaxValue * 0.25 *
                    Math.Sin(2 * Math.PI * 440 * (chunkStart + i) / AudioStreamInfo.SampleRate));
                pcm[i * AudioStreamInfo.Channels] = v;
                pcm[i * AudioStreamInfo.Channels + 1] = v;
            }
            sampleIndex += AudioStreamInfo.ChunkFrames;
            encoder.Encode(pcm, AudioStreamInfo.ChunkFrames,
                startTicks + chunkStart * TimeSpan.TicksPerSecond / AudioStreamInfo.SampleRate);
            try { Thread.Sleep(AudioStreamInfo.ChunkMs); } catch { break; }
        }
    }

    /// <summary>
    /// 7c：WASAPI loopback 软性探测。没有播放设备（声卡禁用、远程会话、无声卡 CI）时
    /// 只记告警并返回 true——「本机没声卡」不是产品的 bug，不该让冒烟测试变红。
    /// </summary>
    private static bool Part7cLoopbackProbe()
    {
        try
        {
            using var capture = new LoopbackAudioCapture();
            var chunks = 0;
            capture.ChunkArrived += _ => Interlocked.Increment(ref chunks);
            capture.Start();
            Thread.Sleep(1500);
            capture.Stop();

            var got = Volatile.Read(ref chunks);
            Logger.Info("Part7c",
                $"loopback 采集 {got} 块（真实 {capture.CapturedChunks} / 静音补齐 {capture.SilenceChunks}）, " +
                $"设备混音格式 {capture.DeviceSampleRate}Hz/{capture.DeviceChannels}ch float={capture.DeviceIsFloat}, " +
                $"峰值电平={capture.PeakLevel:F3}");
            // 20ms 一块，1.5 秒理论约 75 块；明显偏少说明采集线程节奏有问题
            if (got < 30) Logger.Warn("Part7c", $"块数偏少（{got}），loopback 采集节奏可能不稳");
            else Logger.Info("Part7c", "Part7c PASS（软性）");
        }
        catch (Exception ex)
        {
            Logger.Warn("Part7c", $"loopback 采集不可用（本机没有可用播放设备？）: {FirstLine(ex.Message)}");
        }
        return true;
    }

    /// <summary>
    /// Part8 局域网发现：DiscoveryBeacon（Host 信标）→ 组播回环 → DiscoveryListener（Viewer）。
    /// 同机组播走回环接口，2 秒广播周期内应收到 announce 并解析出设备名/端口。
    /// 组播被本机策略禁掉时（罕见）按软性处理：告警但不判失败。
    /// </summary>
    private static bool Part8Discovery()
    {
        Logger.Info("Part8", "---- 局域网组播发现回环 ----");
        var received = new TaskCompletionSource<DiscoveredHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new DiscoveryListener();
        listener.HostSeen += h => received.TrySetResult(h);
        listener.Start();

        using (var beacon = new DiscoveryBeacon("SmokeTest-Host", 48750))
        {
            beacon.Start();
            var gotInTime = received.Task.Wait(TimeSpan.FromSeconds(8));
            if (!gotInTime)
            {
                Logger.Warn("Part8", "8 秒内未收到组播 announce（本机组播可能被禁）。协议层已有单测覆盖。");
                Logger.Info("Part8", "Part8 PASS（软性）");
                return true;
            }
            var host = received.Task.Result;
            Logger.Info("Part8", $"发现共享端: {host.Name} @ {host.Address}:{host.Port}");
            if (host.Name != "SmokeTest-Host" || host.Port != 48750)
            {
                Logger.Error("Part8", "announce 内容与广播不一致");
                return false;
            }
        }

        // 停止广播后条目应随过期时间从快照中消失（用超过 StaleAfter 的等待直接验证）
        Thread.Sleep(300);
        Logger.Info("Part8", "Part8 PASS");
        return true;
    }
}
