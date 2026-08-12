using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace FishingVisionAssistant.Core;

/// <summary>
/// Выполняет YOLO OBB inference через ONNX Runtime, применяет geometry gate и формирует diagnostic preview.
/// </summary>
public sealed class OnnxPanelDetector : IPanelDetector, IDisposable
{
    private const int ExpectedOutputColumns = 7;
    private const int RectifiedWidth = 96;
    private const int RectifiedHeight = 640;
    private const float DiagnosticConfidence = 0.05f;

    private readonly OnnxPanelDetectorOptions _options;
    private readonly InferenceSession _session;
    private readonly object _sessionSync = new();
    private readonly OnnxExecutionProvider _activeExecutionProvider;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private bool _isDisposed;

    public OnnxPanelDetector(OnnxPanelDetectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _options = options;
        (_session, _activeExecutionProvider, FallbackReason) = CreateSession(options);

        (_inputName, _inputWidth, _inputHeight) = ReadInputContract(_session);
        _outputName = ReadOutputContract(_session);
    }

    /// <summary>
    /// Возвращает имя активного ONNX Runtime backend для диагностического интерфейса.
    /// </summary>
    public string ProviderName => _activeExecutionProvider switch
    {
        OnnxExecutionProvider.DirectMl => "DirectML",
        _ => "CPU"
    };

    /// <summary>
    /// Возвращает статический размер входа текущей ONNX-модели.
    /// </summary>
    public string InputSize => $"{_inputWidth} × {_inputHeight}";

    /// <summary>
    /// Возвращает причину перехода Auto с DirectML на CPU или null, если fallback не потребовался.
    /// </summary>
    public string? FallbackReason { get; }

    /// <inheritdoc />
    public PanelDetectionResult Detect(ReadOnlyMemory<byte> encodedImage)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(encodedImage.Length);

        var stopwatch = Stopwatch.StartNew();
        using var source = Cv2.ImDecode(encodedImage.ToArray(), ImreadModes.Color);
        if (source.Empty())
        {
            throw new ArgumentException("Изображение не удалось декодировать.", nameof(encodedImage));
        }

        return Detect(source, stopwatch, PanelPreviewOutputs.All, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public PanelDetectionResult DetectBgr24(
        byte[] pixels,
        int width,
        int height,
        int stride,
        PanelPreviewOutputs previewOutputs = PanelPreviewOutputs.All)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
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
        return Detect(source, stopwatch, previewOutputs, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public PanelDetectionResult DetectBgra32(
        byte[] pixels,
        int width,
        int height,
        int stride,
        PanelPreviewOutputs previewOutputs = PanelPreviewOutputs.All)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, checked(width * 4));
        if (pixels.Length < checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer меньше заявленной геометрии кадра.", nameof(pixels));
        }

        var stopwatch = Stopwatch.StartNew();
        using var bgra = Mat.FromPixelData(height, width, MatType.CV_8UC4, pixels, stride);
        using var source = new Mat();
        var conversionStopwatch = Stopwatch.StartNew();
        Cv2.CvtColor(bgra, source, ColorConversionCodes.BGRA2BGR);
        conversionStopwatch.Stop();
        return Detect(source, stopwatch, previewOutputs, conversionStopwatch.Elapsed);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sessionSync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _session.Dispose();
        }
    }

    private PanelDetectionResult Detect(
        Mat source,
        Stopwatch stopwatch,
        PanelPreviewOutputs previewOutputs,
        TimeSpan colorConversionTime)
    {
        lock (_sessionSync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return DetectCore(source, stopwatch, previewOutputs, colorConversionTime);
        }
    }

    private PanelDetectionResult DetectCore(
        Mat source,
        Stopwatch stopwatch,
        PanelPreviewOutputs previewOutputs,
        TimeSpan colorConversionTime)
    {
        var phaseStopwatch = Stopwatch.StartNew();
        using var networkInput = CreateLetterbox(source, out var transform);
        phaseStopwatch.Stop();
        var letterboxTime = phaseStopwatch.Elapsed;

        phaseStopwatch.Restart();
        var inputTensor = CreateInputTensor(networkInput);
        phaseStopwatch.Stop();
        var tensorCreationTime = phaseStopwatch.Elapsed;
        var preprocessTime = colorConversionTime + letterboxTime + tensorCreationTime;

        phaseStopwatch.Restart();
        using var outputs = RunInference(inputTensor);
        phaseStopwatch.Stop();
        var inferenceTime = phaseStopwatch.Elapsed;

        phaseStopwatch.Restart();
        var predictions = ReadPredictions(outputs);

        var validPredictions = predictions
            .Where(candidate => candidate.ClassId == 0 && candidate.Width > 1 && candidate.Height > 1)
            .ToArray();
        var accepted = validPredictions
            .Where(IsAccepted)
            .OrderByDescending(candidate => candidate.Confidence)
            .FirstOrDefault();

        if (accepted is null)
        {
            var best = validPredictions.OrderByDescending(candidate => candidate.Confidence).FirstOrDefault();
            var reason = best is null
                ? $"ONNX ({ProviderName}): модель не вернула корректных OBB."
                : $"ONNX ({ProviderName}): лучший OBB не прошёл gate — confidence " +
                  $"{best.Confidence:P1}, aspect ratio {best.AspectRatio:F1}; требуется " +
                  $"≥ {_options.MinimumConfidence:P0} и ≥ {_options.MinimumAspectRatio:F1}.";
            var notFoundOverlayPng = Array.Empty<byte>();
            var notFoundDiagnosticPng = Array.Empty<byte>();
            if (previewOutputs.HasFlag(PanelPreviewOutputs.SourceOverlay))
            {
                using var overlay = source.Clone();
                Cv2.PutText(
                    overlay,
                    "ONNX: panel not found",
                    new Point(20, 40),
                    HersheyFonts.HersheySimplex,
                    1,
                    Scalar.OrangeRed,
                    2,
                    LineTypes.AntiAlias);
                notFoundOverlayPng = EncodePng(overlay);
            }

            if (previewOutputs.HasFlag(PanelPreviewOutputs.OnnxDiagnostic))
            {
                using var diagnostic = networkInput.Clone();
                DrawDiagnosticCandidates(diagnostic, validPredictions, accepted);
                notFoundDiagnosticPng = EncodePng(diagnostic);
            }

            phaseStopwatch.Stop();
            stopwatch.Stop();
            return new PanelDetectionResult(
                false,
                best?.Confidence ?? 0,
                reason,
                Array.Empty<ImagePoint>(),
                notFoundOverlayPng,
                notFoundDiagnosticPng,
                null,
                stopwatch.Elapsed,
                new PanelDetectionTimings(
                    preprocessTime,
                    colorConversionTime,
                    letterboxTime,
                    tensorCreationTime,
                    inferenceTime,
                    phaseStopwatch.Elapsed));
        }

        var inputCorners = CreateCorners(accepted);
        var sourceCorners = inputCorners
            .Select(point => transform.ToSource(point, source.Size()))
            .ToArray();
        var overlayPng = Array.Empty<byte>();
        var diagnosticPng = Array.Empty<byte>();
        byte[]? rectifiedPng = null;
        if (previewOutputs.HasFlag(PanelPreviewOutputs.SourceOverlay))
        {
            using var overlay = source.Clone();
            DrawAcceptedOverlay(overlay, sourceCorners, accepted);
            overlayPng = EncodePng(overlay);
        }

        if (previewOutputs.HasFlag(PanelPreviewOutputs.OnnxDiagnostic))
        {
            using var diagnostic = networkInput.Clone();
            DrawDiagnosticCandidates(diagnostic, validPredictions, accepted);
            diagnosticPng = EncodePng(diagnostic);
        }

        if (previewOutputs.HasFlag(PanelPreviewOutputs.RectifiedPanel))
        {
            using var rectified = Rectify(source, sourceCorners, accepted.Width >= accepted.Height);
            rectifiedPng = EncodePng(rectified);
        }

        phaseStopwatch.Stop();
        stopwatch.Stop();

        return new PanelDetectionResult(
            true,
            accepted.Confidence,
            $"ONNX ({ProviderName}): рамка найдена, confidence {accepted.Confidence:P1}, " +
            $"aspect ratio {accepted.AspectRatio:F1}.",
            sourceCorners
                .Select(point => new ImagePoint(point.X, point.Y))
                .ToArray(),
            overlayPng,
            diagnosticPng,
            rectifiedPng,
            stopwatch.Elapsed,
            new PanelDetectionTimings(
                preprocessTime,
                colorConversionTime,
                letterboxTime,
                tensorCreationTime,
                inferenceTime,
                phaseStopwatch.Elapsed));
    }

    private DenseTensor<float> CreateInputTensor(Mat networkInput)
    {
        using var blob = CvDnn.BlobFromImage(
            networkInput,
            1d / 255,
            new Size(_inputWidth, _inputHeight),
            Scalar.All(0),
            swapRB: true,
            crop: false);
        var inputValues = new float[checked(3 * _inputWidth * _inputHeight)];
        Marshal.Copy(blob.Data, inputValues, 0, inputValues.Length);
        return new DenseTensor<float>(inputValues, [1, 3, _inputHeight, _inputWidth]);
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunInference(DenseTensor<float> inputTensor)
    {
        var input = NamedOnnxValue.CreateFromTensor(_inputName, inputTensor);
        return _session.Run([input], [_outputName]);
    }

    private IReadOnlyList<ObbCandidate> ReadPredictions(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var output = outputs.First().AsTensor<float>();
        if (output.Dimensions.Length != 3 || output.Dimensions[0] != 1 ||
            output.Dimensions[2] != ExpectedOutputColumns)
        {
            throw new InvalidDataException(
                $"ONNX output должен иметь форму [1, N, {ExpectedOutputColumns}], получено " +
                $"[{string.Join(", ", output.Dimensions.ToArray())}].");
        }

        var candidates = new List<ObbCandidate>(output.Dimensions[1]);
        for (var index = 0; index < output.Dimensions[1]; index++)
        {
            var candidate = new ObbCandidate(
                output[0, index, 0],
                output[0, index, 1],
                output[0, index, 2],
                output[0, index, 3],
                output[0, index, 4],
                (int)MathF.Round(output[0, index, 5]),
                output[0, index, 6]);
            if (candidate.IsFinite)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static IReadOnlyList<ObbCandidate> ReadPredictions(float[] rawPredictions)
    {
        if (rawPredictions.Length == 0 || rawPredictions.Length % ExpectedOutputColumns != 0)
        {
            throw new InvalidDataException("GPU bridge вернул ONNX output с недопустимой формой.");
        }

        var candidates = new List<ObbCandidate>(rawPredictions.Length / ExpectedOutputColumns);
        for (var index = 0; index < rawPredictions.Length; index += ExpectedOutputColumns)
        {
            var candidate = new ObbCandidate(
                rawPredictions[index],
                rawPredictions[index + 1],
                rawPredictions[index + 2],
                rawPredictions[index + 3],
                rawPredictions[index + 4],
                (int)MathF.Round(rawPredictions[index + 5]),
                rawPredictions[index + 6]);
            if (candidate.IsFinite)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private Mat CreateLetterbox(Mat source, out LetterboxTransform transform)
    {
        transform = CreateLetterboxTransform(source.Width, source.Height);
        var resizedWidth = Math.Max(1, (int)Math.Round(source.Width * transform.Scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(source.Height * transform.Scale));

        var letterbox = new Mat(new Size(_inputWidth, _inputHeight), MatType.CV_8UC3, new Scalar(114, 114, 114));
        using var target = new Mat(letterbox, new Rect(transform.Left, transform.Top, resizedWidth, resizedHeight));
        Cv2.Resize(source, target, target.Size(), interpolation: InterpolationFlags.Linear);
        return letterbox;
    }

    private LetterboxTransform CreateLetterboxTransform(int sourceWidth, int sourceHeight)
    {
        var scale = Math.Min((double)_inputWidth / sourceWidth, (double)_inputHeight / sourceHeight);
        var resizedWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return new LetterboxTransform(
            scale,
            (int)Math.Round((_inputWidth - resizedWidth) / 2d - 0.1),
            (int)Math.Round((_inputHeight - resizedHeight) / 2d - 0.1));
    }

    private bool IsAccepted(ObbCandidate candidate) =>
        candidate.Confidence >= _options.MinimumConfidence &&
        candidate.AspectRatio >= _options.MinimumAspectRatio;

    private void DrawDiagnosticCandidates(
        Mat diagnostic,
        IReadOnlyList<ObbCandidate> candidates,
        ObbCandidate? accepted)
    {
        foreach (var candidate in candidates
                     .Where(candidate => candidate.Confidence >= DiagnosticConfidence)
                     .OrderByDescending(candidate => candidate.Confidence)
                     .Take(12))
        {
            var isAccepted = accepted is not null && candidate.Equals(accepted);
            var color = isAccepted
                ? Scalar.LimeGreen
                : candidate.Confidence >= _options.MinimumConfidence
                    ? Scalar.Orange
                    : Scalar.Gray;
            DrawPolygon(diagnostic, CreateCorners(candidate), color, isAccepted ? 3 : 1);
        }

        Cv2.PutText(
            diagnostic,
            $"ONNX input {_inputWidth}x{_inputHeight} | {ProviderName}",
            new Point(16, 30),
            HersheyFonts.HersheySimplex,
            0.65,
            Scalar.White,
            2,
            LineTypes.AntiAlias);
    }

    private static void DrawAcceptedOverlay(Mat overlay, Point2f[] corners, ObbCandidate candidate)
    {
        DrawPolygon(overlay, corners, Scalar.LimeGreen, 3);
        var anchor = new Point(
            Math.Clamp((int)Math.Round(corners.Min(point => point.X)), 0, overlay.Width - 1),
            Math.Clamp((int)Math.Round(corners.Min(point => point.Y)) - 10, 22, overlay.Height - 1));
        Cv2.PutText(
            overlay,
            $"ONNX {candidate.Confidence:P0} | AR {candidate.AspectRatio:F1}",
            anchor,
            HersheyFonts.HersheySimplex,
            0.65,
            Scalar.LimeGreen,
            2,
            LineTypes.AntiAlias);
    }

    private static void DrawPolygon(Mat image, IReadOnlyList<Point2f> corners, Scalar color, int thickness)
    {
        var points = corners
            .Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
            .ToArray();
        for (var index = 0; index < points.Length; index++)
        {
            Cv2.Line(image, points[index], points[(index + 1) % points.Length], color, thickness, LineTypes.AntiAlias);
        }
    }

    private static Point2f[] CreateCorners(ObbCandidate candidate)
    {
        var halfWidth = candidate.Width / 2;
        var halfHeight = candidate.Height / 2;
        var localCorners = new[]
        {
            new Point2f(-halfWidth, -halfHeight),
            new Point2f(halfWidth, -halfHeight),
            new Point2f(halfWidth, halfHeight),
            new Point2f(-halfWidth, halfHeight)
        };
        var cosine = MathF.Cos(candidate.Angle);
        var sine = MathF.Sin(candidate.Angle);
        return localCorners
            .Select(point => new Point2f(
                candidate.CenterX + point.X * cosine - point.Y * sine,
                candidate.CenterY + point.X * sine + point.Y * cosine))
            .ToArray();
    }

    private static Mat Rectify(Mat source, Point2f[] corners, bool widthIsLongAxis)
    {
        // YOLO хранит вершины в локальном порядке OBB; перестановка разворачивает длинную сторону вертикально.
        var sourcePoints = widthIsLongAxis
            ? new[] { corners[0], corners[3], corners[2], corners[1] }
            : corners;
        var destinationPoints = new[]
        {
            new Point2f(0, 0),
            new Point2f(RectifiedWidth - 1, 0),
            new Point2f(RectifiedWidth - 1, RectifiedHeight - 1),
            new Point2f(0, RectifiedHeight - 1)
        };
        using var transform = Cv2.GetPerspectiveTransform(sourcePoints, destinationPoints);
        var rectified = new Mat();
        Cv2.WarpPerspective(source, rectified, transform, new Size(RectifiedWidth, RectifiedHeight));
        return rectified;
    }

    private static byte[] EncodePng(Mat image)
    {
        Cv2.ImEncode(".png", image, out var encoded);
        return encoded;
    }

    private static (InferenceSession Session, OnnxExecutionProvider ActiveProvider, string? FallbackReason)
        CreateSession(OnnxPanelDetectorOptions options)
    {
        var modelPath = Path.GetFullPath(options.ModelPath);
        if (options.ExecutionProvider != OnnxExecutionProvider.Auto)
        {
            return (
                CreateInferenceSession(modelPath, options.ExecutionProvider, options.DeviceId),
                options.ExecutionProvider,
                null);
        }

        try
        {
            return (
                CreateInferenceSession(modelPath, OnnxExecutionProvider.DirectMl, options.DeviceId),
                OnnxExecutionProvider.DirectMl,
                null);
        }
        catch (Exception directMlException)
        {
            try
            {
                return (
                    CreateInferenceSession(modelPath, OnnxExecutionProvider.Cpu, options.DeviceId),
                    OnnxExecutionProvider.Cpu,
                    directMlException.Message);
            }
            catch (Exception cpuException)
            {
                throw new AggregateException(
                    "Модель не удалось загрузить ни через DirectML, ни через CPU.",
                    directMlException,
                    cpuException);
            }
        }
    }

    private static InferenceSession CreateInferenceSession(
        string modelPath,
        OnnxExecutionProvider executionProvider,
        int deviceId)
    {
        using var sessionOptions = CreateSessionOptions(executionProvider, deviceId);
        return new InferenceSession(modelPath, sessionOptions);
    }

    private static SessionOptions CreateSessionOptions(
        OnnxExecutionProvider executionProvider,
        int deviceId)
    {
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        if (executionProvider == OnnxExecutionProvider.DirectMl)
        {
            // DirectML требует последовательное выполнение и отключённый memory pattern.
            sessionOptions.EnableMemoryPattern = false;
            sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            sessionOptions.AppendExecutionProvider_DML(deviceId);
        }

        return sessionOptions;
    }

    private static (string Name, int Width, int Height) ReadInputContract(InferenceSession session)
    {
        if (session.InputMetadata.Count != 1)
        {
            throw new InvalidDataException("ONNX detector должен иметь ровно один input tensor.");
        }

        var input = session.InputMetadata.Single();
        var dimensions = input.Value.Dimensions;
        if (dimensions.Length != 4 || dimensions[0] != 1 || dimensions[1] != 3 ||
            dimensions[2] <= 0 || dimensions[3] <= 0)
        {
            throw new InvalidDataException(
                $"ONNX input должен иметь статическую форму [1, 3, H, W], получено " +
                $"[{string.Join(", ", dimensions)}].");
        }

        return (input.Key, dimensions[3], dimensions[2]);
    }

    private static string ReadOutputContract(InferenceSession session)
    {
        if (session.OutputMetadata.Count != 1)
        {
            throw new InvalidDataException("ONNX detector должен иметь ровно один output tensor.");
        }

        var output = session.OutputMetadata.Single();
        var dimensions = output.Value.Dimensions;
        if (dimensions.Length != 3 || dimensions[0] != 1 || dimensions[2] != ExpectedOutputColumns)
        {
            throw new InvalidDataException(
                $"ONNX output должен иметь форму [1, N, {ExpectedOutputColumns}], получено " +
                $"[{string.Join(", ", dimensions)}].");
        }

        return output.Key;
    }

    private static void ValidateOptions(OnnxPanelDetectorOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            throw new ArgumentException("Путь к ONNX-модели не задан.", nameof(options));
        }

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException("ONNX-модель не найдена.", options.ModelPath);
        }

        if (options.MinimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Confidence должен находиться в диапазоне 0–1.");
        }

        if (options.MinimumAspectRatio < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Aspect ratio не может быть меньше 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.DeviceId);
        if (!Enum.IsDefined(options.ExecutionProvider))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Неизвестный ONNX execution provider.");
        }
    }

    private sealed record ObbCandidate(
        float CenterX,
        float CenterY,
        float Width,
        float Height,
        float Confidence,
        int ClassId,
        float Angle)
    {
        public double AspectRatio => Math.Max(Width, Height) / Math.Max(1, Math.Min(Width, Height));

        public bool IsFinite =>
            float.IsFinite(CenterX) &&
            float.IsFinite(CenterY) &&
            float.IsFinite(Width) &&
            float.IsFinite(Height) &&
            float.IsFinite(Confidence) &&
            float.IsFinite(Angle);
    }

    private readonly record struct LetterboxTransform(double Scale, int Left, int Top)
    {
        public Point2f ToSource(Point2f point, Size sourceSize) => new(
            Math.Clamp((float)((point.X - Left) / Scale), 0, sourceSize.Width - 1),
            Math.Clamp((float)((point.Y - Top) / Scale), 0, sourceSize.Height - 1));
    }
}
