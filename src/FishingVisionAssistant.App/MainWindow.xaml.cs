using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FishingVisionAssistant.Capture;
using FishingVisionAssistant.Core;
using Microsoft.Win32;

namespace FishingVisionAssistant.App;

/// <summary>
/// Главное диагностическое окно, объединяющее offline-просмотр, состояние detector и рекомендации controller.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IPanelDetector _panelDetector = new PanelDetector();
    private readonly DispatcherTimer _playbackTimer = new(DispatcherPriority.Render);
    private readonly MainWindowViewModel _viewModel = new();
    private PerformanceStatistics _performanceStatistics = new();
    private VideoAnalysisSession? _videoSession;
    private long _currentFrameIndex;
    private double _playbackSpeed = 1;
    private bool _isFrameTransitionActive;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        Closed += MainWindow_Closed;
    }

    private async void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_isFrameTransitionActive)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Выберите запись или диагностический кадр",
            Filter = "Поддерживаемые файлы|*.png;*.jpg;*.jpeg;*.bmp;*.mp4;*.mkv;*.avi;*.mov|Изображения|*.png;*.jpg;*.jpeg;*.bmp|Видео|*.mp4;*.mkv;*.avi;*.mov|Все файлы|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StopPlayback();
        if (IsImage(dialog.FileName))
        {
            DisposeVideoSession();
            await AnalyzeImageAsync(dialog.FileName);
            return;
        }

        await OpenVideoAsync(dialog.FileName);
    }

    private async Task AnalyzeImageAsync(string path)
    {
        try
        {
            _viewModel.BeginImageAnalysis(path);
            var encodedImage = await File.ReadAllBytesAsync(path);
            var result = await Task.Run(() => _panelDetector.Detect(encodedImage));
            _viewModel.ApplyPanelDetection(path, result);
        }
        catch (Exception exception)
        {
            ShowAnalysisError(exception);
        }
    }

    private async Task OpenVideoAsync(string path)
    {
        DisposeVideoSession();
        _viewModel.BeginVideoOpen(path);

        try
        {
            var session = await Task.Run(
                () => new VideoAnalysisSession(new VideoFrameSource(path), _panelDetector));
            _videoSession = session;
            _performanceStatistics = new PerformanceStatistics();
            _currentFrameIndex = 0;
            _viewModel.InitializeVideo(session.Metadata);
            UpdatePlaybackInterval();
            await ShowVideoFrameAsync(0);
        }
        catch (Exception exception)
        {
            DisposeVideoSession();
            ShowAnalysisError(exception);
        }
    }

    private async Task ShowVideoFrameAsync(long requestedFrameIndex)
    {
        var session = _videoSession;
        if (session is null || _isFrameTransitionActive)
        {
            return;
        }

        var frameIndex = Math.Clamp(requestedFrameIndex, 0, session.Metadata.FrameCount - 1);
        _isFrameTransitionActive = true;
        _viewModel.BeginFrameAnalysis(frameIndex);

        try
        {
            var analysis = await Task.Run(() => session.AnalyzeFrame(frameIndex));
            if (!ReferenceEquals(session, _videoSession))
            {
                return;
            }

            if (!analysis.IsFromCache)
            {
                _performanceStatistics.Add(analysis.ProcessingTime);
            }

            _currentFrameIndex = analysis.FrameIndex;
            _viewModel.ApplyVideoFrame(
                session.Metadata,
                analysis,
                _performanceStatistics.GetSnapshot());
        }
        catch (Exception exception)
        {
            StopPlayback();
            ShowAnalysisError(exception);
        }
        finally
        {
            _isFrameTransitionActive = false;
        }
    }

    private async void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoSession is null || _isFrameTransitionActive)
        {
            return;
        }

        if (_currentFrameIndex >= _videoSession.Metadata.FrameCount - 1)
        {
            StopPlayback();
            return;
        }

        await ShowVideoFrameAsync(_currentFrameIndex + 1);
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        await TogglePlaybackAsync();
    }

    private async Task TogglePlaybackAsync()
    {
        if (_videoSession is null)
        {
            return;
        }

        if (_playbackTimer.IsEnabled)
        {
            StopPlayback();
            return;
        }

        if (_currentFrameIndex >= _videoSession.Metadata.FrameCount - 1)
        {
            await ShowVideoFrameAsync(0);
        }

        _playbackTimer.Start();
        _viewModel.SetPlaying(true);
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_videoSession is null || Keyboard.FocusedElement is ComboBox or ComboBoxItem)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                await TogglePlaybackAsync();
                e.Handled = true;
                break;
            case Key.Left:
                await NavigateRelativeAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -10 : -1);
                e.Handled = true;
                break;
            case Key.Right:
                await NavigateRelativeAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
                e.Handled = true;
                break;
        }
    }

    private async void PreviousTen_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(-10);

    private async void PreviousFrame_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(-1);

    private async void NextFrame_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(1);

    private async void NextTen_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(10);

    private async Task NavigateRelativeAsync(long offset)
    {
        StopPlayback();
        await ShowVideoFrameAsync(_currentFrameIndex + offset);
    }

    private async void Timeline_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_videoSession is null)
        {
            return;
        }

        StopPlayback();
        await ShowVideoFrameAsync((long)Math.Round(TimelineSlider.Value));
    }

    private void PlaybackSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem selectedItem } ||
            selectedItem.Tag is not string tag ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            return;
        }

        _playbackSpeed = speed;
        UpdatePlaybackInterval();
    }

    private void UpdatePlaybackInterval()
    {
        var framesPerSecond = _videoSession?.Metadata.FramesPerSecond ?? 30;
        var intervalMilliseconds = Math.Max(1, 1000 / framesPerSecond / _playbackSpeed);
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(intervalMilliseconds);
    }

    private void StopPlayback()
    {
        _playbackTimer.Stop();
        _viewModel.SetPlaying(false);
    }

    private void DisposeVideoSession()
    {
        StopPlayback();
        _videoSession?.Dispose();
        _videoSession = null;
    }

    private void ShowAnalysisError(Exception exception)
    {
        _viewModel.ApplyAnalysisError(exception.Message);
        MessageBox.Show(
            this,
            $"Не удалось проанализировать источник: {exception.Message}",
            "Ошибка анализа",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => DisposeVideoSession();

    private static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }
}
