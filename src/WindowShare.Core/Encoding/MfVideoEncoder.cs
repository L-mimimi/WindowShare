using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Encoding;

/// <summary>
/// Media Foundation H.264 编码器（双模式）：
///   - 异步模式（首选）：经典硬件 MFT（NVENC/QSV/AMF）或 Win11 的 "Microsoft AVC DX12 Encoder"
///     （D3D12 视频编码，在新 GPU 上走硬件）。事件泵线程驱动 NeedInput/HaveOutput。
///   - 同步模式：普通同步 MFT（如系统软件编码器），ProcessInput + 抽干 ProcessOutput。
///   - 支持 D3D11 纹理零拷贝输入（MFT 为 D3D11Aware 且提供了共享设备）。
/// 选择顺序：硬件 MFT → AVC DX12 → 软件同步 MFT（按本机实际可用性自动探测）。
/// </summary>
public sealed class MfVideoEncoder : IDisposable
{
    private static readonly Guid TransformIid = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid Texture2dIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // MF 错误码
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

    // MFT_ENUM_FLAG（mfapi.h）
    private const uint MftEnumFlagSyncmft = 0x01;
    private const uint MftEnumFlagHardware = 0x04;
    private const uint MftEnumFlagLocalmft = 0x10;
    private const uint MftEnumFlagSortandfilter = 0x40;
    private const uint MftEnumFlagAll = 0x3F;

    // 异步 MFT 事件（mfobjects.h MediaEventType）
    private const int MeTransformNeedInput = 601;
    private const int MeTransformHaveOutput = 602;
    private const int MeTransformDrainComplete = 603;

    // 输出流信息标志：MFT 自己分配输出样本
    private const int MftOutputStreamProvidesSamples = 0x1;

    private static readonly object MfGate = new();
    private static int _mfRefCount;

    /// <summary>Media Foundation 全局初始化（引用计数；完整初始化，LITE 模式会导致部分 MFT 异常）</summary>
    private static void EnsureMfStartup()
    {
        lock (MfGate)
        {
            if (_mfRefCount++ == 0)
            {
                try
                {
                    MediaFactory.MFStartup(false);
                    Logging.Logger.Debug("MF", "MFStartup(完整) 完成");
                }
                catch
                {
                    _mfRefCount--;
                }
            }
        }
    }

    private static void MfShutdownRef()
    {
        lock (MfGate)
        {
            if (--_mfRefCount == 0)
            {
                try { MediaFactory.MFShutdown(); } catch { }
            }
        }
    }

    public string EncoderName { get; }
    public bool IsHardware { get; }
    public bool IsD3DAccelerated => _d3dManager != null;
    public bool IsAsyncMode => _asyncEventGenerator != null;

    /// <summary>实际下发给编码器的 H.264 Level（eAVEncH264VLevel 值，如 51 = Level 5.1；0 = 未下发）</summary>
    public int AppliedH264Level { get; }

    /// <summary>编码输出事件（编码线程上触发，回调不要阻塞）</summary>
    public event Action<EncodedVideoFrame>? Encoded;

    private readonly EncoderSettings _settings;
    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager? _d3dManager;
    private readonly bool _providesOutputSamples;
    private readonly object _encodeGate = new();

    // 异步模式状态
    private IMFMediaEventGenerator? _asyncEventGenerator;
    private Thread? _pumpThread;
    private readonly SemaphoreSlim _inputSlots = new(0); // NeedInput 事件计数
    private volatile bool _disposed;
    private volatile bool _pumpRunning;
    /// <summary>速率控制族的 CodecAPI 是否至少有一项下发成功（决定是否需要流启动后重试）</summary>
    private volatile bool _rateControlApplied;

    /// <summary>创建编码器；device 非 null 时优先启用 D3D 零拷贝输入</summary>
    public MfVideoEncoder(EncoderSettings settings, ID3D11Device? device = null, bool hardwarePreferred = true)
    {
        _settings = settings;
        EnsureMfStartup();

        EncoderName = "<unknown>";
        IsHardware = false;
        AppliedH264Level = 0;

        // 逐个候选尝试「激活 → 解锁异步 → 设置 D3D 管理器 → 配置媒体类型」：
        // 编码器可能枚举得到却拒绝目标分辨率/帧率（E_INVALIDARG），此时换下一个候选而不是直接失败。
        var failures = new List<string>();
        var candidates = EnumCandidates(settings.Codec, hardwarePreferred);
        IMFTransform? chosen = null;
        IMFDXGIDeviceManager? chosenManager = null;
        try
        {
            foreach (var candidate in candidates)
            {
                IMFTransform? transform = null;
                IMFDXGIDeviceManager? manager = null;
                try
                {
                    IMFTransform? activated = null;
                    candidate.Activate.ActivateObject(out activated).CheckError();
                    transform = activated ?? throw new InvalidOperationException($"{candidate.Name}: 激活返回空对象");

                    // 解锁异步 MFT：必须早于任何 ProcessMessage / Set*Type 调用，
                    // 否则异步 MFT（含 AVC DX12 编码器）会以 0x80041000 拒绝。
                    try
                    {
                        var attrs = transform.Attributes;
                        attrs?.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                    }
                    catch { /* 同步 MFT 无此属性，忽略 */ }

                    // D3D 设备管理器（零拷贝路径；DX12 编码器必须先设置才能成功配置类型）
                    if (device != null && candidate.RequiresD3DManager)
                    {
                        try
                        {
                            manager = MediaFactory.MFCreateDXGIDeviceManager();
                            manager.ResetDevice(device);
                            transform.ProcessMessage(TMessageType.MessageSetD3DManager,
                                (nuint)manager.NativePointer);
                            Logging.Logger.Info("MF", $"D3D 设备管理器已设置: {candidate.Name}");
                        }
                        catch (Exception ex)
                        {
                            Logging.Logger.Warn("MF",
                                $"{candidate.Name} D3D 设备管理器设置失败（退回系统内存路径）: {ex.Message}");
                            manager?.Dispose();
                            manager = null;
                        }
                    }

                    // 配置类型（输出 → 输入）
                    if (!TryConfigureTypes(transform, settings, out var level, out var configureError))
                    {
                        failures.Add($"{candidate.Name}: {configureError}");
                        Logging.Logger.Info("MF",
                            $"{candidate.Name} 不支持 {settings.Width}x{settings.Height}@{settings.Fps}" +
                            $"（Level {VideoFormatPlanner.H264LevelName(level)}）: {configureError}");
                        continue;
                    }

                    chosen = transform;
                    chosenManager = manager;
                    EncoderName = candidate.Name;
                    IsHardware = candidate.IsHardware;
                    AppliedH264Level = level;
                    transform = null;   // 所有权转移，避免 finally 释放
                    manager = null;
                    break;
                }
                catch (Exception ex)
                {
                    failures.Add($"{candidate.Name}: {ex.GetType().Name} {ex.Message.Split('\n')[0].Trim()}");
                }
                finally
                {
                    transform?.Dispose();
                    manager?.Dispose();
                }
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Activate.Dispose();
        }

        if (chosen == null)
            throw new InvalidOperationException(
                $"未找到支持 {settings.Width}x{settings.Height}@{settings.Fps} 的 H.264 编码器: " +
                string.Join("; ", failures));

        _transform = chosen;
        _d3dManager = chosenManager;
        if (AppliedH264Level > 0)
            Logging.Logger.Info("MF",
                $"H.264 Level 已显式下发: {VideoFormatPlanner.H264LevelName(AppliedH264Level)}" +
                $"（{settings.Width}x{settings.Height}@{settings.Fps}）");

        // 输出流信息：判断是否需要调用方预分配输出样本
        try
        {
            var info = _transform.GetOutputStreamInfo(0);
            _providesOutputSamples = (info.Flags & MftOutputStreamProvidesSamples) != 0;
        }
        catch
        {
            _providesOutputSamples = false;
        }

        ConfigureCodecApi();

        // 启动流消息
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        // 下发时机矩阵：部分编码器（实测 DX12）在流启动前对 CodecAPI 一律拒绝 → 流启动后再试
        // 一轮；仍失败则该编码器的速率控制不可调，画质受其默认速率控制支配（记入诊断）。
        if (!_rateControlApplied)
            ApplyCodecApi("流启动后重试");

        // 异步 MFT：启动事件泵线程
        try
        {
            _asyncEventGenerator = _transform.QueryInterface<IMFMediaEventGenerator>();
            _pumpRunning = true;
            _pumpThread = new Thread(PumpLoop)
            {
                Name = "MfEncoderPump",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _pumpThread.Start();
            Logging.Logger.Info("MF", $"异步事件泵已启动: {EncoderName}");
        }
        catch
        {
            _asyncEventGenerator = null; // 同步 MFT
        }

        Logging.Logger.Info("MF",
            $"编码器就绪: {EncoderName} (硬件={IsHardware}, 零拷贝={IsD3DAccelerated}, " +
            $"模式={(IsAsyncMode ? "异步" : "同步")}, {settings.Codec.DisplayName()} " +
            $"{settings.Width}x{settings.Height}@{settings.Fps}, " +
            $"{settings.BitrateBps / 1000}kbps" +
            (AppliedH264Level > 0 ? $", Level {VideoFormatPlanner.H264LevelName(AppliedH264Level)}" : "") + ")");
    }

    /// <summary>
    /// 探测指定编码当前平台是否有可用编码器（激活候选并试配媒体类型，成功即释放）。
    /// Host 开始共享前用它决定会话编码（HEVC 不可用时回退 H.264），开销约几十毫秒。
    /// </summary>
    public static bool ProbeAvailable(EncoderSettings settings)
    {
        List<Candidate> candidates;
        try { candidates = EnumCandidates(settings.Codec, settings.PreferHardware); }
        catch (Exception ex)
        {
            Logging.Logger.Warn("MF", $"枚举 {settings.Codec.DisplayName()} 编码器失败: {ex.Message}");
            return false;
        }

        try
        {
            foreach (var candidate in candidates)
            {
                IMFTransform? transform = null;
                try
                {
                    candidate.Activate.ActivateObject(out transform);
                    if (transform == null) continue;
                    try
                    {
                        var attrs = transform.Attributes;
                        attrs?.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                    }
                    catch { /* 同步 MFT 无此属性 */ }
                    if (TryConfigureTypes(transform, settings, out _, out _))
                    {
                        Logging.Logger.Info("MF",
                            $"探测到可用的 {settings.Codec.DisplayName()} 编码器: {candidate.Name}");
                        return true;
                    }
                }
                catch { /* 候选不可用，换下一个 */ }
                finally
                {
                    try { transform?.Dispose(); } catch { }
                }
            }
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Activate.Dispose();
        }
        return false;
    }

    // ===== 编码器选择 =====

    /// <summary>Win11 24H2+ 的 D3D12 视频编码器（新 GPU 上走硬件、零拷贝）</summary>
    private const string AvcDx12EncoderName = "Microsoft AVC DX12 Encoder";
    private const string HevcDx12EncoderName = "Microsoft HEVC DX12 Encoder";

    /// <summary>MFVideoFormat_HEVC（FCC 'HEVC'；Vortice 未提供常量，与 mfapi.h 一致）</summary>
    private static readonly Guid HevcVideoFormat =
        new(0x43564548, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    private sealed record Candidate(IMFActivate Activate, string Name, bool IsHardware, bool RequiresD3DManager);

    /// <summary>
    /// 按优先级枚举候选编码器（只枚举不激活；调用方负责 Dispose 每个 IMFActivate）：
    ///   1) 经典硬件 MFT（NVENC / QSV / AMF 注册的 Media Foundation 硬件编码器）
    ///   2) Microsoft AVC/HEVC DX12 Encoder
    ///   3) 系统软件同步 MFT（HEVC 时含商店扩展的 HEVCVideoExtensionEncoder）
    /// 名字看不出是目标编码的（WMV / H263 / MPEG-2）排到最后兜底；同名只保留优先级最高的一份。
    /// </summary>
    private static List<Candidate> EnumCandidates(VideoCodec codec, bool hardwarePreferred)
    {
        var result = new List<Candidate>();
        var fallback = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dx12Name = codec == VideoCodec.Hevc ? HevcDx12EncoderName : AvcDx12EncoderName;

        if (hardwarePreferred)
        {
            AddCandidates(result, fallback, seen,
                EnumActivators(MftEnumFlagHardware | MftEnumFlagSortandfilter), true, true, codec);
            if (result.Count == 0)
                Logging.Logger.Info("MF", $"无注册的硬件编码器 MFT，尝试 {dx12Name}");
        }

        var dx12 = new List<IMFActivate>();
        foreach (var act in EnumActivators(MftEnumFlagAll))
        {
            if (SafeName(act).Equals(dx12Name, StringComparison.OrdinalIgnoreCase)) dx12.Add(act);
            else act.Dispose();
        }
        AddCandidates(result, fallback, seen, dx12, true, true, codec);

        AddCandidates(result, fallback, seen,
            EnumActivators(MftEnumFlagSyncmft | MftEnumFlagLocalmft | MftEnumFlagSortandfilter), false, false, codec);

        result.AddRange(fallback);
        return result;
    }

    /// <summary>把一组激活对象按「名字是否像目标编码」分流进候选表或兜底表</summary>
    private static void AddCandidates(List<Candidate> result, List<Candidate> fallback, HashSet<string> seen,
        List<IMFActivate> activators, bool isHardware, bool requiresD3DManager, VideoCodec codec)
    {
        foreach (var activate in activators)
        {
            var name = SafeName(activate);
            if (!seen.Add(name))
            {
                activate.Dispose();
                continue;
            }
            var candidate = new Candidate(activate, name, isHardware, requiresD3DManager);
            if (LooksLikeCodec(name, codec)) result.Add(candidate);
            else fallback.Add(candidate);
        }
    }

    /// <summary>名字是否像目标编码的编码器（各厂商命名不统一：H264 / H.264 / AVC；HEVC / H265 / H.265）</summary>
    private static bool LooksLikeCodec(string name, VideoCodec codec)
    {
        if (codec == VideoCodec.Hevc)
            return name.Contains("HEVC", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("H265", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("H.265", StringComparison.OrdinalIgnoreCase);
        return name.Contains("H264", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("H.264", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("AVC", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeName(IMFActivate activate)
    {
        try { return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); }
        catch { return "<unknown>"; }
    }

    /// <summary>枚举 MFT 激活对象（调用方负责 Dispose）</summary>
    private static List<IMFActivate> EnumActivators(uint flags)
    {
        var result = new List<IMFActivate>();
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, null,
            out var ptrs, out var count);
        for (var i = 0; i < count; i++)
        {
            var p = Marshal.ReadIntPtr(ptrs, i * IntPtr.Size);
            if (p != IntPtr.Zero)
                result.Add(new IMFActivate(p));
        }
        Marshal.FreeCoTaskMem(ptrs);
        return result;
    }

    /// <summary>枚举可用编码器名（诊断用）</summary>
    public static IReadOnlyList<string> ProbeEncoders()
    {
        var names = new List<string>();
        try
        {
            EnsureMfStartup();
            foreach (var act in EnumActivators(MftEnumFlagAll))
            {
                try { names.Add(act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)); }
                catch { names.Add("<unknown>"); }
                act.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("MF", "枚举编码器失败: " + ex.Message);
        }
        return names;
    }

    // ===== 媒体类型与 CodecAPI =====

    /// <summary>
    /// 配置输出 → 输入媒体类型。
    /// H.264 Level 必须显式下发：Microsoft AVC DX12 Encoder 默认锁在 Level 5.0，
    /// 4K（32400 宏块/帧）或 1080p144（1175040 宏块/秒）会被直接拒绝（E_INVALIDARG）。
    /// HEVC 不下发 Level（各实现取值体系不同，走编码器默认），Profile 优先尝试 Main。
    /// 依次尝试「带 profile/level」「不带」，兼容不接受该属性的编码器。
    /// </summary>
    private static bool TryConfigureTypes(IMFTransform transform, EncoderSettings settings,
        out int appliedLevel, out string error)
    {
        var isHevc = settings.Codec == VideoCodec.Hevc;
        var level = isHevc ? 0 : VideoFormatPlanner.SuggestH264Level(settings.Width, settings.Height, settings.Fps);
        appliedLevel = level;
        error = string.Empty;

        const uint eAVEncH264VProfile_High = 100;
        const uint eAVEncH265VProfile_Main = 1;
        var profileValue = isHevc ? eAVEncH265VProfile_Main : eAVEncH264VProfile_High;
        var profileName = isHevc ? "Main" : "High";
        // HEVC 的 Level 体系与 H.264 不同，不做尝试
        var levelMatrix = isHevc ? new[] { false } : new[] { true, false };
        var outputSubtype = isHevc ? HevcVideoFormat : VideoFormatGuids.H264;

        foreach (var useProfile in new[] { true, false })
        foreach (var useLevel in levelMatrix)
        {
            var outType = MediaFactory.MFCreateMediaType();
            try
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outType.Set(MediaTypeAttributeKeys.Subtype, outputSubtype);
                outType.Set(MediaTypeAttributeKeys.FrameSize, Pack2(settings.Width, settings.Height));
                outType.Set(MediaTypeAttributeKeys.FrameRate, Pack2(settings.Fps, 1));
                outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // Progressive
                outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)settings.BitrateBps);
                if (useLevel) outType.Set(MediaTypeAttributeKeys.Mpeg2Level, (uint)level);
                if (useProfile) outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, profileValue);
                transform.SetOutputType(0, outType, 0);
            }
            catch (SharpGenException ex)
            {
                error = $"SetOutputType(profile={(useProfile ? profileName : "默认")}, " +
                        $"level={(useLevel ? VideoFormatPlanner.H264LevelName(level) : "未设置")}) " +
                        $"0x{ex.HResult:X8}";
                continue;
            }
            finally
            {
                outType.Dispose();
            }

            var inType = MediaFactory.MFCreateMediaType();
            try
            {
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                inType.Set(MediaTypeAttributeKeys.FrameSize, Pack2(settings.Width, settings.Height));
                inType.Set(MediaTypeAttributeKeys.FrameRate, Pack2(settings.Fps, 1));
                inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                inType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
                transform.SetInputType(0, inType, 0);
                appliedLevel = useLevel ? level : 0;
                if (useProfile)
                    Logging.Logger.Info("MF",
                        $"{settings.Codec.DisplayName()} 编码启用 {profileName} Profile");
                return true;
            }
            catch (SharpGenException ex)
            {
                error = $"SetInputType 0x{ex.HResult:X8}";
            }
            finally
            {
                inType.Dispose();
            }
        }

        appliedLevel = level;
        return false;
    }

    /// <summary>尽力而为地设置编码器属性（首次下发入口，诊断日志见 <see cref="ApplyCodecApi"/>）</summary>
    private void ConfigureCodecApi() => ApplyCodecApi("类型配置后");

    /// <summary>
    /// 尽力而为地设置编码器属性（速率控制 / 低延迟 / GOP / 场景），并把每个 HRESULT 写进日志。
    /// 注意：并非所有编码器都实现 ICodecAPI —— 实测 Win11 的 "Microsoft AVC DX12 Encoder"
    /// 在流启动前对下列属性一律返回 E_NOTIMPL（这是桌面内容欠产出、画质偏糊的根因：
    /// 媒体类型上的 MF_MT_AVG_BITRATE 只是被接受，并不构成硬约束）。
    /// 速率控制族按 CBR → 质量模式(3) 的顺序尝试到首个成功；质量模式会锁定码率
    /// （运行中 SetBitrate 失效），拥塞降档只能靠分辨率阶梯，日志里会注明。
    /// </summary>
    private void ApplyCodecApi(string phase)
    {
        var p = _transform.NativePointer;
        var results = new List<string>(8);

        // ===== 速率控制族（互斥：按优先级尝试到首个成功）=====
        var rcHr = CodecApi.SetUint32(p, CodecApi.AvEncCommonRateControlMode, 0, out var rcDetail); // CBR
        if (rcHr >= 0)
        {
            results.Add($"RateControlMode=CBR[{rcDetail}]");
            var brHr = CodecApi.SetUint32(p, CodecApi.AvEncCommonMeanBitRate,
                (uint)_settings.BitrateBps, out var brDetail);
            results.Add($"MeanBitRate={(brHr >= 0 ? $"OK[{brDetail}]" : CodecApi.Hr(brHr))}");
            _rateControlApplied = true;
        }
        else
        {
            var avHr = CodecApi.IsAvailable(p, CodecApi.AvEncCommonRateControlMode);
            results.Add($"RateControlMode=CBR:{CodecApi.Hr(rcHr)}(IsAvailable={CodecApi.Hr(avHr)})");
            var qHr = CodecApi.SetUint32(p, CodecApi.AvEncCommonRateControlMode, 3, out _); // Quality
            if (qHr >= 0)
            {
                var qvHr = CodecApi.SetUint32(p, CodecApi.AvEncCommonQuality, 95, out var qvDetail);
                results.Add(qvHr >= 0
                    ? $"RateControlMode=Quality95[{qvDetail}]（运行中改码率将失效，拥塞降档走分辨率）"
                    : $"RateControlMode=Quality但Quality95={CodecApi.Hr(qvHr)}");
                _rateControlApplied = true;
            }
            else
            {
                results.Add($"RateControlMode=Quality:{CodecApi.Hr(qHr)}");
            }
        }

        // ===== 与速率控制无关的通用属性 =====
        var llHr = CodecApi.SetUint32(p, CodecApi.AvLowLatencyMode, 1, out var llDetail);
        results.Add($"LowLatency={(llHr >= 0 ? $"OK[{llDetail}]" : CodecApi.Hr(llHr))}");
        if (_settings.GopSize > 0)
        {
            var gHr = CodecApi.SetUint32(p, CodecApi.AvEncMPVGopSize, (uint)_settings.GopSize, out var gDetail);
            results.Add($"GOPSize={(gHr >= 0 ? $"OK[{gDetail}]" : CodecApi.Hr(gHr))}");
        }
        var scHr = CodecApi.SetUint32(p, CodecApi.AvScenarioInfo, 1, out var scDetail);
        results.Add($"Scenario=DisplayRemoting[{(scHr >= 0 ? scDetail : CodecApi.Hr(scHr))}]");

        Logging.Logger.Info("CodecAPI",
            $"属性下发[{phase} / {EncoderName}]: {string.Join(", ", results)}");
    }

    // ===== 输入 API =====

    /// <summary>编码一帧 NV12 GPU 纹理（零拷贝）</summary>
    public void EncodeNv12Texture(ID3D11Texture2D nv12Texture, long timestampUtc)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MfVideoEncoder));
        if (!IsD3DAccelerated)
            throw new InvalidOperationException("未启用 D3D 路径，请使用 EncodeNv12Bytes");

        var sample = CreateTextureSample(nv12Texture, timestampUtc);
        SubmitSample(sample);
    }

    /// <summary>编码一帧 NV12 系统内存数据（stride=width）</summary>
    public void EncodeNv12Bytes(byte[] nv12, long timestampUtc)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MfVideoEncoder));

        var sample = CreateMemorySample(nv12, timestampUtc);
        SubmitSample(sample);
    }

    private void SubmitSample(IMFSample sample)
    {
        if (IsAsyncMode)
        {
            // 异步模式：等待 NeedInput 槽位（由事件泵计数），在调用线程上提交。
            // 最多等 500ms；超时说明编码器消费不及时，丢帧以维持实时性。
            if (!_inputSlots.Wait(500))
            {
                sample.Dispose();
                Logging.Logger.Debug("MF", "等待 NeedInput 超时，丢弃一帧");
                return;
            }
            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            finally
            {
                sample.Dispose();
            }
        }
        else
        {
            lock (_encodeGate)
            {
                try
                {
                    _transform.ProcessInput(0, sample, 0);
                }
                finally
                {
                    sample.Dispose();
                }
                DrainOutputsSync();
            }
        }
    }

    private IMFSample CreateTextureSample(ID3D11Texture2D texture, long timestampUtc)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(Texture2dIid, texture, 0, false);
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = timestampUtc;
        sample.SampleDuration = 10_000_000L / Math.Max(1, _settings.Fps);
        return sample;
    }

    private IMFSample CreateMemorySample(byte[] nv12, long timestampUtc)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        buffer.Lock(out var data, out _, out _);
        Marshal.Copy(nv12, 0, data, nv12.Length);
        buffer.Unlock();
        buffer.CurrentLength = nv12.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = timestampUtc;
        sample.SampleDuration = 10_000_000L / Math.Max(1, _settings.Fps);
        return sample;
    }

    // ===== 异步事件泵 =====

    /// <summary>异步 MFT 事件泵：NeedInput → 释放槽位；HaveOutput → 取输出</summary>
    private void PumpLoop()
    {
        var eg = _asyncEventGenerator!;
        while (_pumpRunning && !_disposed)
        {
            IMFMediaEvent? evt = null;
            try
            {
                evt = eg.GetEvent(1); // MF_EVENT_FLAG_NO_WAIT：轮询模式，保证可响应退出
                if (evt == null)
                {
                    Thread.Sleep(2);
                    continue;
                }
            }
            catch (SharpGenException ex) when ((uint)ex.HResult == 0xC00D3E80) // MF_E_NO_EVENTS_AVAILABLE
            {
                Thread.Sleep(2);
                continue;
            }
            catch (Exception ex)
            {
                if (_pumpRunning && !_disposed)
                    Logging.Logger.Error("MF", "事件泵异常", ex);
                Thread.Sleep(10);
                continue;
            }

            using (evt)
            {
                var type = (int)evt.EventType;
                try
                {
                    switch (type)
                    {
                        case MeTransformNeedInput:
                            _inputSlots.Release();
                            break;

                        case MeTransformHaveOutput:
                            ProcessOutputOnce();
                            break;

                        case MeTransformDrainComplete:
                            break;
                    }
                }
                catch (SharpGenException ex) when (ex.HResult == MfETransformStreamChange)
                {
                    Logging.Logger.Info("MF", "输出流格式变化（异步）");
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error("MF", $"事件处理异常 (type={type})", ex);
                }
            }
        }
    }

    /// <summary>执行一次 ProcessOutput 并派发编码结果</summary>
    private void ProcessOutputOnce()
    {
        var outBuf = new OutputDataBuffer { StreamID = 0 };
        try
        {
            if (!_providesOutputSamples)
            {
                // 调用方预分配输出样本（多数同步/部分异步 MFT 要求）
                var info = SafeGetOutputStreamInfo();
                var size = info > 0 ? info : 4 * 1024 * 1024;
                var outSample = MediaFactory.MFCreateSample();
                var outBuffer = MediaFactory.MFCreateMemoryBuffer(size);
                outSample.AddBuffer(outBuffer);
                outBuffer.Dispose();
                outBuf.Sample = outSample;
            }

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out _);
            if (result.Failure)
            {
                if (result.Code == MfETransformNeedMoreInput || result.Code == MfETransformStreamChange)
                    return;
                result.CheckError();
                return;
            }

            var sample = outBuf.Sample;
            if (sample == null) return;
            try
            {
                EmitSample(sample);
            }
            finally
            {
                sample.Dispose();
            }
        }
        catch (SharpGenException ex) when (ex.HResult == MfETransformNeedMoreInput ||
                                            ex.HResult == MfETransformStreamChange)
        {
            outBuf.Sample?.Dispose();
        }
        catch (Exception)
        {
            outBuf.Sample?.Dispose();
            throw;
        }
    }

    private int SafeGetOutputStreamInfo()
    {
        try { return _transform.GetOutputStreamInfo(0).Size; }
        catch { return 0; }
    }

    /// <summary>把输出样本转换为 EncodedVideoFrame 并回调</summary>
    private void EmitSample(IMFSample outSample)
    {
        using var buffer = outSample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var length);
        try
        {
            if (length <= 0) return;
            var data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);

            var frame = new EncodedVideoFrame
            {
                Data = data,
                Keyframe = AnnexB.IsKeyframe(data, _settings.Codec),
                TimestampUtc = outSample.SampleTime != 0 ? outSample.SampleTime : DateTime.UtcNow.Ticks,
                Width = _settings.Width,
                Height = _settings.Height,
            };
            Encoded?.Invoke(frame);
        }
        finally
        {
            try { buffer.Unlock(); } catch { }
        }
    }

    // ===== 同步模式抽干 =====

    private void DrainOutputsSync()
    {
        while (true)
        {
            var outBuf = new OutputDataBuffer { StreamID = 0 };
            Result result;
            try
            {
                if (!_providesOutputSamples)
                {
                    var size = SafeGetOutputStreamInfo();
                    var outSample = MediaFactory.MFCreateSample();
                    var outBuffer = MediaFactory.MFCreateMemoryBuffer(size > 0 ? size : 1024 * 1024);
                    outSample.AddBuffer(outBuffer);
                    outBuffer.Dispose();
                    outBuf.Sample = outSample;
                }
                result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out _);
            }
            catch (SharpGenException ex) when (ex.HResult == MfETransformNeedMoreInput ||
                                                ex.HResult == MfETransformStreamChange)
            {
                outBuf.Sample?.Dispose();
                if (ex.HResult == MfETransformStreamChange)
                    continue;
                return;
            }
            catch (Exception)
            {
                outBuf.Sample?.Dispose();
                throw;
            }

            if (result.Failure)
            {
                outBuf.Sample?.Dispose();
                if (result.Code == MfETransformNeedMoreInput) return;
                if (result.Code == MfETransformStreamChange) continue;
                result.CheckError();
                return;
            }

            var sample = outBuf.Sample;
            if (sample == null) return;
            try { EmitSample(sample); }
            finally { sample.Dispose(); }
        }
    }

    // ===== 控制 =====

    /// <summary>请求下一帧为关键帧</summary>
    public bool ForceKeyFrame() =>
        CodecApi.TrySetUint32(_transform.NativePointer, CodecApi.AvEncVideoForceKeyFrame, 1);

    /// <summary>运行中调整码率</summary>
    public bool SetBitrate(int bitrateBps) =>
        CodecApi.TrySetUint32(_transform.NativePointer, CodecApi.AvEncCommonMeanBitRate, (uint)bitrateBps);

    /// <summary>
    /// 打包宽高到 UINT64（MF_MT_FRAME_SIZE 格式：高 32 位=宽，低 32 位=高，与 MFSetAttributeSize 一致）。
    /// </summary>
    private static ulong Pack2(int width, int height) =>
        ((ulong)(uint)width << 32) | (uint)height;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsAsyncMode)
        {
            _pumpRunning = false;
            try
            {
                // 唤醒轮询线程
                _asyncEventGenerator?.QueueEvent(0, Guid.Empty, Result.Ok, null);
            }
            catch { }
            _pumpThread?.Join(1500);
            _inputSlots.Dispose();
        }

        lock (_encodeGate)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
            }
            catch { }
            _d3dManager?.Dispose();
            _transform.Dispose();
        }
        MfShutdownRef();
        Logging.Logger.Info("MF", "编码器已释放");
    }
}
