using OpenCvSharp;

namespace FishingVisionAssistant.Core;

/// <summary>
/// Выпрямляет размеченный OBB из чистого кадра для визуальной проверки четырёх углов.
/// </summary>
public static class ObbPreviewRenderer
{
    private const int MaximumSide = 640;

    public static byte[]? Render(ReadOnlySpan<byte> encodedFrame, IReadOnlyList<ImagePoint> corners)
    {
        if (encodedFrame.IsEmpty || corners.Count != 4)
        {
            return null;
        }

        using var source = Cv2.ImDecode(encodedFrame.ToArray(), ImreadModes.Color);
        if (source.Empty())
        {
            return null;
        }

        return RenderSource(source, corners);
    }

    /// <summary>
    /// Строит preview OBB напрямую из BGR24 buffer текущего кадра без PNG-кодирования и повторного декодирования видео.
    /// </summary>
    public static byte[]? RenderBgr24(
        byte[] bgr24Pixels,
        int width,
        int height,
        int stride,
        IReadOnlyList<ImagePoint> corners)
    {
        ArgumentNullException.ThrowIfNull(bgr24Pixels);
        ArgumentNullException.ThrowIfNull(corners);
        if (width <= 0 || height <= 0 || stride < checked(width * 3) ||
            bgr24Pixels.Length < checked(stride * height))
        {
            return null;
        }

        using var source = Mat.FromPixelData(
            height,
            width,
            MatType.CV_8UC3,
            bgr24Pixels,
            stride);
        return RenderSource(source, corners);
    }

    private static byte[]? RenderSource(Mat source, IReadOnlyList<ImagePoint> corners)
    {
        var ordered = OrderCorners(corners, source.Width, source.Height);
        var width = (Distance(ordered[0], ordered[1]) + Distance(ordered[3], ordered[2])) / 2;
        var height = (Distance(ordered[0], ordered[3]) + Distance(ordered[1], ordered[2])) / 2;
        if (width < 2 || height < 2)
        {
            return null;
        }

        var scale = Math.Min(1, MaximumSide / Math.Max(width, height));
        var targetWidth = Math.Max(2, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(2, (int)Math.Round(height * scale));
        var target = new[]
        {
            new Point2f(0, 0),
            new Point2f(targetWidth - 1, 0),
            new Point2f(targetWidth - 1, targetHeight - 1),
            new Point2f(0, targetHeight - 1)
        };

        // Perspective transform показывает ровно тот фрагмент, который попадёт в positive OBB label.
        using var transform = Cv2.GetPerspectiveTransform(ordered, target);
        using var rectified = new Mat();
        Cv2.WarpPerspective(
            source,
            rectified,
            transform,
            new Size(targetWidth, targetHeight),
            InterpolationFlags.Linear,
            BorderTypes.Replicate);
        Cv2.ImEncode(".png", rectified, out var encodedPreview);
        return encodedPreview;
    }

    private static Point2f[] OrderCorners(IReadOnlyList<ImagePoint> corners, int width, int height)
    {
        var points = corners
            .Select(point => new Point2f(
                (float)Math.Clamp(point.X, 0, width - 1),
                (float)Math.Clamp(point.Y, 0, height - 1)))
            .ToArray();
        var top = points.OrderBy(point => point.Y).Take(2).OrderBy(point => point.X).ToArray();
        var bottom = points.OrderByDescending(point => point.Y).Take(2).OrderBy(point => point.X).ToArray();
        return [top[0], top[1], bottom[1], bottom[0]];
    }

    private static double Distance(Point2f first, Point2f second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }
}
