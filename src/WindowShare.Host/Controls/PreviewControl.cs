using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowShare.Host;

/// <summary>
/// 预览控件：WriteableBitmap 直接绘制 BGRA 帧（WritePixels，零拷贝进位图后备缓冲）。
/// </summary>
public class PreviewControl : Border
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private WriteableBitmap? _bitmap;

    public PreviewControl()
    {
        Child = _image;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
        _image.Stretch = Stretch.Uniform;
    }

    /// <summary>更新一帧 BGRA（须在 UI 线程调用）</summary>
    public void UpdateFrame(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return;
        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
    }
}
