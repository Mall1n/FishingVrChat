using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FishingVisionAssistant.App;

/// <summary>
/// Представляет состояние диагностического окна без привязки к реализации захвата и detector.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly Brush UnknownAdviceBrush = CreateFrozenBrush("#414A54");

    private string _sourceStatus = "Источник не выбран";
    private string _sourcePath = "Откройте скриншот или видеозапись для offline-анализа";
    private string _previewTitle = "Нет изображения";
    private string _previewHint = "Нажмите «Открыть запись»";
    private BitmapSource? _sourcePreview;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AdviceText => "НЕ УВЕРЕН";

    public string AdviceReason => "Detector ещё не подключён; рекомендация намеренно заблокирована.";

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

    public BitmapSource? SourcePreview
    {
        get => _sourcePreview;
        private set => SetField(ref _sourcePreview, value);
    }

    public double PanelConfidence => 0;

    public string PanelConfidenceText => "0 %";

    public double WhiteZoneConfidence => 0;

    public string WhiteZoneConfidenceText => "0 %";

    public double FishConfidence => 0;

    public string FishConfidenceText => "0 %";

    public string FramePosition => "—";

    public string PipelineFps => "0.0";

    public string PipelineLatency => "—";

    public void SelectOfflineSource(string path, BitmapSource? preview)
    {
        SourcePath = path;
        SourcePreview = preview;

        if (preview is null)
        {
            SourceStatus = $"Выбрана видеозапись: {Path.GetFileName(path)}";
            PreviewTitle = "Видео выбрано";
            PreviewHint = "Покадровый decoder будет подключён на следующем этапе";
            return;
        }

        SourceStatus = $"Загружен диагностический кадр: {Path.GetFileName(path)}";
        PreviewTitle = string.Empty;
        PreviewHint = string.Empty;
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
