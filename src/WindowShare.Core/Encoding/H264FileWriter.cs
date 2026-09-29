namespace WindowShare.Core.Encoding;

/// <summary>
/// H.264 裸流文件写入器（Annex-B .h264 文件）。
/// 用途：编码验证 —— 生成的文件可直接被 ffprobe/播放器识别。
/// </summary>
public sealed class H264FileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _gate = new();
    private long _frames;
    private long _bytes;
    private bool _firstFrameSeen;
    private bool _hasParameterSets;

    public string FilePath { get; }
    public long Frames => Interlocked.Read(ref _frames);
    public long Bytes => Interlocked.Read(ref _bytes);
    public bool HasParameterSets => _hasParameterSets;

    public H264FileWriter(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        Logging.Logger.Info("H264Writer", $"开始写入: {filePath}");
    }

    /// <summary>写入一帧码流（校验首帧含参数集/IDR）</summary>
    public void Write(EncodedVideoFrame frame)
    {
        lock (_gate)
        {
            if (!_firstFrameSeen)
            {
                _firstFrameSeen = true;
                if (!AnnexB.IsValidStreamStart(frame.Data))
                    Logging.Logger.Warn("H264Writer", "首帧不是合法的 Annex-B 码流起始");
            }
            if (AnnexB.ContainsParameterSets(frame.Data))
                _hasParameterSets = true;

            _stream.Write(frame.Data, 0, frame.Data.Length);
            _stream.Flush();
            Interlocked.Increment(ref _frames);
            Interlocked.Add(ref _bytes, frame.Data.Length);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Flush();
            _stream.Dispose();
            Logging.Logger.Info("H264Writer", $"写入完成: {_frames} 帧, {_bytes} 字节, 参数集={_hasParameterSets}");
        }
    }
}
