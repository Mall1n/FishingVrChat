using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FishingVisionAssistant.App;

/// <summary>
/// Главное диагностическое окно, объединяющее offline-просмотр, состояние detector и рекомендации controller.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OpenRecording_Click(object sender, RoutedEventArgs e)
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

        try
        {
            _viewModel.SelectOfflineSource(dialog.FileName, TryLoadImage(dialog.FileName));
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Не удалось открыть файл: {exception.Message}",
                "Ошибка открытия",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static BitmapImage? TryLoadImage(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
