using System.Diagnostics;
using OpenCvSharp;

namespace FishingVisionAssistant.Core;

/// <summary>
/// Ищет вытянутую сине-фиолетовую рамку по HSV-маске и нормализует её через perspective transform.
/// </summary>
public sealed class PanelDetector : IPanelDetector
{
    private readonly PanelDetectorOptions _options;

    public PanelDetector(PanelDetectorOptions? options = null)
    {
        _options = options ?? new PanelDetectorOptions();
        ValidateOptions(_options);
    }

    /// <inheritdoc />
    public PanelDetectionResult Detect(ReadOnlyMemory<byte> encodedImage)
    {
        ArgumentOutOfRangeException.ThrowIfZero(encodedImage.Length);

        var stopwatch = Stopwatch.StartNew();
        using var source = Cv2.ImDecode(encodedImage.ToArray(), ImreadModes.Color);
        if (source.Empty())
        {
            throw new ArgumentException("Изображение не удалось декодировать.", nameof(encodedImage));
        }

        using var overlay = source.Clone();
        using var mask = CreateColorMask(source);
        var candidate = FindBestCandidate(mask, source.Size());

        if (candidate is null)
        {
            stopwatch.Stop();
            Cv2.PutText(
                overlay,
                "Panel not found",
                new Point(20, 40),
                HersheyFonts.HersheySimplex,
                1,
                Scalar.OrangeRed,
                2,
                LineTypes.AntiAlias);

            return new PanelDetectionResult(
                false,
                0,
                "Подходящая сине-фиолетовая вертикальная рамка не найдена.",
                Array.Empty<ImagePoint>(),
                EncodePng(overlay),
                EncodePng(mask),
                null,
                stopwatch.Elapsed);
        }

        var corners = OrderCorners(candidate.Rectangle.Points());
        DrawCandidate(overlay, corners, candidate.Confidence);
        using var rectified = Rectify(source, corners);
        stopwatch.Stop();

        return new PanelDetectionResult(
            true,
            candidate.Confidence,
            $"Найдена вертикальная рамка с aspect ratio {candidate.AspectRatio:F1}.",
            corners.Select(point => new ImagePoint(point.X, point.Y)).ToArray(),
            EncodePng(overlay),
            EncodePng(mask),
            EncodePng(rectified),
            stopwatch.Elapsed);
    }

    private Mat CreateColorMask(Mat source)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);

        var mask = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(_options.MinimumHue, _options.MinimumSaturation, _options.MinimumValue),
            new Scalar(_options.MaximumHue, 255, 255),
            mask);

        // Вертикальное открытие удаляет текст и значок рыбы, сохраняя длинные стороны рамки.
        var openKernelHeight = EnsureOdd(Math.Max(11, source.Rows / 50));
        var closeKernelHeight = EnsureOdd(Math.Max(31, source.Rows / 6));
        var mergeKernelWidth = EnsureOdd(Math.Max(9, source.Rows / 35));
        using var openKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, openKernelHeight));
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, closeKernelHeight));
        using var mergeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(mergeKernelWidth, 3));
        using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, openKernel);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, mergeKernel);
        Cv2.Dilate(mask, mask, dilateKernel, iterations: 1);
        return mask;
    }

    private Candidate? FindBestCandidate(Mat mask, Size frameSize)
    {
        Cv2.FindContours(
            mask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        Candidate? best = null;
        foreach (var contour in contours)
        {
            if (contour.Length < 4)
            {
                continue;
            }

            var rectangle = Cv2.MinAreaRect(contour);
            var longSide = Math.Max(rectangle.Size.Width, rectangle.Size.Height);
            var shortSide = Math.Min(rectangle.Size.Width, rectangle.Size.Height);
            if (shortSide <= 0)
            {
                continue;
            }

            var heightRatio = longSide / frameSize.Height;
            var aspectRatio = longSide / shortSide;
            if (heightRatio < _options.MinimumHeightRatio ||
                aspectRatio < _options.MinimumAspectRatio ||
                aspectRatio > _options.MaximumAspectRatio)
            {
                continue;
            }

            var ordered = OrderCorners(rectangle.Points());
            var longAxis = ordered[3] - ordered[0];
            var verticality = Math.Abs(longAxis.Y) / Math.Max(longAxis.DistanceTo(new Point2f(0, 0)), 0.001);
            if (verticality < 0.45)
            {
                continue;
            }

            var aspectScore = 1 - Math.Min(Math.Abs(aspectRatio - 14) / 14, 1);
            var heightScore = Math.Min(heightRatio / 0.65, 1);
            var confidence = Math.Clamp(0.35 + 0.35 * heightScore + 0.2 * aspectScore + 0.1 * verticality, 0, 1);
            var candidate = new Candidate(rectangle, aspectRatio, confidence, longSide * shortSide);

            if (best is null || candidate.Rank > best.Rank)
            {
                best = candidate;
            }
        }

        return best;
    }

    private Mat Rectify(Mat source, IReadOnlyList<Point2f> corners)
    {
        var target = new[]
        {
            new Point2f(0, 0),
            new Point2f(_options.NormalizedWidth - 1, 0),
            new Point2f(_options.NormalizedWidth - 1, _options.NormalizedHeight - 1),
            new Point2f(0, _options.NormalizedHeight - 1)
        };

        using var transform = Cv2.GetPerspectiveTransform(corners, target);
        var result = new Mat();
        Cv2.WarpPerspective(
            source,
            result,
            transform,
            new Size(_options.NormalizedWidth, _options.NormalizedHeight),
            InterpolationFlags.Linear,
            BorderTypes.Replicate);
        return result;
    }

    private static Point2f[] OrderCorners(IReadOnlyCollection<Point2f> points)
    {
        var top = points.OrderBy(point => point.Y).Take(2).OrderBy(point => point.X).ToArray();
        var bottom = points.OrderByDescending(point => point.Y).Take(2).OrderBy(point => point.X).ToArray();
        return [top[0], top[1], bottom[1], bottom[0]];
    }

    private static void DrawCandidate(Mat overlay, IReadOnlyList<Point2f> corners, double confidence)
    {
        var contour = corners.Select(point => new Point((int)point.X, (int)point.Y)).ToArray();
        Cv2.Polylines(overlay, [contour], true, new Scalar(57, 217, 138), 3, LineTypes.AntiAlias);
        var labelPoint = new Point(Math.Max(contour.Min(point => point.X), 8), Math.Max(contour.Min(point => point.Y) - 12, 24));
        Cv2.PutText(
            overlay,
            $"Panel {confidence:P0}",
            labelPoint,
            HersheyFonts.HersheySimplex,
            0.7,
            new Scalar(57, 217, 138),
            2,
            LineTypes.AntiAlias);
    }

    private static byte[] EncodePng(Mat image)
    {
        Cv2.ImEncode(".png", image, out var encoded);
        return encoded;
    }

    private static int EnsureOdd(int value) => value % 2 == 0 ? value + 1 : value;

    private static void ValidateOptions(PanelDetectorOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumHue, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumHue, 179);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(options.MinimumHue, options.MaximumHue);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MinimumHeightRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MinimumHeightRatio, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MinimumAspectRatio, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumAspectRatio, options.MinimumAspectRatio);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NormalizedWidth, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NormalizedHeight, 16);
    }

    private sealed record Candidate(RotatedRect Rectangle, double AspectRatio, double Confidence, double Area)
    {
        public double Rank => Area * Confidence;
    }
}
