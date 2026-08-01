using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
    private string _pipelineFps = "—";
    private string _performanceSummary = "Нет измерений";
    private string _cacheStatus = "—";
    private string _framePosition = "—";
    private string _videoPosition = "—";
    private string _playbackSpeedText = "1×";
    private double _panelConfidence;
    private double _timelineMaximum = 1;
    private double _timelineValue;
    private bool _isVideoLoaded;
    private bool _isBusy;
    private bool _isPlaying;
    private BitmapSource? _sourcePreview;
    private BitmapSource? _rectifiedPreview;
    private BitmapSource? _maskPreview;

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
        private set => SetField(ref _sourcePath, value);
    }

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

    public void BeginFrameAnalysis(long frameIndex)
    {
        IsBusy = true;
        PreviewTitle = SourcePreview is null ? "Декодирование кадра…" : string.Empty;
        PanelReason = $"Анализ кадра {frameIndex + 1:N0}";
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

    public void ApplyAnalysisError(string message)
    {
        SourceStatus = "Ошибка анализа";
        PreviewTitle = "Не удалось обработать источник";
        PreviewHint = message;
        PanelReason = message;
        PanelConfidence = 0;
        RectifiedPreview = null;
        MaskPreview = null;
        PipelineLatency = "—";
        CacheStatus = "ошибка";
        IsBusy = false;
    }

    private void ApplyDetectionResult(PanelDetectionResult result)
    {
        PreviewTitle = string.Empty;
        PreviewHint = string.Empty;
        PanelReason = result.Reason;
        PanelConfidence = result.Confidence;
        SourcePreview = DecodeImage(result.OverlayPng);
        MaskPreview = DecodeImage(result.MaskPng);
        RectifiedPreview = result.RectifiedPanelPng is null ? null : DecodeImage(result.RectifiedPanelPng);
    }

    private void ResetDetectionResult()
    {
        PanelConfidence = 0;
        SourcePreview = null;
        RectifiedPreview = null;
        MaskPreview = null;
        PipelineLatency = "—";
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
