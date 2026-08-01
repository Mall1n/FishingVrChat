using System.IO;
using System.Windows;
using FishingVisionAssistant.Core;
using Microsoft.Win32;

namespace FishingVisionAssistant.App;

/// <summary>
/// Главное диагностическое окно, объединяющее offline-просмотр, состояние detector и рекомендации controller.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IPanelDetector _panelDetector = new PanelDetector();
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите запись или диагностический кадр",
            Filter = "Поддерживаемые файлы|*.png;*.jpg;*.jpeg;*.bmp;*.mp4;*.mkv;*.avi;*.mov|Изображения|*.png;*.jpg;*.jpeg;*.bmp|Видео|*.mp4;*.mkv;*.avi;*.mov|Все файлы|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!IsImage(dialog.FileName))
        {
            _viewModel.SelectVideoSource(dialog.FileName);
            return;
        }

        try
        {
            _viewModel.BeginImageAnalysis(dialog.FileName);
            var encodedImage = await File.ReadAllBytesAsync(dialog.FileName);
            var result = await Task.Run(() => _panelDetector.Detect(encodedImage));
            _viewModel.ApplyPanelDetection(dialog.FileName, result);
        }
        catch (Exception exception)
        {
            _viewModel.ApplyAnalysisError(exception.Message);
            MessageBox.Show(
                this,
                $"Не удалось проанализировать файл: {exception.Message}",
                "Ошибка анализа",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }
}
