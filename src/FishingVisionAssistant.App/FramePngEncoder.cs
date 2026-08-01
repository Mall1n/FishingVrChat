using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FishingVisionAssistant.Capture;

namespace FishingVisionAssistant.App;

/// <summary>
/// Преобразует исходные video frame и изображения в PNG без diagnostic overlay.
/// </summary>
public static class FramePngEncoder
{
    public static byte[] Encode(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bitmap = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            frame.Bgr24Pixels,
            frame.Stride);
        bitmap.Freeze();
        return Encode(bitmap);
    }

    public static byte[] NormalizeEncodedImage(byte[] encodedImage)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);
        using var input = new MemoryStream(encodedImage, writable: false);
        var decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        return Encode(decoder.Frames[0]);
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
