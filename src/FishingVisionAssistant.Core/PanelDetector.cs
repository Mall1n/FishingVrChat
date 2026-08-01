using System.Diagnostics;
using OpenCvSharp;

namespace FishingVisionAssistant.Core;

/// <summary>
/// Строит настраиваемую HSV-маску, ищет вытянутую рамку и нормализует её через perspective transform.
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

        return Detect(source, stopwatch);
    }

    /// <inheritdoc />
    public PanelDetectionResult DetectBgr24(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, checked(width * 3));
        if (pixels.Length < checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer меньше заявленной геометрии кадра.", nameof(pixels));
        }

        var stopwatch = Stopwatch.StartNew();
        using var source = Mat.FromPixelData(height, width, MatType.CV_8UC3, pixels, stride);
        return Detect(source, stopwatch);
    }

    private PanelDetectionResult Detect(Mat source, Stopwatch stopwatch)
    {
        using var overlay = source.Clone();
        using var mask = CreateColorMask(source);
        var whiteCandidate = FindBestWhiteAnchorCandidate(source, out var whiteDiagnostic);
        var candidate = whiteCandidate ?? FindBestCandidate(mask, source.Size());

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
                $"{whiteDiagnostic} Подходящая вертикальная рамка в текущей HSV-маске не найдена.",
                Array.Empty<ImagePoint>(),
                EncodePng(overlay),
                EncodePng(mask),
                null,
                stopwatch.Elapsed);
        }

        var corners = OrderCorners(candidate.Rectangle.Points());
        DrawCandidate(overlay, corners, candidate.Confidence, candidate.Kind);
        using var rectified = Rectify(source, corners);
        stopwatch.Stop();

        return new PanelDetectionResult(
            true,
            candidate.Confidence,
            candidate.Kind switch
            {
                CandidateKind.WhiteAnchor =>
                    $"Найдена рамка по белой зоне и локальным границам с aspect ratio " +
                    $"{candidate.AspectRatio:F1}. {candidate.Detail}",
                CandidateKind.HsvSegmentPair =>
                    $"Найдена рамка по двум соосным HSV-сегментам с aspect ratio {candidate.AspectRatio:F1}.",
                _ => $"Найдена рамка с aspect ratio {candidate.AspectRatio:F1} в текущей HSV-маске."
            },
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

        // Вертикальное открытие удаляет мелкий шум, а закрытие соединяет разрывы сторон рамки.
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

    private Candidate? FindBestWhiteAnchorCandidate(Mat source, out string diagnostic)
    {
        using var hsv = new Mat();
        using var gray = new Mat();
        using var edges = new Mat();
        using var whiteMask = new Mat();
        Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Canny(gray, edges, 45, 135);

        var channels = Cv2.Split(hsv);
        try
        {
            Cv2.MeanStdDev(channels[2], out var valueMean, out var valueDeviation);
            var minimumWhiteValue = Math.Clamp(
                (int)Math.Round(valueMean.Val0 + 1.35 * valueDeviation.Val0),
                135,
                225);
            Cv2.InRange(
                hsv,
                new Scalar(0, 0, minimumWhiteValue),
                new Scalar(179, 110, 255),
                whiteMask);
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }

        // Белая зона может быть разорвана значком рыбы, поэтому склеиваем только небольшие вертикальные разрывы.
        var openKernelHeight = EnsureOdd(Math.Max(7, source.Rows / 120));
        var closeKernelHeight = EnsureOdd(Math.Max(19, source.Rows / 34));
        using var openKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, openKernelHeight));
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, closeKernelHeight));
        Cv2.MorphologyEx(whiteMask, whiteMask, MorphTypes.Open, openKernel);
        Cv2.MorphologyEx(whiteMask, whiteMask, MorphTypes.Close, closeKernel);

        Cv2.FindContours(
            whiteMask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var anchors = contours
            .Select(contour => CreateWhiteAnchor(contour, source.Size()))
            .Where(anchor => anchor is not null)
            .Cast<WhiteAnchor>()
            .ToList();
        AddPairedWhiteAnchors(anchors, source.Size());

        Candidate? best = null;
        var structureDiagnostics = new List<string>();
        foreach (var anchor in anchors)
        {
            var candidate = BuildCandidateFromWhiteAnchor(
                anchor,
                source,
                edges,
                source.Size(),
                out var structureDiagnostic);
            structureDiagnostics.Add(structureDiagnostic);
            if (candidate is not null && (best is null || candidate.Rank > best.Rank))
            {
                best = candidate;
            }
        }

        diagnostic = anchors.Count switch
        {
            0 => "White-anchor: подходящие белые сегменты не найдены.",
            _ when best is null =>
                $"White-anchor: найдено сегментов {anchors.Count}, локальные границы не подтверждены " +
                $"({string.Join("; ", structureDiagnostics)}).",
            _ => $"White-anchor: локальная структура подтверждена для {anchors.Count} кандидатов."
        };
        return best;
    }

    private static WhiteAnchor? CreateWhiteAnchor(Point[] contour, Size frameSize)
    {
        if (contour.Length < 4)
        {
            return null;
        }

        var rectangle = Cv2.MinAreaRect(contour);
        var longSide = Math.Max(rectangle.Size.Width, rectangle.Size.Height);
        var shortSide = Math.Min(rectangle.Size.Width, rectangle.Size.Height);
        if (shortSide <= 0)
        {
            return null;
        }

        var heightRatio = longSide / frameSize.Height;
        var aspectRatio = longSide / shortSide;
        var longAxis = NormalizeDownward(GetLongAxis(rectangle));
        var verticality = Math.Abs(longAxis.Y);
        var whiteArea = Cv2.ContourArea(contour);
        var rectangularity = Math.Clamp(whiteArea / Math.Max(longSide * shortSide, 1), 0, 1);
        if (heightRatio < 0.025 ||
            heightRatio > 0.28 ||
            aspectRatio < 1.8 ||
            aspectRatio > 14 ||
            verticality < 0.45 ||
            rectangularity < 0.28)
        {
            return null;
        }

        var shapeScore = Math.Clamp(
            0.35 * Math.Min(aspectRatio / 5, 1) +
            0.35 * rectangularity +
            0.3 * verticality,
            0,
            1);
        return new WhiteAnchor(
            rectangle,
            longSide,
            shortSide,
            longAxis,
            whiteArea,
            rectangularity,
            shapeScore);
    }

    private static void AddPairedWhiteAnchors(List<WhiteAnchor> anchors, Size frameSize)
    {
        var originalAnchors = anchors.ToArray();
        for (var firstIndex = 0; firstIndex < originalAnchors.Length - 1; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < originalAnchors.Length; secondIndex++)
            {
                var first = originalAnchors[firstIndex];
                var second = originalAnchors[secondIndex];
                var delta = second.Rectangle.Center - first.Rectangle.Center;
                var distance = delta.DistanceTo(new Point2f(0, 0));
                if (distance <= 0)
                {
                    continue;
                }

                var pairAxis = new Point2f((float)(delta.X / distance), (float)(delta.Y / distance));
                var alignment = Math.Min(
                    Math.Abs(Dot(first.LongAxis, pairAxis)),
                    Math.Abs(Dot(second.LongAxis, pairAxis)));
                var perpendicularDistance = Math.Abs(Dot(delta, new Point2f(-pairAxis.Y, pairAxis.X)));
                var estimatedGap = Math.Max(distance - (first.LongSide + second.LongSide) / 2, 0);
                if (Math.Abs(pairAxis.Y) < 0.72 ||
                    alignment < 0.72 ||
                    perpendicularDistance > Math.Max(first.ShortSide, second.ShortSide) * 1.6 ||
                    estimatedGap > frameSize.Height * 0.1)
                {
                    continue;
                }

                var points = first.Rectangle.Points().Concat(second.Rectangle.Points()).ToArray();
                var rectangle = Cv2.MinAreaRect(points);
                var longSide = Math.Max(rectangle.Size.Width, rectangle.Size.Height);
                var shortSide = Math.Min(rectangle.Size.Width, rectangle.Size.Height);
                if (shortSide <= 0 || longSide / frameSize.Height > 0.32)
                {
                    continue;
                }

                var whiteArea = first.WhiteArea + second.WhiteArea;
                var fillRatio = Math.Clamp(whiteArea / Math.Max(longSide * shortSide, 1), 0, 1);
                if (fillRatio < 0.38)
                {
                    continue;
                }

                anchors.Add(new WhiteAnchor(
                    rectangle,
                    longSide,
                    shortSide,
                    NormalizeDownward(GetLongAxis(rectangle)),
                    whiteArea,
                    fillRatio,
                    Math.Min((first.ShapeScore + second.ShapeScore + fillRatio) / 3 + 0.08, 1)));
            }
        }
    }

    private Candidate? BuildCandidateFromWhiteAnchor(
        WhiteAnchor anchor,
        Mat source,
        Mat edges,
        Size frameSize,
        out string diagnostic)
    {
        var axis = anchor.LongAxis;
        var perpendicular = new Point2f(-axis.Y, axis.X);
        var searchHeight = Math.Clamp(
            Math.Max(anchor.LongSide * 4, frameSize.Height * 0.28),
            frameSize.Height * 0.28,
            frameSize.Height * 0.68);
        var searchHalfWidth = Math.Max(anchor.ShortSide * 2.6, searchHeight / 14 * 1.8);
        var searchRectangle = CreateAlignedRectangle(
            anchor.Rectangle,
            anchor.Rectangle.Center,
            searchHeight,
            searchHalfWidth * 2);
        var bounds = ClampBounds(Cv2.BoundingRect(searchRectangle.Points()), frameSize);
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            diagnostic = $"anchor {anchor.LongSide:F0}x{anchor.ShortSide:F0}: пустой ROI";
            return null;
        }

        using var edgeRegion = new Mat(edges, bounds);
        using var regionMask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.Black);
        var localPolygon = searchRectangle.Points()
            .Select(point => new Point(
                (int)Math.Round(point.X - bounds.X),
                (int)Math.Round(point.Y - bounds.Y)))
            .ToArray();
        Cv2.FillConvexPoly(regionMask, localPolygon, Scalar.White);
        using var localEdges = new Mat();
        Cv2.BitwiseAnd(edgeRegion, regionMask, localEdges);
        var edgePixelCount = Cv2.CountNonZero(localEdges);

        var lines = Cv2.HoughLinesP(
            localEdges,
            1,
            Math.PI / 180,
            threshold: Math.Max(6, bounds.Height / 80),
            minLineLength: Math.Max(anchor.LongSide * 0.12, frameSize.Height * 0.012),
            maxLineGap: Math.Max(10, frameSize.Height * 0.04));

        var minimumProjection = -anchor.LongSide / 2;
        var maximumProjection = anchor.LongSide / 2;
        var leftEvidence = 0d;
        var rightEvidence = 0d;
        var matchedLength = 0d;
        foreach (var line in lines)
        {
            var first = new Point2f(line.P1.X + bounds.X, line.P1.Y + bounds.Y);
            var second = new Point2f(line.P2.X + bounds.X, line.P2.Y + bounds.Y);
            var lineVector = second - first;
            var lineLength = lineVector.DistanceTo(new Point2f(0, 0));
            if (lineLength <= 0)
            {
                continue;
            }

            var normalizedLine = new Point2f(
                (float)(lineVector.X / lineLength),
                (float)(lineVector.Y / lineLength));
            var alignment = Math.Abs(Dot(normalizedLine, axis));
            var midpoint = new Point2f((first.X + second.X) / 2, (first.Y + second.Y) / 2);
            var offset = Dot(midpoint - anchor.Rectangle.Center, perpendicular);
            var absoluteOffset = Math.Abs(offset);
            if (alignment < 0.88 ||
                absoluteOffset < anchor.ShortSide * 0.22 ||
                absoluteOffset > searchHalfWidth)
            {
                continue;
            }

            minimumProjection = Math.Min(
                minimumProjection,
                Math.Min(
                    Dot(first - anchor.Rectangle.Center, axis),
                    Dot(second - anchor.Rectangle.Center, axis)));
            maximumProjection = Math.Max(
                maximumProjection,
                Math.Max(
                    Dot(first - anchor.Rectangle.Center, axis),
                    Dot(second - anchor.Rectangle.Center, axis)));
            matchedLength += lineLength * alignment;
            if (offset < 0)
            {
                leftEvidence += lineLength * alignment;
            }
            else
            {
                rightEvidence += lineLength * alignment;
            }
        }

        var span = maximumProjection - minimumProjection;
        var heightRatio = span / frameSize.Height;
        var minimumSideEvidence = Math.Min(leftEvidence, rightEvidence);
        var expectedPanelWidth = span / 14;
        var anchorHeightCoverage = anchor.LongSide / Math.Max(span, 1);
        var anchorWidthCoverage = anchor.ShortSide / Math.Max(expectedPanelWidth, 1);
        diagnostic =
            $"anchor {anchor.LongSide:F0}x{anchor.ShortSide:F0}: edges {edgePixelCount}, lines {lines.Length}, " +
            $"span {span:F0}, sides {leftEvidence:F0}/{rightEvidence:F0}, " +
            $"cover {anchorHeightCoverage:F2}/{anchorWidthCoverage:F2}, fill {anchor.FillRatio:F2}";
        if (heightRatio < _options.MinimumHeightRatio ||
            span < anchor.LongSide * 1.45 ||
            minimumSideEvidence < anchor.LongSide * 0.45 ||
            anchorHeightCoverage < 0.18 ||
            anchorHeightCoverage > 0.72 ||
            anchorWidthCoverage < 0.45 ||
            anchorWidthCoverage > 1.8 ||
            anchor.FillRatio < 0.42)
        {
            return null;
        }

        var centerShift = (minimumProjection + maximumProjection) / 2;
        var center = new Point2f(
            anchor.Rectangle.Center.X + axis.X * (float)centerShift,
            anchor.Rectangle.Center.Y + axis.Y * (float)centerShift);
        var panelWidth = Math.Max(anchor.ShortSide * 1.2, span / 14);
        var rectangle = CreateAlignedRectangle(anchor.Rectangle, center, span, panelWidth);
        var aspectRatio = span / panelWidth;
        var railColor = MeasureRailColor(source, rectangle);
        diagnostic += $", color {railColor.Left:P0}/{railColor.Right:P0}";
        if (Math.Min(railColor.Left, railColor.Right) < 0.4)
        {
            return null;
        }

        var railCoverage = Math.Clamp(matchedLength / Math.Max(span * 2, 1), 0, 1);
        var sideBalance = Math.Min(leftEvidence, rightEvidence) / Math.Max(leftEvidence, rightEvidence);
        var heightScore = Math.Min(heightRatio / 0.55, 1);
        var anchorCoverageScore = 1 - Math.Min(Math.Abs(anchorHeightCoverage - 0.36) / 0.36, 1);
        var anchorWidthScore = 1 - Math.Min(Math.Abs(anchorWidthCoverage - 0.8) / 0.8, 1);
        var confidence = Math.Clamp(
            0.26 + 0.18 * anchor.ShapeScore + 0.17 * railCoverage + 0.12 * sideBalance +
            0.08 * heightScore + 0.11 * anchorCoverageScore + 0.08 * anchorWidthScore,
            0,
            0.94);
        return new Candidate(
            rectangle,
            aspectRatio,
            confidence,
            anchor.LongSide * anchor.ShortSide * (0.6 + 0.4 * railCoverage),
            CandidateKind.WhiteAnchor,
            $"Цветные края: {railColor.Left:P0}/{railColor.Right:P0}.");
    }

    private static RailColorScore MeasureRailColor(Mat source, RotatedRect rectangle)
    {
        const int normalizedWidth = 48;
        const int normalizedHeight = 256;
        var target = new[]
        {
            new Point2f(0, 0),
            new Point2f(normalizedWidth - 1, 0),
            new Point2f(normalizedWidth - 1, normalizedHeight - 1),
            new Point2f(0, normalizedHeight - 1)
        };

        using var transform = Cv2.GetPerspectiveTransform(OrderCorners(rectangle.Points()), target);
        using var rectified = new Mat();
        using var hsv = new Mat();
        using var blueMask = new Mat();
        Cv2.WarpPerspective(
            source,
            rectified,
            transform,
            new Size(normalizedWidth, normalizedHeight),
            InterpolationFlags.Linear,
            BorderTypes.Replicate);
        Cv2.CvtColor(rectified, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(95, 50, 25), new Scalar(175, 255, 255), blueMask);

        var bandWidth = normalizedWidth / 3;
        using var leftBand = new Mat(blueMask, new Rect(0, 0, bandWidth, normalizedHeight));
        using var rightBand = new Mat(
            blueMask,
            new Rect(normalizedWidth - bandWidth, 0, bandWidth, normalizedHeight));
        var bandArea = bandWidth * normalizedHeight;
        return new RailColorScore(
            Cv2.CountNonZero(leftBand) / (double)bandArea,
            Cv2.CountNonZero(rightBand) / (double)bandArea);
    }

    private static RotatedRect CreateAlignedRectangle(
        RotatedRect orientationSource,
        Point2f center,
        double longSide,
        double shortSide)
    {
        var size = orientationSource.Size.Width >= orientationSource.Size.Height
            ? new Size2f((float)longSide, (float)shortSide)
            : new Size2f((float)shortSide, (float)longSide);
        return new RotatedRect(center, size, orientationSource.Angle);
    }

    private static Rect ClampBounds(Rect bounds, Size frameSize)
    {
        var left = Math.Clamp(bounds.Left, 0, frameSize.Width);
        var top = Math.Clamp(bounds.Top, 0, frameSize.Height);
        var right = Math.Clamp(bounds.Right, 0, frameSize.Width);
        var bottom = Math.Clamp(bounds.Bottom, 0, frameSize.Height);
        return new Rect(left, top, Math.Max(right - left, 0), Math.Max(bottom - top, 0));
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
        var segments = new List<Segment>();
        foreach (var contour in contours)
        {
            var segment = CreateSegment(contour, frameSize);
            if (segment is not null)
            {
                segments.Add(segment);
            }

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

            var rectanglePoints = rectangle.Points();
            var firstEdge = rectanglePoints[1] - rectanglePoints[0];
            var secondEdge = rectanglePoints[2] - rectanglePoints[1];
            var longAxis = firstEdge.DistanceTo(new Point2f(0, 0)) >= secondEdge.DistanceTo(new Point2f(0, 0))
                ? firstEdge
                : secondEdge;
            var verticality = Math.Abs(longAxis.Y) /
                              Math.Max(longAxis.DistanceTo(new Point2f(0, 0)), 0.001);
            if (verticality < 0.45)
            {
                continue;
            }

            var aspectScore = 1 - Math.Min(Math.Abs(aspectRatio - 14) / 14, 1);
            var heightScore = Math.Min(heightRatio / 0.65, 1);
            var confidence = Math.Clamp(
                0.35 + 0.35 * heightScore + 0.2 * aspectScore + 0.1 * verticality,
                0,
                1);
            var candidate = new Candidate(
                rectangle,
                aspectRatio,
                confidence,
                longSide * shortSide,
                CandidateKind.HsvContour,
                string.Empty);
            if (best is null || candidate.Rank > best.Rank)
            {
                best = candidate;
            }
        }

        return best ?? FindBestSegmentPair(segments, frameSize);
    }

    private Candidate? FindBestSegmentPair(IReadOnlyList<Segment> segments, Size frameSize)
    {
        Candidate? best = null;
        for (var firstIndex = 0; firstIndex < segments.Count - 1; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < segments.Count; secondIndex++)
            {
                var first = segments[firstIndex];
                var second = segments[secondIndex];
                var upper = first.Center.Y <= second.Center.Y ? first : second;
                var lower = ReferenceEquals(upper, first) ? second : first;
                var delta = lower.Center - upper.Center;
                var centerDistance = delta.DistanceTo(new Point2f(0, 0));
                if (centerDistance <= 0)
                {
                    continue;
                }

                var pairAxis = new Point2f(
                    (float)(delta.X / centerDistance),
                    (float)(delta.Y / centerDistance));
                var pairVerticality = Math.Abs(pairAxis.Y);
                var firstAlignment = Math.Abs(Dot(first.LongAxis, pairAxis));
                var secondAlignment = Math.Abs(Dot(second.LongAxis, pairAxis));
                var gap = Math.Max(lower.Top - upper.Bottom, 0);
                var span = Math.Max(first.Bottom, second.Bottom) - Math.Min(first.Top, second.Top);
                var heightRatio = span / frameSize.Height;
                var coverage = Math.Clamp((first.LongSide + second.LongSide) / Math.Max(span, 1), 0, 1);

                // Пара должна выглядеть как две части одной наклонённой стороны, а не как случайные штрихи.
                if (pairVerticality < 0.78 ||
                    firstAlignment < 0.82 ||
                    secondAlignment < 0.82 ||
                    gap > frameSize.Height * 0.34 ||
                    heightRatio < _options.MinimumHeightRatio ||
                    coverage < 0.28)
                {
                    continue;
                }

                var combinedPoints = first.Rectangle.Points().Concat(second.Rectangle.Points()).ToArray();
                var rawRectangle = Cv2.MinAreaRect(combinedPoints);
                var longSide = Math.Max(rawRectangle.Size.Width, rawRectangle.Size.Height);
                var rawShortSide = Math.Min(rawRectangle.Size.Width, rawRectangle.Size.Height);
                var shortSide = Math.Max(rawShortSide, longSide / 14);
                var aspectRatio = longSide / Math.Max(shortSide, 0.001);
                if (aspectRatio < _options.MinimumAspectRatio ||
                    aspectRatio > _options.MaximumAspectRatio)
                {
                    continue;
                }

                var rectangle = ExpandShortSide(rawRectangle, longSide, shortSide);
                var heightScore = Math.Min(heightRatio / 0.65, 1);
                var gapScore = 1 - Math.Min(gap / (frameSize.Height * 0.34), 1);
                var alignmentScore = (pairVerticality + firstAlignment + secondAlignment) / 3;
                var confidence = Math.Clamp(
                    0.32 + 0.22 * heightScore + 0.18 * coverage + 0.18 * alignmentScore + 0.1 * gapScore,
                    0,
                    0.88);
                var candidate = new Candidate(
                    rectangle,
                    aspectRatio,
                    confidence,
                    longSide * shortSide * coverage,
                    CandidateKind.HsvSegmentPair,
                    string.Empty);
                if (best is null || candidate.Rank > best.Rank)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static Segment? CreateSegment(Point[] contour, Size frameSize)
    {
        if (contour.Length < 2)
        {
            return null;
        }

        var rectangle = Cv2.MinAreaRect(contour);
        var longSide = Math.Max(rectangle.Size.Width, rectangle.Size.Height);
        var shortSide = Math.Min(rectangle.Size.Width, rectangle.Size.Height);
        if (shortSide <= 0 ||
            longSide < frameSize.Height * 0.025 ||
            longSide / shortSide < 3 ||
            shortSide > frameSize.Height * 0.06)
        {
            return null;
        }

        var longAxis = GetLongAxis(rectangle);
        var axisLength = longAxis.DistanceTo(new Point2f(0, 0));
        if (axisLength <= 0)
        {
            return null;
        }

        var normalizedAxis = new Point2f(
            (float)(longAxis.X / axisLength),
            (float)(longAxis.Y / axisLength));
        if (normalizedAxis.Y < 0)
        {
            normalizedAxis = new Point2f(-normalizedAxis.X, -normalizedAxis.Y);
        }

        if (Math.Abs(normalizedAxis.Y) < 0.55)
        {
            return null;
        }

        var points = rectangle.Points();
        return new Segment(
            rectangle,
            rectangle.Center,
            longSide,
            normalizedAxis,
            points.Min(point => point.Y),
            points.Max(point => point.Y));
    }

    private static Point2f GetLongAxis(RotatedRect rectangle)
    {
        var points = rectangle.Points();
        var firstEdge = points[1] - points[0];
        var secondEdge = points[2] - points[1];
        return firstEdge.DistanceTo(new Point2f(0, 0)) >= secondEdge.DistanceTo(new Point2f(0, 0))
            ? firstEdge
            : secondEdge;
    }

    private static RotatedRect ExpandShortSide(RotatedRect rectangle, double longSide, double shortSide)
    {
        var size = rectangle.Size.Width >= rectangle.Size.Height
            ? new Size2f((float)longSide, (float)shortSide)
            : new Size2f((float)shortSide, (float)longSide);
        return new RotatedRect(rectangle.Center, size, rectangle.Angle);
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

    private static void DrawCandidate(
        Mat overlay,
        IReadOnlyList<Point2f> corners,
        double confidence,
        CandidateKind kind)
    {
        var contour = corners.Select(point => new Point((int)point.X, (int)point.Y)).ToArray();
        Cv2.Polylines(overlay, [contour], true, new Scalar(57, 217, 138), 3, LineTypes.AntiAlias);
        var labelPoint = new Point(
            Math.Max(contour.Min(point => point.X), 8),
            Math.Max(contour.Min(point => point.Y) - 12, 24));
        Cv2.PutText(
            overlay,
            kind switch
            {
                CandidateKind.WhiteAnchor => $"Panel white {confidence:P0}",
                CandidateKind.HsvSegmentPair => $"Panel pair {confidence:P0}",
                _ => $"Panel {confidence:P0}"
            },
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

    private static double Dot(Point2f first, Point2f second) =>
        first.X * second.X + first.Y * second.Y;

    private static Point2f NormalizeDownward(Point2f vector)
    {
        var length = vector.DistanceTo(new Point2f(0, 0));
        if (length <= 0)
        {
            return new Point2f(0, 1);
        }

        var normalized = new Point2f((float)(vector.X / length), (float)(vector.Y / length));
        return normalized.Y < 0
            ? new Point2f(-normalized.X, -normalized.Y)
            : normalized;
    }

    private static void ValidateOptions(PanelDetectorOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumHue, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumHue, 179);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(options.MinimumHue, options.MaximumHue);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumSaturation, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MinimumSaturation, 255);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumValue, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MinimumValue, 255);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MinimumHeightRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MinimumHeightRatio, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MinimumAspectRatio, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumAspectRatio, options.MinimumAspectRatio);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NormalizedWidth, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NormalizedHeight, 16);
    }

    private sealed record Candidate(
        RotatedRect Rectangle,
        double AspectRatio,
        double Confidence,
        double Area,
        CandidateKind Kind,
        string Detail)
    {
        public double Rank => Area * Confidence;
    }

    private sealed record Segment(
        RotatedRect Rectangle,
        Point2f Center,
        double LongSide,
        Point2f LongAxis,
        double Top,
        double Bottom);

    private sealed record WhiteAnchor(
        RotatedRect Rectangle,
        double LongSide,
        double ShortSide,
        Point2f LongAxis,
        double WhiteArea,
        double FillRatio,
        double ShapeScore);

    private sealed record RailColorScore(double Left, double Right);

    private enum CandidateKind
    {
        HsvContour,
        HsvSegmentPair,
        WhiteAnchor
    }
}
