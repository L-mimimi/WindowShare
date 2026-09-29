namespace WindowShare.Core.Capture;

/// <summary>
/// 捕获引擎工厂：按可用性自动降级 WGC → DXGI（仅整屏）→ GDI。
/// 隐私策略：窗口捕获绝不允许"截屏窗口所在整个显示器"的引擎来近似实现，
/// 否则会把窗口之外的桌面内容发出去——窗口源只有 WGC / GDI(PrintWindow) 两种实现。
/// </summary>
public static class CaptureEngineFactory
{
    /// <summary>创建引擎，返回引擎与说明（说明用于 UI 提示回退原因）</summary>
    public static (ICaptureEngine Engine, string Note) Create(CaptureSource source)
    {
        if (GraphicsCaptureEngine.IsAvailable())
            return (new GraphicsCaptureEngine(), string.Empty);

        if (source.Kind == CaptureSourceKind.Monitor && DxgiDuplicationEngine.IsAvailable())
            return (new DxgiDuplicationEngine(),
                "系统不支持 Windows Graphics Capture，已回退到 DXGI Desktop Duplication（仅整屏）");

        return (new GdiCaptureEngine(),
            "已回退到 GDI 捕获（兼容模式，性能与帧率较低）");
    }

    /// <summary>探测各引擎可用性（诊断面板用）</summary>
    public static List<(string Name, bool Available)> Probe()
    {
        var list = new List<(string, bool)>
        {
            ("Windows Graphics Capture", GraphicsCaptureEngine.IsAvailable()),
            ("DXGI Desktop Duplication", DxgiDuplicationEngine.IsAvailable()),
            ("GDI", GdiCaptureEngine.IsAvailable()),
        };
        return list;
    }
}
