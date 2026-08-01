using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private string _framePosition = "—";
    private double _panelConfidence;
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

    public string PipelineFps => "—";

    public string PipelineLatency
    {
        get => _pipelineLatency;
        private set => SetField(ref _pipelineLatency, value);
    }

    public void BeginImageAnalysis(string path)
    {
        SourcePath = path;
        SourceStatus = $"Анализируется кадр: {Path.GetFileName(path)}";
        PreviewTitle = "Обработка…";
        PreviewHint = string.Empty;
        PanelReason = "Detector выполняет поиск рамки";
        PanelConfidence = 0;
        SourcePreview = null;
        RectifiedPreview = null;
        MaskPreview = null;
        FramePosition = "1 / 1";
        PipelineLatency = "—";
    }

    public void ApplyPanelDetection(string path, PanelDetectionResult result)
    {
        SourcePath = path;
        SourceStatus = result.IsDetected
            ? $"Рамка найдена: {Path.GetFileName(path)}"
            : $"Рамка не найдена: {Path.GetFileName(path)}";
        PreviewTitle = string.Empty;
        PreviewHint = string.Empty;
        PanelReason = result.Reason;
        PanelConfidence = result.Confidence;
        PipelineLatency = $"{result.ProcessingTime.TotalMilliseconds:F1} мс";
        FramePosition = "1 / 1";
        SourcePreview = DecodeImage(result.OverlayPng);
        MaskPreview = DecodeImage(result.MaskPng);
        RectifiedPreview = result.RectifiedPanelPng is null ? null : DecodeImage(result.RectifiedPanelPng);
    }

    public void SelectVideoSource(string path)
    {
        SourcePath = path;
        SourceStatus = $"Выбрана видеозапись: {Path.GetFileName(path)}";
        PreviewTitle = "Видео выбрано";
        PreviewHint = "Покадровый decoder будет подключён на следующем этапе";
        PanelReason = "Ожидание decoder";
        PanelConfidence = 0;
        SourcePreview = null;
        RectifiedPreview = null;
        MaskPreview = null;
        FramePosition = "—";
        PipelineLatency = "—";
    }

    public void ApplyAnalysisError(string message)
    {
        SourceStatus = "Ошибка анализа";
        PreviewTitle = "Не удалось обработать изображение";
        PreviewHint = message;
        PanelReason = message;
        PanelConfidence = 0;
        RectifiedPreview = null;
        MaskPreview = null;
        PipelineLatency = "—";
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
