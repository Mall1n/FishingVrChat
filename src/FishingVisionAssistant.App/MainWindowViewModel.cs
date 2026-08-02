using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FishingVisionAssistant.Capture;
using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Представляет состояние Frame Inspector и преобразует diagnostic result в WPF-изображения.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly Brush UnknownAdviceBrush = CreateFrozenBrush("#414A54");

    private string _sourceStatus = "Источник не выбран";
    private string _sourcePath = "Откройте скриншот или видеозапись для offline-анализа";
    private string _previewTitle = "Нет изображения";
    private string _previewHint = "Нажмите «Открыть запись»";
    private string _panelReason = "Ожидание кадра";
    private string _pipelineLatency = "—";
    private string _decodeLatency = "—";
    private string _queueWaitLatency = "—";
    private string _inputLatencyLabel = "Decode";
    private string _preprocessLatency = "—";
    private string _colorConversionLatency = "—";
    private string _letterboxLatency = "—";
    private string _tensorCreationLatency = "—";
    private string _inferenceLatency = "—";
    private string _postprocessLatency = "—";
    private string _pipelineFps = "—";
    private string _performanceSummary = "Нет измерений";
    private string _cacheStatus = "—";
    private string _framePosition = "—";
    private string _videoPosition = "—";
    private string _playbackSpeedText = "1×";
    private string _diagnosticPreviewTitle = "Диагностика ONNX";
    private double _panelConfidence;
    private double _timelineMaximum = 1;
    private double _timelineValue;
    private bool _isVideoLoaded;
    private bool _isBusy;
    private bool _isPlaying;
    private WriteableBitmap? _liveSourceBitmap;
    private BitmapSource? _sourcePreview;
    private BitmapSource? _rectifiedPreview;
    private BitmapSource? _maskPreview;
    private BitmapSource? _trainingBoundsPreview;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AdviceText => "НЕ УВЕРЕН";

    public string AdviceReason => "Detector игровых элементов ещё не подключён; рекомендация намеренно заблокирована.";

    public Brush AdviceBrush => UnknownAdviceBrush;

    public string SourceStatus
    {
        get => _sourceStatus;
        private set => SetField(ref _sourceStatus, value);
    }

    public string SourcePath
    {
        get => _sourcePath;
        private set
        {
            if (SetField(ref _sourcePath, value))
            {
                OnPropertyChanged(nameof(SourceName));
            }
        }
    }

    public string SourceName => string.IsNullOrWhiteSpace(SourcePath)
        ? "Источник не выбран"
        : Path.GetFileName(SourcePath);

    public string PreviewTitle
    {
        get => _previewTitle;
        private set => SetField(ref _previewTitle, value);
    }

    public string PreviewHint
    {
        get => _previewHint;
        private set => SetField(ref _previewHint, value);
    }

    public string PanelReason
    {
        get => _panelReason;
        private set => SetField(ref _panelReason, value);
    }

    public BitmapSource? SourcePreview
    {
        get => _sourcePreview;
        private set => SetField(ref _sourcePreview, value);
    }

    public BitmapSource? RectifiedPreview
    {
        get => _rectifiedPreview;
        private set => SetField(ref _rectifiedPreview, value);
    }

    public BitmapSource? MaskPreview
    {
        get => _maskPreview;
        private set => SetField(ref _maskPreview, value);
    }

    public string DiagnosticPreviewTitle
    {
        get => _diagnosticPreviewTitle;
        private set => SetField(ref _diagnosticPreviewTitle, value);
    }

    public BitmapSource? TrainingBoundsPreview
    {
        get => _trainingBoundsPreview;
        private set => SetField(ref _trainingBoundsPreview, value);
    }

    public double PanelConfidence
    {
        get => _panelConfidence;
        private set
        {
            if (SetField(ref _panelConfidence, value))
            {
                OnPropertyChanged(nameof(PanelConfidenceText));
            }
        }
    }

    public string PanelConfidenceText => PanelConfidence.ToString("P0");

    public double WhiteZoneConfidence => 0;

    public string WhiteZoneConfidenceText => "0 %";

    public double FishConfidence => 0;

    public string FishConfidenceText => "0 %";

    public string FramePosition
    {
        get => _framePosition;
        private set => SetField(ref _framePosition, value);
    }

    public string VideoPosition
    {
        get => _videoPosition;
        private set => SetField(ref _videoPosition, value);
    }

    public string PlaybackSpeedText
    {
        get => _playbackSpeedText;
        private set => SetField(ref _playbackSpeedText, value);
    }

    public string PipelineFps
    {
        get => _pipelineFps;
        private set => SetField(ref _pipelineFps, value);
    }

    public string PipelineLatency
    {
        get => _pipelineLatency;
        private set => SetField(ref _pipelineLatency, value);
    }

    public string DecodeLatency
    {
        get => _decodeLatency;
        private set => SetField(ref _decodeLatency, value);
    }

    public string QueueWaitLatency
    {
        get => _queueWaitLatency;
        private set => SetField(ref _queueWaitLatency, value);
    }

    public string InputLatencyLabel
    {
        get => _inputLatencyLabel;
        private set => SetField(ref _inputLatencyLabel, value);
    }

    public string PreprocessLatency
    {
        get => _preprocessLatency;
        private set => SetField(ref _preprocessLatency, value);
    }

    public string ColorConversionLatency
    {
        get => _colorConversionLatency;
        private set => SetField(ref _colorConversionLatency, value);
    }

    public string LetterboxLatency
    {
        get => _letterboxLatency;
        private set => SetField(ref _letterboxLatency, value);
    }

    public string TensorCreationLatency
    {
        get => _tensorCreationLatency;
        private set => SetField(ref _tensorCreationLatency, value);
    }

    public string InferenceLatency
    {
        get => _inferenceLatency;
        private set => SetField(ref _inferenceLatency, value);
    }

    public string PostprocessLatency
    {
        get => _postprocessLatency;
        private set => SetField(ref _postprocessLatency, value);
    }

    public string PerformanceSummary
    {
        get => _performanceSummary;
        private set => SetField(ref _performanceSummary, value);
    }

    public string CacheStatus
    {
        get => _cacheStatus;
        private set => SetField(ref _cacheStatus, value);
    }

    public double TimelineMaximum
    {
        get => _timelineMaximum;
        private set => SetField(ref _timelineMaximum, value);
    }

    public double TimelineValue
    {
        get => _timelineValue;
        private set => SetField(ref _timelineValue, value);
    }

    public bool IsVideoLoaded
    {
        get => _isVideoLoaded;
        private set
        {
            if (SetField(ref _isVideoLoaded, value))
            {
                OnPropertyChanged(nameof(CanNavigate));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanNavigate));
            }
        }
    }

    public bool CanNavigate => IsVideoLoaded;

    public string PlayPauseText => _isPlaying ? "⏸" : "▶";

    public void BeginImageAnalysis(string path)
    {
        ResetVideoState();
        InputLatencyLabel = "Decode";
        SourcePath = path;
        SourceStatus = $"Анализируется кадр: {Path.GetFileName(path)}";
        PreviewTitle = "Обработка…";
        PreviewHint = string.Empty;
        PanelReason = "Detector выполняет поиск рамки";
        FramePosition = "1 / 1";
        IsBusy = true;
        ResetDetectionResult();
    }

    public void ApplyPanelDetection(string path, PanelDetectionResult result)
    {
        SourcePath = path;
        SourceStatus = result.IsDetected
            ? $"Рамка найдена: {Path.GetFileName(path)}"
            : $"Рамка не найдена: {Path.GetFileName(path)}";
        ApplyDetectionResult(result);
        FramePosition = "1 / 1";
        VideoPosition = "Статическое изображение";
        PipelineLatency = $"{result.ProcessingTime.TotalMilliseconds:F1} мс";
        DecodeLatency = "—";
        PipelineFps = result.ProcessingTime.TotalMilliseconds <= 0
            ? "—"
            : $"{1000 / result.ProcessingTime.TotalMilliseconds:F1}";
        PerformanceSummary = "Один статический кадр";
        CacheStatus = "без кэша";
        IsBusy = false;
    }

    public void BeginVideoOpen(string path)
    {
        ResetVideoState();
        InputLatencyLabel = "Decode";
        SourcePath = path;
        SourceStatus = $"Открывается видео: {Path.GetFileName(path)}";
        PreviewTitle = "Чтение метаданных…";
        PreviewHint = string.Empty;
        PanelReason = "Ожидание первого кадра";
        IsBusy = true;
        ResetDetectionResult();
    }

    public void InitializeVideo(VideoMetadata metadata)
    {
        SourcePath = metadata.SourcePath;
        SourceStatus = $"{metadata.Width} × {metadata.Height} · {metadata.FramesPerSecond:F2} FPS · {FormatTime(metadata.Duration)}";
        TimelineMaximum = Math.Max(metadata.FrameCount - 1, 1);
        TimelineValue = 0;
        FramePosition = $"1 / {metadata.FrameCount:N0}";
        VideoPosition = $"00:00.000 / {FormatTime(metadata.Duration)}";
        PreviewTitle = "Декодирование первого кадра…";
        IsVideoLoaded = true;
        IsBusy = false;
    }

    /// <summary>
    /// Переводит Frame Inspector в режим непрерывного live-источника.
    /// </summary>
    public void BeginLiveCapture(FrameSourceDescriptor descriptor)
    {
        ResetVideoState();
        ResetDetectionResult();
        SourcePath = descriptor.DisplayName;
        SourceStatus = "Live capture запущен · обрабатывается только самый свежий кадр";
        PreviewTitle = "Ожидание первого live-кадра…";
        PreviewHint = string.Empty;
        PanelReason = "Ожидание live inference";
        VideoPosition = "LIVE";
        InputLatencyLabel = "GPU → CPU copy";
    }

    /// <summary>
    /// Обновляет live-метрики на каждом inference и применяет только созданные detector preview.
    /// </summary>
    public void ApplyLiveFrame(
        LiveFrameAnalysis analysis,
        PerformanceSnapshot performance,
        LivePreviewSettings previewSettings)
    {
        ArgumentNullException.ThrowIfNull(previewSettings);
        var result = analysis.PanelDetection;
        PreviewTitle = string.Empty;
        PreviewHint = string.Empty;
        PanelReason = result.Reason;
        PanelConfidence = result.Confidence;
        if (analysis.PreviewOutputs != PanelPreviewOutputs.None)
        {
            if (analysis.PreviewOutputs.HasFlag(PanelPreviewOutputs.SourceOverlay) &&
                previewSettings.UpdateSourcePreview &&
                analysis.SourcePreviewFrame is not null)
            {
                ApplyLiveSourceFrame(analysis.SourcePreviewFrame);
            }

            if (analysis.PreviewOutputs.HasFlag(PanelPreviewOutputs.OnnxDiagnostic) &&
                previewSettings.UpdateOnnxDiagnosticPreview &&
                result.MaskPng.Length > 0)
            {
                MaskPreview = DecodeImage(result.MaskPng);
            }

            if (analysis.PreviewOutputs.HasFlag(PanelPreviewOutputs.RectifiedPanel) &&
                previewSettings.UpdateRectifiedPreview)
            {
                RectifiedPreview = result.RectifiedPanelPng is null
                    ? null
                    : DecodeImage(result.RectifiedPanelPng);
            }
        }

        PreprocessLatency = FormatLatency(result.Timings?.Preprocess);
        ColorConversionLatency = FormatLatency(result.Timings?.ColorConversion);
        LetterboxLatency = FormatLatency(result.Timings?.Letterbox);
        TensorCreationLatency = FormatLatency(result.Timings?.TensorCreation);
        InferenceLatency = FormatLatency(result.Timings?.Inference);
        PostprocessLatency = FormatLatency(result.Timings?.Postprocess);
        PipelineLatency = FormatLatency(analysis.EndToEndTime);
        DecodeLatency = FormatLatency(analysis.CaptureCopyTime);
        QueueWaitLatency = FormatLatency(analysis.QueueTime);
        PipelineFps = performance.SampleCount == 0 ? "—" : $"{performance.FramesPerSecond:F1}";
        PerformanceSummary = performance.SampleCount == 0
            ? $"live cold {performance.ColdStartMilliseconds:F1} мс · ожидание прогретых кадров"
            : $"live cold {performance.ColdStartMilliseconds:F1} · median {performance.MedianMilliseconds:F1} · " +
              $"p95 {performance.Percentile95Milliseconds:F1} мс";
        CacheStatus = FormatLiveProcessingMode(analysis.PreviewOutputs);
        FramePosition = $"live #{analysis.SequenceNumber:N0}";
        VideoPosition = "LIVE · latest-frame";
        SourceStatus = result.IsDetected
            ? "Live capture · рамка найдена"
            : "Live capture · рамка не найдена";
    }

    public void EndLiveCapture(string status)
    {
        SourceStatus = status;
        CacheStatus = "live остановлен";
    }

    /// <summary>
    /// Отмечает live session как приостановленную или продолженную, сохраняя последние preview и метрики.
    /// </summary>
    public void SetLiveCapturePaused(bool isPaused)
    {
        SourceStatus = isPaused
            ? "Live capture приостановлен · последний результат сохранён"
            : "Live capture продолжен · ожидается свежий кадр";
        CacheStatus = isPaused ? "live · пауза" : "live · продолжен";
        VideoPosition = isPaused ? "LIVE · пауза" : "LIVE · latest-frame";
    }

    public void BeginFrameAnalysis(long frameIndex)
    {
        IsBusy = true;
        PreviewTitle = SourcePreview is null ? "Декодирование кадра…" : string.Empty;
        PanelReason = $"Анализ кадра {frameIndex + 1:N0}";
    }

    public void ApplyFrameDecodeFailure(VideoMetadata metadata, long frameIndex)
    {
        ResetDetectionResult();
        TimelineValue = frameIndex;
        FramePosition = $"{frameIndex + 1:N0} / {metadata.FrameCount:N0}";
        VideoPosition = $"{FormatTime(TimeSpan.FromSeconds(frameIndex / metadata.FramesPerSecond))} / {FormatTime(metadata.Duration)}";
        SourceStatus = $"Не удалось декодировать кадр {frameIndex + 1:N0}";
        PreviewTitle = "Кадр недоступен";
        PreviewHint = "Видео не содержит декодируемых данных для выбранной позиции.";
        PanelReason = "Декодирование кадра не удалось";
        IsBusy = false;
    }
    public void ApplyVideoFrame(
        VideoMetadata metadata,
        VideoFrameAnalysis analysis,
        PerformanceSnapshot performance)
    {
        ApplyDetectionResult(analysis.PanelDetection);
        TimelineValue = analysis.FrameIndex;
        FramePosition = $"{analysis.FrameIndex + 1:N0} / {metadata.FrameCount:N0}";
        VideoPosition = $"{FormatTime(analysis.Position)} / {FormatTime(metadata.Duration)}";
        PipelineLatency = analysis.IsFromCache
            ? "0.0 мс"
            : $"{analysis.ProcessingTime.TotalMilliseconds:F1} мс";
        DecodeLatency = analysis.IsFromCache
            ? "кэш"
            : FormatLatency(analysis.DecodeTime);
        if (analysis.IsFromCache)
        {
            PreprocessLatency = "кэш";
            ColorConversionLatency = "кэш";
            LetterboxLatency = "кэш";
            TensorCreationLatency = "кэш";
            InferenceLatency = "кэш";
            PostprocessLatency = "кэш";
        }
        else
        {
            ColorConversionLatency = FormatLatency(analysis.PanelDetection.Timings?.ColorConversion);
            LetterboxLatency = FormatLatency(analysis.PanelDetection.Timings?.Letterbox);
            TensorCreationLatency = FormatLatency(analysis.PanelDetection.Timings?.TensorCreation);
        }
        PipelineFps = performance.SampleCount == 0 ? "—" : $"{performance.FramesPerSecond:F1}";
        PerformanceSummary = performance.SampleCount == 0
            ? $"cold {performance.ColdStartMilliseconds:F1} мс · ожидание прогретых кадров"
            : $"cold {performance.ColdStartMilliseconds:F1} · median {performance.MedianMilliseconds:F1} · p95 {performance.Percentile95Milliseconds:F1} мс";
        CacheStatus = analysis.IsFromCache ? "LRU-кэш" : $"decode {analysis.DecodeTime.TotalMilliseconds:F1} мс";
        SourceStatus = analysis.PanelDetection.IsDetected
            ? $"Рамка найдена на кадре {analysis.FrameIndex + 1:N0}"
            : $"Рамка не найдена на кадре {analysis.FrameIndex + 1:N0}";
        IsBusy = false;
    }

    public void SetPlaying(bool isPlaying)
    {
        if (_isPlaying == isPlaying)
        {
            return;
        }

        _isPlaying = isPlaying;
        OnPropertyChanged(nameof(PlayPauseText));
    }

    public void SetPlaybackSpeed(double speed) => PlaybackSpeedText = $"{speed:0.##}×";

    public void SetTrainingBoundsPreview(byte[]? encodedPreview) =>
        TrainingBoundsPreview = encodedPreview is null ? null : DecodeImage(encodedPreview);

    public void ApplyAnalysisError(string message)
    {
        SourceStatus = "Ошибка анализа";
        PreviewTitle = "Не удалось обработать источник";
        PreviewHint = message;
        PanelReason = message;
        PanelConfidence = 0;
        RectifiedPreview = null;
        MaskPreview = null;
        TrainingBoundsPreview = null;
        PipelineLatency = "—";
        ResetTimingBreakdown();
        CacheStatus = "ошибка";
        IsBusy = false;
    }

    private void ApplyDetectionResult(PanelDetectionResult result)
    {
        _liveSourceBitmap = null;
        PreviewTitle = string.Empty;
        PreviewHint = string.Empty;
        PanelReason = result.Reason;
        PanelConfidence = result.Confidence;
        SourcePreview = DecodeImage(result.OverlayPng);
        MaskPreview = DecodeImage(result.MaskPng);
        RectifiedPreview = result.RectifiedPanelPng is null ? null : DecodeImage(result.RectifiedPanelPng);
        PreprocessLatency = FormatLatency(result.Timings?.Preprocess);
        ColorConversionLatency = FormatLatency(result.Timings?.ColorConversion);
        LetterboxLatency = FormatLatency(result.Timings?.Letterbox);
        TensorCreationLatency = FormatLatency(result.Timings?.TensorCreation);
        InferenceLatency = FormatLatency(result.Timings?.Inference);
        PostprocessLatency = FormatLatency(result.Timings?.Postprocess);
    }

    private void ResetDetectionResult()
    {
        _liveSourceBitmap = null;
        PanelConfidence = 0;
        SourcePreview = null;
        RectifiedPreview = null;
        MaskPreview = null;
        TrainingBoundsPreview = null;
        PipelineLatency = "—";
        ResetTimingBreakdown();
        PipelineFps = "—";
        PerformanceSummary = "Нет измерений";
        CacheStatus = "—";
    }

    private void ResetVideoState()
    {
        IsVideoLoaded = false;
        IsBusy = false;
        TimelineMaximum = 1;
        TimelineValue = 0;
        FramePosition = "—";
        VideoPosition = "—";
        SetPlaying(false);
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss\.fff") : time.ToString(@"mm\:ss\.fff");

    private static string FormatLatency(TimeSpan? time) =>
        time is null ? "—" : $"{time.Value.TotalMilliseconds:F1} мс";

    private static string FormatLiveProcessingMode(PanelPreviewOutputs outputs)
    {
        if (outputs == PanelPreviewOutputs.None)
        {
            return "Inference · без preview";
        }

        var previewNames = new List<string>(3);
        if (outputs.HasFlag(PanelPreviewOutputs.SourceOverlay))
        {
            previewNames.Add("кадр");
        }

        if (outputs.HasFlag(PanelPreviewOutputs.RectifiedPanel))
        {
            previewNames.Add("шкала");
        }

        if (outputs.HasFlag(PanelPreviewOutputs.OnnxDiagnostic))
        {
            previewNames.Add("diag");
        }

        return $"Inference · {string.Join("/", previewNames)}";
    }

    private void ApplyLiveSourceFrame(CapturedFrame frame)
    {
        if (frame.PixelFormat != FramePixelFormat.Bgra32)
        {
            throw new InvalidDataException($"Live preview не поддерживает формат {frame.PixelFormat}.");
        }

        if (_liveSourceBitmap is null ||
            _liveSourceBitmap.PixelWidth != frame.Width ||
            _liveSourceBitmap.PixelHeight != frame.Height)
        {
            _liveSourceBitmap = new WriteableBitmap(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            SourcePreview = _liveSourceBitmap;
        }

        _liveSourceBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.PixelBuffer,
            frame.Stride,
            0);
    }

    private void ResetTimingBreakdown()
    {
        DecodeLatency = "—";
        QueueWaitLatency = "—";
        PreprocessLatency = "—";
        ColorConversionLatency = "—";
        LetterboxLatency = "—";
        TensorCreationLatency = "—";
        InferenceLatency = "—";
        PostprocessLatency = "—";
    }

    private static BitmapSource DecodeImage(byte[] encodedImage)
    {
        using var stream = new MemoryStream(encodedImage, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
