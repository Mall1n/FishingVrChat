using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FishingVisionAssistant.Capture;
using FishingVisionAssistant.Core;
using Microsoft.Win32;
using Windows.Graphics.Capture;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace FishingVisionAssistant.App;

/// <summary>
/// Главное диагностическое окно, объединяющее offline-просмотр, состояние detector и рекомендации controller.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly double[] PlaybackSpeeds = [0.25, 0.5, 1, 1.5, 2];
    private static readonly TimeSpan FrameInspectorUpdateInterval = TimeSpan.FromMilliseconds(100);
    private const int MaximumTrainingLogLinesPerUiUpdate = 200;
    private const double DefaultOnnxMinimumConfidence = 0.5;
    private const double DefaultOnnxMinimumAspectRatio = 10;

    private IPanelDetector? _panelDetector;
    private readonly DispatcherTimer _annotationPreviewTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(25)
    };
    private readonly DispatcherTimer _playbackTimer = new(DispatcherPriority.Render);
    private readonly DispatcherTimer _trainingLogUiTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
    private readonly ApplicationSettingsStore _settingsStore = new();
    private readonly ObbDatasetWriter _datasetWriter = new();
    private readonly MlTrainingRunner _mlTrainingRunner = new();
    private readonly MainWindowViewModel _viewModel = new();
    private readonly object _liveUiSync = new();
    private readonly object _trainingLogSync = new();
    private readonly object _trainingLogUiSync = new();
    private readonly SemaphoreSlim _onnxActivationGate = new(1, 1);
    private readonly Queue<TrainingLogLine> _pendingTrainingLogLines = new();
    private ObbAnnotationOverlay? _annotationOverlay;
    private LiveDetectionOverlay? _liveDetectionOverlay;
    private LiveAnalysisSession? _pendingLiveUiSession;
    private LiveFrameAnalysis? _pendingLiveUiFrame;
    private LiveInspectorSnapshot? _pendingLiveInspector;
    private bool _isLiveUiUpdateScheduled;
    private bool _isLiveCaptureStopping;
    private bool _isOnnxTransitionActive;
    private Task? _liveStopTask;
    private long _lastLiveUiUpdateTimestamp;
    private PerformanceStatistics _performanceStatistics = new();
    private LiveInspectorStatistics _liveInspectorStatistics = new();
    private VideoAnalysisSession? _videoSession;
    private LiveAnalysisSession? _liveSession;
    private PanelDetectionResult? _currentDetection;
    private string? _datasetRoot;
    private string? _currentImagePath;
    private string? _lastVideoPath;
    private long _currentFrameIndex;
    private long _lastVideoFrameIndex;
    private byte[]? _annotationFramePng;
    private VideoFrame? _annotationVideoFrame;
    private IReadOnlyList<ObbDatasetExistingSample> _existingAnnotations = [];
    private IReadOnlyList<ObbDatasetTimelineMarker> _timelineAnnotations = [];
    private double _playbackSpeed = 1;
    private int _playbackSpeedIndex = 2;
    private bool _isFrameTransitionActive;
    private bool _isAnnotationSaveActive;
    private bool _isAnnotationPreviewRendering;
    private bool _isRestoringSettings;
    private string? _onnxModelPath;
    private OnnxPanelDetector? _onnxPanelDetector;
    private OnnxExecutionProvider _onnxExecutionProvider = OnnxExecutionProvider.Auto;
    private LivePreviewSettings _livePreviewSettings = LivePreviewSettings.Default;
    private double _onnxMinimumConfidence = DefaultOnnxMinimumConfidence;
    private double _onnxMinimumAspectRatio = DefaultOnnxMinimumAspectRatio;
    private int _annotationPreviewVersion;
    private CancellationTokenSource? _mlTrainingCancellation;
    private TrainingLogWindow? _trainingLogWindow;
    private TrainingLogLine? _pendingTrainingProgressLine;
    private string? _trainingLogPath;
    private string? _trainingResultDirectory;
    private string? _latestTrainingWeightsPath;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _annotationOverlay = new ObbAnnotationOverlay(SourcePreviewImage, AnnotationCanvas);
        _liveDetectionOverlay = new LiveDetectionOverlay(SourcePreviewImage, LiveDetectionCanvas);
        _annotationOverlay.Changed += AnnotationOverlay_Changed;
        UpdateAnnotationControls();
        _annotationPreviewTimer.Tick += AnnotationPreviewTimer_Tick;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _trainingLogUiTimer.Tick += TrainingLogUiTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var settings = _settingsStore.Load();
        RestoreSettings(settings);

        var datasetArgument = arguments
            .FirstOrDefault(argument => argument.StartsWith("--dataset=", StringComparison.OrdinalIgnoreCase));
        if (datasetArgument is not null)
        {
            _datasetRoot = Path.GetFullPath(datasetArgument["--dataset=".Length..]);
            DatasetPathText.Text = _datasetRoot;
        }

        EnsureDatasetStructure();

        await RestoreDetectorAsync();
        var explicitSourcePath = arguments.FirstOrDefault(File.Exists);
        var sourcePath = explicitSourcePath ??
                         (_lastVideoPath is not null && File.Exists(_lastVideoPath) ? _lastVideoPath : null);

        var requestedFrame = arguments
            .FirstOrDefault(argument => argument.StartsWith("--frame=", StringComparison.OrdinalIgnoreCase));
        var frameIndex = requestedFrame is not null &&
                         long.TryParse(requestedFrame["--frame=".Length..], out var parsedFrame)
            ? parsedFrame
            : sourcePath is not null &&
              string.Equals(sourcePath, _lastVideoPath, StringComparison.OrdinalIgnoreCase)
                ? _lastVideoFrameIndex
                : 0;

        if (_panelDetector is null && sourcePath is not null)
        {
            AnnotationStatusText.Text = "Последний источник не открыт: сначала выберите ONNX-модель.";
        }
        else if (sourcePath is not null && IsImage(sourcePath))
        {
            await AnalyzeImageAsync(sourcePath);
        }
        else if (sourcePath is not null)
        {
            await OpenVideoAsync(sourcePath, frameIndex);
        }
        else if (_lastVideoPath is not null)
        {
            AnnotationStatusText.Text = "Последнее видео не найдено. Выберите запись вручную.";
        }

        AnnotationModeCheckBox.IsChecked = settings.IsAnnotationModeEnabled;
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

        await StopLiveCaptureAsync();
        StopPlayback();
        if (IsImage(dialog.FileName))
        {
            DisposeVideoSession();
            await AnalyzeImageAsync(dialog.FileName);
            return;
        }

        await OpenVideoAsync(dialog.FileName);
    }

    private async void LiveCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_isLiveCaptureStopping || _isOnnxTransitionActive)
        {
            return;
        }

        if (_liveSession is not null)
        {
            await StopLiveCaptureAsync();
            return;
        }

        var detector = _panelDetector;
        if (detector is null)
        {
            ShowOnnxModelRequired();
            return;
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            MessageBox.Show(
                this,
                "Windows.Graphics.Capture не поддерживается этой версией Windows.",
                "Live capture недоступен",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var picker = new GraphicsCapturePicker();
            var windowHandle = new WindowInteropHelper(this).Handle;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var item = await picker.PickSingleItemAsync();
            if (item is null)
            {
                return;
            }

            DisposeVideoSession();
            ResetCurrentDetection();
            _currentImagePath = null;
            var livePreviewSettings = GetEffectiveLivePreviewSettings();
            var session = new LiveAnalysisSession(
                new WindowsGraphicsCaptureFrameSource(item),
                detector,
                livePreviewSettings);
            _liveSession = session;
            _performanceStatistics = new PerformanceStatistics();
            _liveInspectorStatistics = new LiveInspectorStatistics();
            _lastLiveUiUpdateTimestamp = 0;
            _viewModel.BeginLiveCapture(session.Descriptor);
            LiveCaptureButton.Content = "Остановить Live capture";
            PauseLiveCaptureButton.Content = "⏸";
            PauseLiveCaptureButton.IsEnabled = true;
            PauseLiveCaptureButton.ToolTip = "Приостановить live capture";
            UpdateAnnotationControls();
            session.Start(
                analysis => QueueLiveFrameForUi(session, analysis),
                exception => Dispatcher.BeginInvoke(() => HandleLiveCaptureError(session, exception)),
                () => Dispatcher.BeginInvoke(() => HandleLiveCaptureCompleted(session)));
        }
        catch (Exception exception)
        {
            await StopLiveCaptureAsync();
            ShowAnalysisError(exception);
        }
    }

    private void PauseLiveCapture_Click(object sender, RoutedEventArgs e)
    {
        var session = _liveSession;
        if (session is null)
        {
            return;
        }

        if (session.IsPaused)
        {
            session.Resume();
            PauseLiveCaptureButton.Content = "⏸";
            PauseLiveCaptureButton.ToolTip = "Приостановить live capture";
            _viewModel.SetLiveCapturePaused(false);
            return;
        }

        session.Pause();
        PauseLiveCaptureButton.Content = "▶";
        PauseLiveCaptureButton.ToolTip = "Продолжить live capture";
        _viewModel.SetLiveCapturePaused(true);
    }

    private async Task AnalyzeImageAsync(string path)
    {
        var detector = _panelDetector;
        if (detector is null)
        {
            ShowOnnxModelRequired();
            return;
        }

        try
        {
            ResetCurrentDetection();
            _currentImagePath = path;
            _viewModel.BeginImageAnalysis(path);
            var encodedImage = await File.ReadAllBytesAsync(path);
            _annotationFramePng = FramePngEncoder.NormalizeEncodedImage(encodedImage);
            var result = await Task.Run(() => detector.Detect(encodedImage));
            _viewModel.ApplyPanelDetection(path, result);
            SetCurrentDetection(result);
            await RefreshExistingAnnotationAsync();
        }
        catch (Exception exception)
        {
            ShowAnalysisError(exception);
        }
    }

    private async Task OpenVideoAsync(string path, long initialFrameIndex = 0)
    {
        var detector = _panelDetector;
        if (detector is null)
        {
            ShowOnnxModelRequired();
            return;
        }

        DisposeVideoSession();
        ResetCurrentDetection();
        _currentImagePath = null;
        _viewModel.BeginVideoOpen(path);

        try
        {
            var session = await Task.Run(
                () => new VideoAnalysisSession(new VideoFrameSource(path), detector));
            _videoSession = session;
            _lastVideoPath = Path.GetFullPath(path);
            _performanceStatistics = new PerformanceStatistics();
            _currentFrameIndex = Math.Clamp(initialFrameIndex, 0, session.Metadata.FrameCount - 1);
            _lastVideoFrameIndex = _currentFrameIndex;
            _viewModel.InitializeVideo(session.Metadata);
            UpdatePlaybackInterval();
            await RefreshTimelineAnnotationsAsync();
            await ShowVideoFrameAsync(_currentFrameIndex);
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
        ResetCurrentDetection();
        _viewModel.BeginFrameAnalysis(frameIndex);

        try
        {
            var includeSourceFrame = AnnotationModeCheckBox.IsChecked == true;
            var analysisResult = await Task.Run(
                () => session.AnalyzeFrame(frameIndex, includeSourceFrame));
            if (!ReferenceEquals(session, _videoSession))
            {
                return;
            }

            if (analysisResult is null)
            {
                _currentFrameIndex = frameIndex;
                _lastVideoFrameIndex = frameIndex;
                _viewModel.ApplyFrameDecodeFailure(session.Metadata, frameIndex);
                return;
            }

            var analysis = analysisResult.Analysis;

            if (!analysis.IsFromCache)
            {
                _performanceStatistics.Add(analysis.ProcessingTime);
            }

            _currentFrameIndex = analysis.FrameIndex;
            _lastVideoFrameIndex = analysis.FrameIndex;
            _viewModel.ApplyVideoFrame(
                session.Metadata,
                analysis,
                _performanceStatistics.GetSnapshot());
            if (AnnotationModeCheckBox.IsChecked == true)
            {
                _annotationVideoFrame = analysisResult.SourceFrame ??
                    await Task.Run(() => session.ReadFrame(analysis.FrameIndex));
            }

            SetCurrentDetection(analysis.PanelDetection);
            await RefreshExistingAnnotationAsync();
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
        if (AnnotationModeCheckBox.IsChecked == true && Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    await SavePositiveAnnotationAsync();
                    e.Handled = true;
                    return;
                case Key.E:
                    BeginCorrection();
                    e.Handled = true;
                    return;
                case Key.M:
                    BeginManualAnnotation();
                    e.Handled = true;
                    return;
                case Key.N:
                    await SaveAnnotationAsync(ObbAnnotationKind.Negative);
                    e.Handled = true;
                    return;
                case Key.Delete:
                    await DeleteCurrentAnnotationAsync();
                    e.Handled = true;
                    return;
            }
        }

        if (_videoSession is null)
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
                await NavigateRelativeAsync(
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : -GetSecondFrameOffset());
                e.Handled = true;
                break;
            case Key.Right:
                await NavigateRelativeAsync(
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : GetSecondFrameOffset());
                e.Handled = true;
                break;
        }
    }

    private async void PreviousSecond_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(-GetSecondFrameOffset());

    private async void PreviousFrame_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(-1);

    private async void NextFrame_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(1);

    private async void NextSecond_Click(object sender, RoutedEventArgs e) =>
        await NavigateRelativeAsync(GetSecondFrameOffset());

    private async void PreviousAnnotation_Click(object sender, RoutedEventArgs e) =>
        await NavigateToAnnotationAsync(previous: true);

    private async void NextAnnotation_Click(object sender, RoutedEventArgs e) =>
        await NavigateToAnnotationAsync(previous: false);

    private long GetSecondFrameOffset()
    {
        var framesPerSecond = _videoSession?.Metadata.FramesPerSecond ?? 60;
        return Math.Max(1, (long)Math.Round(framesPerSecond, MidpointRounding.AwayFromZero));
    }

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

    private void TimelineMarkersCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RenderTimelineMarkers();

    private void PlaybackSpeed_Click(object sender, RoutedEventArgs e)
    {
        _playbackSpeedIndex = (_playbackSpeedIndex + 1) % PlaybackSpeeds.Length;
        _playbackSpeed = PlaybackSpeeds[_playbackSpeedIndex];
        _viewModel.SetPlaybackSpeed(_playbackSpeed);
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
        _timelineAnnotations = [];
        RenderTimelineMarkers();
        UpdateAnnotationControls();
    }

    private void ApplyLiveFrame(
        LiveAnalysisSession session,
        LiveFrameAnalysis analysis,
        LiveInspectorSnapshot inspector,
        bool updateInspector)
    {
        if (!ReferenceEquals(session, _liveSession) || session.IsPaused)
        {
            return;
        }

        var previewSettings = GetEffectiveLivePreviewSettings();
        _viewModel.ApplyLiveFrame(
            analysis,
            inspector,
            previewSettings,
            updateInspector);
        if (analysis.SourcePreviewFrame is not null && previewSettings.UpdateSourcePreview)
        {
            if (analysis.PanelDetection.IsDetected)
            {
                _liveDetectionOverlay?.Show(analysis.PanelDetection.Corners);
            }
            else
            {
                _liveDetectionOverlay?.Clear();
            }
        }
    }

    private void QueueLiveFrameForUi(LiveAnalysisSession session, LiveFrameAnalysis analysis)
    {
        var shouldSchedule = false;
        lock (_liveUiSync)
        {
            if (!ReferenceEquals(session, _liveSession))
            {
                return;
            }

            var inspector = _liveInspectorStatistics.Add(analysis);
            _pendingLiveUiSession = session;
            _pendingLiveUiFrame = analysis;
            _pendingLiveInspector = inspector;
            if (!_isLiveUiUpdateScheduled)
            {
                _isLiveUiUpdateScheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ApplyPendingLiveFrame));
        }
    }

    private void ApplyPendingLiveFrame()
    {
        LiveAnalysisSession? session;
        LiveFrameAnalysis? analysis;
        LiveInspectorSnapshot? inspector;
        var updateInspector = false;
        lock (_liveUiSync)
        {
            session = _pendingLiveUiSession;
            analysis = _pendingLiveUiFrame;
            inspector = _pendingLiveInspector;
            _pendingLiveUiSession = null;
            _pendingLiveUiFrame = null;
            _pendingLiveInspector = null;
            _isLiveUiUpdateScheduled = false;
            var now = Stopwatch.GetTimestamp();
            updateInspector = _lastLiveUiUpdateTimestamp == 0 ||
                Stopwatch.GetElapsedTime(_lastLiveUiUpdateTimestamp, now) >= FrameInspectorUpdateInterval;
            if (updateInspector)
            {
                _lastLiveUiUpdateTimestamp = now;
            }
        }

        if (session is not null && analysis is not null && inspector is not null)
        {
            ApplyLiveFrame(session, analysis, inspector, updateInspector);
        }
    }

    private void ClearPendingLiveFrames(LiveAnalysisSession session)
    {
        lock (_liveUiSync)
        {
            if (!ReferenceEquals(_pendingLiveUiSession, session))
            {
                return;
            }

            _pendingLiveUiSession = null;
            _pendingLiveUiFrame = null;
            _pendingLiveInspector = null;
            _lastLiveUiUpdateTimestamp = 0;
        }
    }

    private async void HandleLiveCaptureError(LiveAnalysisSession session, Exception exception)
    {
        if (!ReferenceEquals(session, _liveSession))
        {
            return;
        }

        await StopLiveCaptureAsync();
        ShowAnalysisError(exception);
    }

    private async void HandleLiveCaptureCompleted(LiveAnalysisSession session)
    {
        if (!ReferenceEquals(session, _liveSession))
        {
            return;
        }

        await StopLiveCaptureAsync("Live capture завершён выбранным источником");
    }

    private async Task StopLiveCaptureAsync(string status = "Live capture остановлен")
    {
        if (_liveStopTask is not null)
        {
            await _liveStopTask;
            return;
        }

        var session = _liveSession;
        if (session is null)
        {
            return;
        }

        _liveStopTask = StopLiveCaptureCoreAsync(session, status);
        try
        {
            await _liveStopTask;
        }
        finally
        {
            _liveStopTask = null;
        }
    }

    private async Task StopLiveCaptureCoreAsync(LiveAnalysisSession session, string status)
    {
        _liveSession = null;
        _isLiveCaptureStopping = true;
        ClearPendingLiveFrames(session);
        LiveCaptureButton.Content = "Запустить Live capture";
        LiveCaptureButton.IsEnabled = false;
        PauseLiveCaptureButton.Content = "▶";
        PauseLiveCaptureButton.IsEnabled = false;
        PauseLiveCaptureButton.ToolTip = "Live capture не запущен";
        try
        {
            await session.DisposeAsync();
            _viewModel.EndLiveCapture(status);
            UpdateAnnotationControls();
        }
        finally
        {
            _isLiveCaptureStopping = false;
            LiveCaptureButton.IsEnabled = !_isOnnxTransitionActive;
        }
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

    private void ShowOnnxModelRequired()
    {
        OnnxDetectorStatusText.Text = "Сначала выберите существующую ONNX-модель.";
        MessageBox.Show(
            this,
            "Для анализа требуется ONNX-модель. Выберите её в блоке «ONNX detector».",
            "ONNX-модель не загружена",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _mlTrainingCancellation?.Cancel();
        _mlTrainingCancellation?.Dispose();
        _mlTrainingCancellation = null;
        _settingsStore.Save(CaptureSettings());
        _annotationPreviewTimer.Stop();
        _trainingLogUiTimer.Stop();
        var liveSession = _liveSession;
        _liveSession = null;
        liveSession?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        DisposeVideoSession();
        _onnxPanelDetector?.Dispose();
        _onnxPanelDetector = null;
    }

    private void RestoreSettings(ApplicationSettings settings)
    {
        _isRestoringSettings = true;
        try
        {
            _lastVideoPath = settings.LastVideoPath;
            _lastVideoFrameIndex = Math.Max(0, settings.LastVideoFrameIndex);
            _datasetRoot = settings.DatasetRoot;
            DatasetPathText.Text = string.IsNullOrWhiteSpace(_datasetRoot) ? "Папка не выбрана" : _datasetRoot;
            _onnxModelPath = string.IsNullOrWhiteSpace(settings.OnnxModelPath)
                ? FindDefaultOnnxModelPath()
                : settings.OnnxModelPath;
            UpdateOnnxModelPathText();
            _onnxMinimumConfidence = Math.Clamp(settings.OnnxMinimumConfidence, 0.05, 0.95);
            _onnxMinimumAspectRatio = Math.Clamp(settings.OnnxMinimumAspectRatio, 1, 30);
            _onnxExecutionProvider = Enum.IsDefined(settings.OnnxExecutionProvider)
                ? settings.OnnxExecutionProvider
                : OnnxExecutionProvider.Auto;
            SelectOnnxExecutionProvider(_onnxExecutionProvider);
            UpdateOnnxGateSummary();
            _livePreviewSettings = new LivePreviewSettings(
                settings.IsSourcePreviewEnabled,
                settings.IsRectifiedPreviewEnabled,
                settings.IsOnnxDiagnosticPreviewEnabled,
                NormalizeLivePreviewInterval(settings.LivePreviewRefreshEveryNFrames));
            PauseAllPreviewsCheckBox.IsChecked = settings.IsAllPreviewsPaused;
            SourcePreviewCheckBox.IsChecked = _livePreviewSettings.UpdateSourcePreview;
            RectifiedPreviewCheckBox.IsChecked = _livePreviewSettings.UpdateRectifiedPreview;
            OnnxDiagnosticPreviewCheckBox.IsChecked = _livePreviewSettings.UpdateOnnxDiagnosticPreview;
            SelectLivePreviewInterval(_livePreviewSettings.RefreshEveryNFrames);
            UpdateLivePreviewSettingsSummary();

            var split = Enum.IsDefined(typeof(DatasetSplit), settings.DatasetSplit)
                ? settings.DatasetSplit
                : DatasetSplit.Train;
            SelectDatasetSplit(split);

            _playbackSpeedIndex = Math.Clamp(settings.PlaybackSpeedIndex, 0, PlaybackSpeeds.Length - 1);
            _playbackSpeed = PlaybackSpeeds[_playbackSpeedIndex];
            _viewModel.SetPlaybackSpeed(_playbackSpeed);
        }
        finally
        {
            _isRestoringSettings = false;
        }
    }

    private ApplicationSettings CaptureSettings() => new()
    {
        LastVideoPath = _lastVideoPath,
        LastVideoFrameIndex = _lastVideoFrameIndex,
        DatasetRoot = _datasetRoot,
        DatasetSplit = GetSelectedSplit(),
        IsAnnotationModeEnabled = AnnotationModeCheckBox.IsChecked == true,
        OnnxModelPath = _onnxModelPath,
        OnnxMinimumConfidence = _onnxMinimumConfidence,
        OnnxMinimumAspectRatio = _onnxMinimumAspectRatio,
        OnnxExecutionProvider = _onnxExecutionProvider,
        IsSourcePreviewEnabled = _livePreviewSettings.UpdateSourcePreview,
        IsRectifiedPreviewEnabled = _livePreviewSettings.UpdateRectifiedPreview,
        IsOnnxDiagnosticPreviewEnabled = _livePreviewSettings.UpdateOnnxDiagnosticPreview,
        IsAllPreviewsPaused = PauseAllPreviewsCheckBox.IsChecked == true,
        LivePreviewRefreshEveryNFrames = _livePreviewSettings.RefreshEveryNFrames,
        PlaybackSpeedIndex = _playbackSpeedIndex
    };

    private void SelectDatasetSplit(DatasetSplit split)
    {
        var item = DatasetSplitComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Tag?.ToString(), split.ToString(), StringComparison.OrdinalIgnoreCase));
        DatasetSplitComboBox.SelectedItem = item ?? DatasetSplitComboBox.Items[0];
    }

    private async Task RestoreDetectorAsync()
    {
        if (string.IsNullOrWhiteSpace(_onnxModelPath) || !File.Exists(_onnxModelPath))
        {
            _panelDetector = null;
            OnnxDetectorStatusText.Text = "ONNX-модель не найдена. Выберите существующий .onnx файл.";
            return;
        }

        await ActivateOnnxDetectorAsync(reanalyzeCurrentSource: false);
    }

    private async void ChooseOnnxModel_Click(object sender, RoutedEventArgs e)
    {
        if (_isFrameTransitionActive)
        {
            OnnxDetectorStatusText.Text = "Дождитесь завершения анализа текущего кадра.";
            return;
        }

        if (IsOnnxActivationInProgress())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Выберите YOLO OBB ONNX-модель",
            Filter = "ONNX-модели|*.onnx|Все файлы|*.*",
            InitialDirectory = _onnxModelPath is null ? null : Path.GetDirectoryName(_onnxModelPath)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _onnxModelPath = Path.GetFullPath(dialog.FileName);
        UpdateOnnxModelPathText();
        await ActivateOnnxDetectorAsync(reanalyzeCurrentSource: true);
    }

    private void UpdateOnnxModelPathText()
    {
        OnnxModelNameHeaderText.Text = string.IsNullOrWhiteSpace(_onnxModelPath)
            ? "модель не выбрана"
            : Path.GetFileName(_onnxModelPath);
        OnnxModelNameHeaderText.ToolTip = _onnxModelPath ?? "Модель не выбрана";
    }

    private async void ConfigureOnnxGate_Click(object sender, RoutedEventArgs e)
    {
        if (_isFrameTransitionActive)
        {
            OnnxDetectorStatusText.Text = "Дождитесь завершения анализа текущего кадра.";
            return;
        }

        if (IsOnnxActivationInProgress())
        {
            return;
        }

        var dialog = new OnnxGateSettingsWindow(_onnxMinimumConfidence, _onnxMinimumAspectRatio)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _onnxMinimumConfidence = dialog.MinimumConfidence;
        _onnxMinimumAspectRatio = dialog.MinimumAspectRatio;
        UpdateOnnxGateSummary();
        if (_onnxPanelDetector is not null)
        {
            await ActivateOnnxDetectorAsync(reanalyzeCurrentSource: true);
        }
    }

    private async void OnnxBackend_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isRestoringSettings)
        {
            return;
        }

        var selectedProvider = GetSelectedOnnxExecutionProvider();
        if (selectedProvider == _onnxExecutionProvider)
        {
            return;
        }

        if (_isFrameTransitionActive)
        {
            SelectOnnxExecutionProvider(_onnxExecutionProvider);
            OnnxDetectorStatusText.Text = "Дождитесь завершения анализа текущего кадра.";
            return;
        }

        if (IsOnnxActivationInProgress())
        {
            SelectOnnxExecutionProvider(_onnxExecutionProvider);
            return;
        }

        _onnxExecutionProvider = selectedProvider;
        UpdateOnnxGateSummary();
        if (!string.IsNullOrWhiteSpace(_onnxModelPath) && File.Exists(_onnxModelPath))
        {
            await ActivateOnnxDetectorAsync(reanalyzeCurrentSource: true);
        }
    }

    private void LivePreviewSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isRestoringSettings)
        {
            return;
        }

        ApplyLivePreviewSettingsFromControls();
    }

    private void LivePreviewInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isRestoringSettings)
        {
            return;
        }

        ApplyLivePreviewSettingsFromControls();
    }

    private void ApplyLivePreviewSettingsFromControls()
    {
        _livePreviewSettings = new LivePreviewSettings(
            SourcePreviewCheckBox.IsChecked == true,
            RectifiedPreviewCheckBox.IsChecked == true,
            OnnxDiagnosticPreviewCheckBox.IsChecked == true,
            GetSelectedLivePreviewInterval());
        _liveSession?.UpdatePreviewSettings(GetEffectiveLivePreviewSettings());
        UpdateLivePreviewSettingsSummary();
    }

    private LivePreviewSettings GetEffectiveLivePreviewSettings()
    {
        return PauseAllPreviewsCheckBox.IsChecked == true
            ? _livePreviewSettings with
            {
                UpdateSourcePreview = false,
                UpdateRectifiedPreview = false,
                UpdateOnnxDiagnosticPreview = false
            }
            : _livePreviewSettings;
    }

    private void UpdateLivePreviewSettingsSummary()
    {
        if (PauseAllPreviewsCheckBox.IsChecked == true)
        {
            PreviewSettingsSummaryText.Text = "Все preview заморожены на последних изображениях.";
            return;
        }

        var activePreviews = new List<string>(3);
        if (_livePreviewSettings.UpdateSourcePreview)
        {
            activePreviews.Add("исходный кадр");
        }

        if (_livePreviewSettings.UpdateRectifiedPreview)
        {
            activePreviews.Add("шкала");
        }

        if (_livePreviewSettings.UpdateOnnxDiagnosticPreview)
        {
            activePreviews.Add("ONNX");
        }

        var activeText = activePreviews.Count == 0
            ? "Все preview заморожены"
            : $"Обновляются: {string.Join(", ", activePreviews)}";
        PreviewSettingsSummaryText.Text =
            $"{activeText} · {FormatLivePreviewInterval(_livePreviewSettings.RefreshEveryNFrames)}.";
    }

    private void SelectLivePreviewInterval(int frameInterval)
    {
        var item = LivePreviewIntervalComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => candidate.Tag?.ToString() == frameInterval.ToString());
        LivePreviewIntervalComboBox.SelectedItem = item ?? LivePreviewIntervalComboBox.Items[2];
    }

    private int GetSelectedLivePreviewInterval()
    {
        return LivePreviewIntervalComboBox.SelectedItem is ComboBoxItem item &&
               int.TryParse(item.Tag?.ToString(), out var interval)
            ? NormalizeLivePreviewInterval(interval)
            : LivePreviewSettings.Default.RefreshEveryNFrames;
    }

    private static int NormalizeLivePreviewInterval(int interval) => interval is 1 or 2 or 4 or 8
        ? interval
        : LivePreviewSettings.Default.RefreshEveryNFrames;

    private static string FormatLivePreviewInterval(int interval) => interval switch
    {
        1 => "каждый обработанный кадр",
        2 => "каждый 2-й обработанный кадр",
        4 => "каждый 4-й обработанный кадр",
        8 => "каждый 8-й обработанный кадр",
        _ => $"каждый {interval}-й обработанный кадр"
    };

    private async Task ActivateOnnxDetectorAsync(bool reanalyzeCurrentSource)
    {
        if (string.IsNullOrWhiteSpace(_onnxModelPath) || !File.Exists(_onnxModelPath))
        {
            OnnxDetectorStatusText.Text = "Выберите существующий .onnx файл.";
            return;
        }

        await _onnxActivationGate.WaitAsync();
        SetOnnxTransitionControlsEnabled(false);
        try
        {
            StopPlayback();
            OnnxDetectorStatusText.ToolTip = null;
            OnnxDetectorStatusText.Text = "Останавливаю live и освобождаю предыдущую ONNX-модель…";

            await StopLiveCaptureAsync("Live capture остановлен после смены detector");
            var previous = _onnxPanelDetector;
            _onnxPanelDetector = null;
            if (ReferenceEquals(_panelDetector, previous))
            {
                _panelDetector = null;
            }

            previous?.Dispose();

            OnnxDetectorStatusText.Text = "Загрузка модели через Windows ML self-contained…";
            var modelPath = _onnxModelPath;
            var detector = await Task.Run(() => new OnnxPanelDetector(new OnnxPanelDetectorOptions
            {
                ModelPath = modelPath,
                MinimumConfidence = _onnxMinimumConfidence,
                MinimumAspectRatio = _onnxMinimumAspectRatio,
                ExecutionProvider = _onnxExecutionProvider
            }));

            _onnxPanelDetector = detector;
            await ReplaceActiveDetectorAsync(detector, reanalyzeCurrentSource);
            OnnxDetectorStatusText.Text = detector.FallbackReason is null
                ? $"Модель загружена · Windows ML self-contained · {detector.ProviderName} · {detector.InputSize}."
                : "Модель загружена · Windows ML self-contained · CPU · " +
                  "Auto fallback: DirectML недоступен.";
            OnnxDetectorStatusText.ToolTip = detector.FallbackReason;
            UpdateOnnxGateSummary();
        }
        catch (Exception exception)
        {
            OnnxDetectorStatusText.Text = $"ONNX не загружен: {exception.Message}";
            OnnxDetectorStatusText.ToolTip = exception.ToString();
            MessageBox.Show(
                this,
                $"Не удалось загрузить ONNX detector: {exception.Message}",
                "Ошибка ONNX detector",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetOnnxTransitionControlsEnabled(true);
            _onnxActivationGate.Release();
        }
    }

    private async Task ReplaceActiveDetectorAsync(IPanelDetector detector, bool reanalyzeCurrentSource)
    {
        await StopLiveCaptureAsync("Live capture остановлен после смены detector");
        _panelDetector = detector;
        if (_videoSession is not null)
        {
            _videoSession.UpdateDetector(detector);
            _performanceStatistics = new PerformanceStatistics();
            if (reanalyzeCurrentSource)
            {
                await ShowVideoFrameAsync(_currentFrameIndex);
            }

            return;
        }

        if (reanalyzeCurrentSource && _currentImagePath is not null)
        {
            await AnalyzeImageAsync(_currentImagePath);
        }
    }

    private bool IsOnnxActivationInProgress() => _onnxActivationGate.CurrentCount == 0;

    private void SetOnnxTransitionControlsEnabled(bool isEnabled)
    {
        _isOnnxTransitionActive = !isEnabled;
        ChooseOnnxModelButton.IsEnabled = isEnabled;
        OnnxBackendComboBox.IsEnabled = isEnabled;
        ConfigureOnnxGateButton.IsEnabled = isEnabled;
        LiveCaptureButton.IsEnabled = isEnabled && !_isLiveCaptureStopping;
    }

    private void UpdateOnnxGateSummary()
    {
        var requestedProvider = FormatOnnxExecutionProvider(_onnxExecutionProvider);
        var activeProvider = _onnxPanelDetector is null
            ? string.Empty
            : $", активен: {_onnxPanelDetector.ProviderName}";
        OnnxGateSummaryText.Text =
            $"Gate: confidence ≥ {_onnxMinimumConfidence:P0}, " +
            $"aspect ratio ≥ {_onnxMinimumAspectRatio:F1}. " +
            $"Backend: {requestedProvider}{activeProvider}.";
    }

    private void SelectOnnxExecutionProvider(OnnxExecutionProvider provider)
    {
        var item = OnnxBackendComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Tag?.ToString(), provider.ToString(), StringComparison.OrdinalIgnoreCase));
        OnnxBackendComboBox.SelectedItem = item ?? OnnxBackendComboBox.Items[0];
    }

    private OnnxExecutionProvider GetSelectedOnnxExecutionProvider()
    {
        return OnnxBackendComboBox.SelectedItem is ComboBoxItem item &&
               Enum.TryParse<OnnxExecutionProvider>(item.Tag?.ToString(), ignoreCase: true, out var provider)
            ? provider
            : OnnxExecutionProvider.Auto;
    }

    private static string FormatOnnxExecutionProvider(OnnxExecutionProvider provider) => provider switch
    {
        OnnxExecutionProvider.Auto => "Auto (DirectML → CPU)",
        OnnxExecutionProvider.DirectMl => "DirectML",
        OnnxExecutionProvider.Cpu => "CPU",
        _ => provider.ToString()
    };

    private static string? FindDefaultOnnxModelPath()
    {
        var roots = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; directory is not null && depth < 7; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "artifacts",
                    "models",
                    "fishing-panel-obb.onnx");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async void AnnotationMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_annotationOverlay is null)
        {
            return;
        }

        StopPlayback();
        if (AnnotationModeCheckBox.IsChecked == true)
        {
            try
            {
                await EnsureAnnotationFrameAsync();
            }
            catch (Exception exception)
            {
                AnnotationStatusText.Text = $"Не удалось подготовить OBB preview: {exception.Message}";
                return;
            }

            if (AnnotationModeCheckBox.IsChecked != true)
            {
                _annotationFramePng = null;
                _annotationVideoFrame = null;
                return;
            }

            RefreshAnnotationSuggestion();
            UpdateAnnotationInstruction();
        }
        else
        {
            _annotationFramePng = null;
            _annotationVideoFrame = null;
            _annotationOverlay.Clear();
            _viewModel.SetTrainingBoundsPreview(null);
            AnnotationStatusText.Text = "Режим разметки выключен.";
        }

        UpdateAnnotationControls();
    }

    private async void ChooseDatasetFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите корневую папку OBB dataset",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _datasetRoot = dialog.FolderName;
        DatasetPathText.Text = _datasetRoot;
        if (!EnsureDatasetStructure())
        {
            return;
        }

        await RefreshExistingAnnotationAsync();
        await RefreshTimelineAnnotationsAsync();
        UpdateAnnotationControls();
    }

    private async void CheckDataset_Click(object sender, RoutedEventArgs e) =>
        await RunMlCommandAsync(isTraining: false);

    private async void StartTraining_Click(object sender, RoutedEventArgs e) =>
        await RunMlCommandAsync(isTraining: true);

    private async void ExportOnnx_Click(object sender, RoutedEventArgs e)
    {
        if (_mlTrainingCancellation is not null)
        {
            return;
        }

        var mlPaths = FindMlPaths();
        if (mlPaths is null)
        {
            TrainingStatusText.Text = "Не найдены .venv или ml\\fishing_obb.py рядом с проектом.";
            return;
        }

        var weightsPath = _latestTrainingWeightsPath;
        if (string.IsNullOrWhiteSpace(weightsPath) || !File.Exists(weightsPath))
        {
            var weightsDialog = new OpenFileDialog
            {
                Title = "Выберите checkpoint для экспорта",
                Filter = "PyTorch checkpoint|*.pt",
                InitialDirectory = Path.Combine(mlPaths.RepositoryRoot, "artifacts", "ml")
            };
            if (weightsDialog.ShowDialog(this) != true)
            {
                return;
            }

            weightsPath = weightsDialog.FileName;
        }

        if (!TryGetOnnxExportImageSize(out var imageSize))
        {
            return;
        }

        var modelsDirectory = Path.Combine(mlPaths.RepositoryRoot, "artifacts", "models");
        Directory.CreateDirectory(modelsDirectory);
        var runDirectory = Directory.GetParent(weightsPath)?.Parent;
        var suggestedName =
            $"{runDirectory?.Name ?? Path.GetFileNameWithoutExtension(weightsPath)}-{imageSize}.onnx";
        var outputDialog = new SaveFileDialog
        {
            Title = "Сохранить ONNX-модель",
            Filter = "ONNX model|*.onnx",
            DefaultExt = ".onnx",
            AddExtension = true,
            InitialDirectory = modelsDirectory,
            FileName = suggestedName
        };
        if (outputDialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunOnnxExportAsync(mlPaths, weightsPath, outputDialog.FileName, imageSize);
    }

    private void StopTraining_Click(object sender, RoutedEventArgs e)
    {
        if (_mlTrainingCancellation is null)
        {
            return;
        }

        TrainingStatusText.Text = "Останавливаю ML-процесс…";
        _mlTrainingCancellation.Cancel();
    }

    private async Task RunMlCommandAsync(bool isTraining)
    {
        if (_mlTrainingCancellation is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_datasetRoot) || !Directory.Exists(_datasetRoot))
        {
            TrainingStatusText.Text = "Сначала выберите существующую папку dataset в блоке OBB-разметки.";
            return;
        }

        var mlPaths = FindMlPaths();
        if (mlPaths is null)
        {
            TrainingStatusText.Text = "Не найдены .venv или ml\\fishing_obb.py рядом с проектом.";
            return;
        }

        var arguments = new List<string> { isTraining ? "train" : "check", "--dataset", _datasetRoot };
        var runVersion = isTraining ? GetNextTrainingVersion(mlPaths.RepositoryRoot) : (int?)null;
        var runName = runVersion is null ? null : $"fishing-panel-obb-{runVersion}";
        if (isTraining)
        {
            if (!TryGetTrainingOptions(out var epochs, out var patience, out var batch))
            {
                return;
            }

            arguments.AddRange(
            [
                "--epochs", epochs.ToString(),
                "--patience", patience.ToString(),
                "--imgsz", "1024",
                "--batch", batch.ToString(),
                "--device", GetSelectedTrainingDevice(),
                "--model", GetSelectedTrainingModel(),
                "--project", Path.Combine(mlPaths.RepositoryRoot, "artifacts", "ml"),
                "--name", runName!,
                "--statistics-number", runVersion!.Value.ToString()
            ]);
        }

        _mlTrainingCancellation = new CancellationTokenSource();
        CreateTrainingLog(mlPaths.RepositoryRoot, isTraining ? "train" : "check", runName);
        StartTrainingLogUiUpdates();
        UpdateTrainingControls(isRunning: true);
        TrainingLastLogText.Text = "Подготавливаю запуск ML-процесса…";
        AppendTrainingLog($"> {Path.GetFileName(mlPaths.PythonPath)} ml\\fishing_obb.py {string.Join(' ', arguments)}");
        TrainingStatusText.Text = isTraining ? "Идёт обучение модели…" : "Проверяю dataset…";
        try
        {
            var exitCode = await _mlTrainingRunner.RunAsync(
                mlPaths.PythonPath,
                mlPaths.ScriptPath,
                mlPaths.RepositoryRoot,
                arguments,
                AppendTrainingLog,
                _mlTrainingCancellation.Token);
            var action = isTraining ? "Обучение" : "Проверка dataset";
            OpenTrainingResultFolderButton.IsEnabled =
                exitCode == 0 && _trainingResultDirectory is not null && Directory.Exists(_trainingResultDirectory);
            _latestTrainingWeightsPath = exitCode == 0 && _trainingResultDirectory is not null
                ? Path.Combine(_trainingResultDirectory, "weights", "best.pt")
                : null;
            TrainingStatusText.Text = exitCode == 0
                ? $"{action} завершено."
                : $"{action} завершено с кодом {exitCode}. Проверьте журнал.";
        }
        catch (Exception exception)
        {
            AppendTrainingLog(exception.ToString());
            TrainingStatusText.Text = $"Не удалось запустить ML-процесс: {exception.Message}";
        }
        finally
        {
            StopTrainingLogUiUpdates();
            _mlTrainingCancellation?.Dispose();
            _mlTrainingCancellation = null;
            UpdateTrainingControls(isRunning: false);
        }
    }

    private async Task RunOnnxExportAsync(MlPaths mlPaths, string weightsPath, string outputPath, int imageSize)
    {
        var arguments = new List<string>
        {
            "export",
            "--weights", weightsPath,
            "--output", outputPath,
            "--imgsz", imageSize.ToString(),
            "--device", GetSelectedTrainingDevice()
        };

        _mlTrainingCancellation = new CancellationTokenSource();
        CreateTrainingLog(mlPaths.RepositoryRoot, "export", runName: null);
        StartTrainingLogUiUpdates();
        UpdateTrainingControls(isRunning: true);
        TrainingLastLogText.Text = "Подготавливаю экспорт ONNX…";
        AppendTrainingLog($"> {Path.GetFileName(mlPaths.PythonPath)} ml\\fishing_obb.py {string.Join(' ', arguments)}");
        TrainingStatusText.Text = "Экспортирую ONNX-модель…";
        try
        {
            var exitCode = await _mlTrainingRunner.RunAsync(
                mlPaths.PythonPath,
                mlPaths.ScriptPath,
                mlPaths.RepositoryRoot,
                arguments,
                AppendTrainingLog,
                _mlTrainingCancellation.Token);
            _trainingResultDirectory = Path.GetDirectoryName(outputPath);
            OpenTrainingResultFolderButton.IsEnabled =
                exitCode == 0 && _trainingResultDirectory is not null && Directory.Exists(_trainingResultDirectory);
            TrainingStatusText.Text = exitCode == 0
                ? $"ONNX сохранена: {outputPath}"
                : $"Экспорт ONNX завершён с кодом {exitCode}. Проверьте журнал.";
        }
        catch (Exception exception)
        {
            AppendTrainingLog(exception.ToString());
            TrainingStatusText.Text = $"Не удалось экспортировать ONNX: {exception.Message}";
        }
        finally
        {
            StopTrainingLogUiUpdates();
            _mlTrainingCancellation?.Dispose();
            _mlTrainingCancellation = null;
            UpdateTrainingControls(isRunning: false);
        }
    }

    private bool TryGetTrainingOptions(out int epochs, out int patience, out int batch)
    {
        epochs = 0;
        patience = 0;
        batch = 0;
        if (!int.TryParse(TrainingEpochsTextBox.Text, out epochs) || epochs <= 0)
        {
            TrainingStatusText.Text = "Число эпох должно быть положительным целым числом.";
            return false;
        }

        if (!int.TryParse(TrainingPatienceTextBox.Text, out patience) || patience <= 0)
        {
            TrainingStatusText.Text = "Patience должно быть положительным целым числом.";
            return false;
        }

        if (!int.TryParse(TrainingBatchTextBox.Text, out batch) || batch == 0 || batch < -1)
        {
            TrainingStatusText.Text = "Batch должен быть -1 для авто-режима или положительным целым числом.";
            return false;
        }

        return true;
    }

    private string GetSelectedTrainingDevice() =>
        (TrainingDeviceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

    private string GetSelectedTrainingModel() =>
        (TrainingModelComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "yolo26n-obb.pt";

    private bool TryGetOnnxExportImageSize(out int imageSize)
    {
        imageSize = 0;
        if (OnnxExportImageSizeComboBox.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out imageSize) ||
            imageSize is < 320 or > 2048 || imageSize % 32 != 0)
        {
            TrainingStatusText.Text = "Выберите корректный размер ONNX input, кратный 32.";
            return false;
        }

        return true;
    }

    private void UpdateTrainingControls(bool isRunning)
    {
        CheckDatasetButton.IsEnabled = !isRunning;
        StartTrainingButton.IsEnabled = !isRunning;
        StopTrainingButton.IsEnabled = isRunning;
        TrainingEpochsTextBox.IsEnabled = !isRunning;
        TrainingPatienceTextBox.IsEnabled = !isRunning;
        TrainingBatchTextBox.IsEnabled = !isRunning;
        TrainingModelComboBox.IsEnabled = !isRunning;
        TrainingDeviceComboBox.IsEnabled = !isRunning;
        OnnxExportImageSizeComboBox.IsEnabled = !isRunning;
        ExportOnnxButton.IsEnabled = !isRunning;
        OpenTrainingLogButton.IsEnabled = !string.IsNullOrWhiteSpace(_trainingLogPath) && File.Exists(_trainingLogPath);
        if (isRunning)
        {
            OpenTrainingResultFolderButton.IsEnabled = false;
        }
    }

    private void AppendTrainingLog(string line)
    {
        WriteTrainingLogFile(line);
        var logLine = TrainingLogLine.Create(line);
        lock (_trainingLogUiSync)
        {
            if (logLine.ReplacesPreviousProgress)
            {
                _pendingTrainingProgressLine = logLine;
            }
            else
            {
                if (_pendingTrainingProgressLine is not null)
                {
                    _pendingTrainingLogLines.Enqueue(_pendingTrainingProgressLine);
                    _pendingTrainingProgressLine = null;
                }

                _pendingTrainingLogLines.Enqueue(logLine);
            }
        }
    }

    private void StartTrainingLogUiUpdates()
    {
        lock (_trainingLogUiSync)
        {
            _pendingTrainingLogLines.Clear();
            _pendingTrainingProgressLine = null;
        }

        _trainingLogUiTimer.Start();
    }

    private void StopTrainingLogUiUpdates()
    {
        _trainingLogUiTimer.Stop();
        UpdateTrainingLogUi();
    }

    private void TrainingLogUiTimer_Tick(object? sender, EventArgs e) =>
        UpdateTrainingLogUi();

    private void UpdateTrainingLogUi()
    {
        var lines = TakePendingTrainingLogLines();
        if (lines.Count == 0)
        {
            return;
        }

        TrainingLastLogText.Text = lines[^1].Text;
        _trainingLogWindow?.AppendLines(lines);
    }

    private List<TrainingLogLine> TakePendingTrainingLogLines()
    {
        lock (_trainingLogUiSync)
        {
            var lines = new List<TrainingLogLine>(MaximumTrainingLogLinesPerUiUpdate + 1);
            while (lines.Count < MaximumTrainingLogLinesPerUiUpdate &&
                   _pendingTrainingLogLines.TryDequeue(out var line))
            {
                lines.Add(line);
            }

            if (_pendingTrainingLogLines.Count == 0 && _pendingTrainingProgressLine is not null)
            {
                lines.Add(_pendingTrainingProgressLine);
                _pendingTrainingProgressLine = null;
            }

            return lines;
        }
    }

    private void OpenTrainingLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_trainingLogPath) || !File.Exists(_trainingLogPath))
        {
            TrainingStatusText.Text = "Журнал текущего запуска ещё не создан.";
            return;
        }

        if (_trainingLogWindow is { IsVisible: true })
        {
            _trainingLogWindow.Activate();
            return;
        }

        _trainingLogWindow = new TrainingLogWindow(_trainingLogPath) { Owner = this };
        _trainingLogWindow.Closed += (_, _) => _trainingLogWindow = null;
        _trainingLogWindow.Show();
    }

    private void OpenTrainingResultFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_trainingResultDirectory) || !Directory.Exists(_trainingResultDirectory))
        {
            TrainingStatusText.Text = "Папка результата ещё не создана.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_trainingResultDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            TrainingStatusText.Text = $"Не удалось открыть результат: {exception.Message}";
        }
    }

    private void CreateTrainingLog(string repositoryRoot, string operation, string? runName)
    {
        var logsDirectory = Path.Combine(repositoryRoot, "artifacts", "ml", "logs");
        Directory.CreateDirectory(logsDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _trainingLogPath = Path.Combine(logsDirectory, $"{operation}-{timestamp}.log");
        File.WriteAllText(_trainingLogPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _trainingResultDirectory = runName is null
            ? null
            : Path.Combine(repositoryRoot, "artifacts", "ml", runName);
        OpenTrainingLogButton.IsEnabled = true;
        OpenTrainingResultFolderButton.IsEnabled = false;
    }

    private void WriteTrainingLogFile(string line)
    {
        var logPath = _trainingLogPath;
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        lock (_trainingLogSync)
        {
            File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static MlPaths? FindMlPaths()
    {
        foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                var pythonPath = Path.Combine(directory.FullName, ".venv", "Scripts", "python.exe");
                var scriptPath = Path.Combine(directory.FullName, "ml", "fishing_obb.py");
                if (File.Exists(pythonPath) && File.Exists(scriptPath))
                {
                    return new MlPaths(directory.FullName, pythonPath, scriptPath);
                }
            }
        }

        return null;
    }

    private static int GetNextTrainingVersion(string repositoryRoot)
    {
        const string runPrefix = "fishing-panel-obb";
        var artifactsDirectory = Path.Combine(repositoryRoot, "artifacts", "ml");
        var maximumVersion = 0;
        if (Directory.Exists(artifactsDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(artifactsDirectory, $"{runPrefix}*"))
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, runPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    maximumVersion = Math.Max(maximumVersion, 1);
                }
                else if (name.StartsWith($"{runPrefix}-", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(name[(runPrefix.Length + 1)..], out var version) &&
                         version > 0)
                {
                    maximumVersion = Math.Max(maximumVersion, version);
                }
            }

            var logsDirectory = Path.Combine(artifactsDirectory, "logs");
            if (Directory.Exists(logsDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(logsDirectory, "stats-*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (int.TryParse(name["stats-".Length..], out var version) && version > 0)
                    {
                        maximumVersion = Math.Max(maximumVersion, version);
                    }
                }
            }
        }

        return maximumVersion + 1;
    }

    private void DatasetSplit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isRestoringSettings)
        {
            return;
        }

        ApplyExistingAnnotationState();
    }

    private async void AcceptAnnotation_Click(object sender, RoutedEventArgs e) =>
        await SavePositiveAnnotationAsync();

    private void CorrectAnnotation_Click(object sender, RoutedEventArgs e) => BeginCorrection();

    private void ManualAnnotation_Click(object sender, RoutedEventArgs e) => BeginManualAnnotation();

    private async void NegativeAnnotation_Click(object sender, RoutedEventArgs e) =>
        await SaveAnnotationAsync(ObbAnnotationKind.Negative);

    private async void DeleteAnnotation_Click(object sender, RoutedEventArgs e) =>
        await DeleteCurrentAnnotationAsync();

    private void BeginCorrection()
    {
        if (AnnotationModeCheckBox.IsChecked != true || _annotationOverlay?.BeginCorrection() != true)
        {
            AnnotationStatusText.Text = "Нет готовых четырёх точек — используйте M для ручной разметки.";
            return;
        }

        AnnotationStatusText.Text = "Перетащите оранжевые точки и нажмите Enter для сохранения.";
        UpdateAnnotationControls();
    }

    private void BeginManualAnnotation()
    {
        if (AnnotationModeCheckBox.IsChecked != true || _viewModel.SourcePreview is null)
        {
            return;
        }

        StopPlayback();
        _annotationOverlay?.BeginManual();
        AnnotationStatusText.Text = "Поставьте четыре угла шкалы, затем нажмите Enter.";
        UpdateAnnotationControls();
    }

    private async Task SavePositiveAnnotationAsync()
    {
        if (_annotationOverlay?.HasCompleteBox != true)
        {
            AnnotationStatusText.Text = "Для positive sample нужны четыре точки.";
            return;
        }

        var kind = _annotationOverlay.Mode switch
        {
            ObbOverlayMode.Corrected => ObbAnnotationKind.Corrected,
            ObbOverlayMode.Manual => ObbAnnotationKind.Manual,
            ObbOverlayMode.Existing when CurrentExistingAnnotation is { AnnotationKind: not ObbAnnotationKind.Negative } existing =>
                existing.AnnotationKind,
            _ => ObbAnnotationKind.Accepted
        };
        await SaveAnnotationAsync(kind);
    }

    private async Task SaveAnnotationAsync(ObbAnnotationKind kind)
    {
        if (_isAnnotationSaveActive ||
            AnnotationModeCheckBox.IsChecked != true ||
            _viewModel.SourcePreview is null ||
            !EnsureDatasetRoot())
        {
            return;
        }

        var sourcePath = _videoSession?.Metadata.SourcePath ?? _currentImagePath;
        if (sourcePath is null)
        {
            return;
        }

        _isAnnotationSaveActive = true;
        UpdateAnnotationControls();
        AnnotationStatusText.Text = "Сохранение sample…";
        try
        {
            var session = _videoSession;
            long? frameIndex = session is null ? null : _currentFrameIndex;
            var videoFrame = _annotationVideoFrame;
            var imagePng = _annotationFramePng;
            if (session is not null && (videoFrame is null || videoFrame.FrameIndex != frameIndex))
            {
                AnnotationStatusText.Text = "Исходный кадр разметки ещё не готов. Повторите сохранение через мгновение.";
                return;
            }

            if (session is null && imagePng is null)
            {
                AnnotationStatusText.Text = "Исходное изображение разметки ещё не готово. Повторите сохранение через мгновение.";
                return;
            }

            var corners = kind == ObbAnnotationKind.Negative
                ? null
                : _annotationOverlay!.GetCorners();
            var detectorProposal = _currentDetection is null
                ? null
                : new DetectorProposalMetadata(
                    _currentDetection.IsDetected,
                    _currentDetection.Confidence,
                    _currentDetection.Reason,
                    _currentDetection.Corners);
            var imageWidth = videoFrame?.Width ?? _viewModel.SourcePreview.PixelWidth;
            var imageHeight = videoFrame?.Height ?? _viewModel.SourcePreview.PixelHeight;
            var framePng = videoFrame is not null
                ? await Task.Run(() => FramePngEncoder.Encode(videoFrame))
                : imagePng!;
            var sample = new ObbDatasetSample(
                sourcePath,
                frameIndex,
                GetSelectedSplit(),
                kind,
                imageWidth,
                imageHeight,
                framePng,
                corners,
                detectorProposal);
            var result = await _datasetWriter.SaveAsync(_datasetRoot!, sample);
            await RefreshExistingAnnotationAsync();
            await RefreshTimelineAnnotationsAsync();
            AnnotationStatusText.Text = $"Сохранено: {result.SampleId}. Текущий кадр оставлен открытым.";
        }
        catch (Exception exception)
        {
            AnnotationStatusText.Text = $"Ошибка сохранения: {exception.Message}";
            MessageBox.Show(
                this,
                exception.Message,
                "Ошибка OBB-разметки",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isAnnotationSaveActive = false;
            UpdateAnnotationControls();
        }
    }

    private async Task DeleteCurrentAnnotationAsync()
    {
        if (_isAnnotationSaveActive ||
            string.IsNullOrWhiteSpace(_datasetRoot) ||
            _existingAnnotations.Count == 0)
        {
            return;
        }

        var sourcePath = _videoSession?.Metadata.SourcePath ?? _currentImagePath;
        if (sourcePath is null)
        {
            return;
        }

        var splits = string.Join(", ", _existingAnnotations.Select(annotation => annotation.Split).Distinct());
        var confirmation = MessageBox.Show(
            this,
            $"Удалить разметку текущего кадра из {splits}?\n\nБудут удалены PNG, label и metadata. Отменить это действие нельзя.",
            "Удаление OBB-разметки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        StopPlayback();
        _isAnnotationSaveActive = true;
        UpdateAnnotationControls();
        AnnotationStatusText.Text = "Удаление sample…";
        try
        {
            long? frameIndex = _videoSession is null ? null : _currentFrameIndex;
            var result = await _datasetWriter.DeleteExistingAsync(_datasetRoot, sourcePath, frameIndex);
            await RefreshExistingAnnotationAsync();
            await RefreshTimelineAnnotationsAsync();
            AnnotationStatusText.Text =
                $"Удалено файлов: {result.DeletedFiles}. Текущий кадр оставлен открытым.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnnotationStatusText.Text = $"Ошибка удаления: {exception.Message}";
            MessageBox.Show(
                this,
                exception.Message,
                "Ошибка удаления OBB-разметки",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isAnnotationSaveActive = false;
            UpdateAnnotationControls();
        }
    }

    private bool EnsureDatasetRoot()
    {
        if (!string.IsNullOrWhiteSpace(_datasetRoot))
        {
            return EnsureDatasetStructure();
        }

        ChooseDatasetFolder_Click(this, new RoutedEventArgs());
        return !string.IsNullOrWhiteSpace(_datasetRoot) && EnsureDatasetStructure();
    }

    private bool EnsureDatasetStructure()
    {
        if (string.IsNullOrWhiteSpace(_datasetRoot))
        {
            return false;
        }

        try
        {
            _datasetWriter.EnsureStructure(_datasetRoot);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AnnotationStatusText.Text = $"Не удалось подготовить dataset: {exception.Message}";
            return false;
        }
    }

    private DatasetSplit GetSelectedSplit()
    {
        var tag = (DatasetSplitComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<DatasetSplit>(tag, ignoreCase: true, out var split)
            ? split
            : DatasetSplit.Train;
    }

    private ObbDatasetExistingSample? CurrentExistingAnnotation =>
        _existingAnnotations.FirstOrDefault(annotation => annotation.Split == GetSelectedSplit()) ??
        _existingAnnotations.FirstOrDefault();

    private async Task RefreshExistingAnnotationAsync()
    {
        _existingAnnotations = [];
        var datasetRoot = _datasetRoot;
        var sourcePath = _videoSession?.Metadata.SourcePath ?? _currentImagePath;
        long? frameIndex = _videoSession is null ? null : _currentFrameIndex;
        if (string.IsNullOrWhiteSpace(datasetRoot) || sourcePath is null)
        {
            ApplyExistingAnnotationState();
            return;
        }

        try
        {
            var matches = await _datasetWriter.FindExistingAsync(datasetRoot, sourcePath, frameIndex);
            var currentSourcePath = _videoSession?.Metadata.SourcePath ?? _currentImagePath;
            long? currentFrameIndex = _videoSession is null ? null : _currentFrameIndex;
            if (!string.Equals(sourcePath, currentSourcePath, StringComparison.OrdinalIgnoreCase) ||
                frameIndex != currentFrameIndex)
            {
                return;
            }

            _existingAnnotations = matches;
            ApplyExistingAnnotationState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetExistingAnnotationStatus(
                $"Не удалось прочитать разметку: {exception.Message}",
                Brushes.OrangeRed);
        }
    }

    private async Task RefreshTimelineAnnotationsAsync()
    {
        var session = _videoSession;
        var datasetRoot = _datasetRoot;
        if (session is null || string.IsNullOrWhiteSpace(datasetRoot))
        {
            _timelineAnnotations = [];
            RenderTimelineMarkers();
            UpdateAnnotationControls();
            return;
        }

        var sourcePath = session.Metadata.SourcePath;
        try
        {
            var markers = await _datasetWriter.FindTimelineMarkersAsync(datasetRoot, sourcePath);
            if (!ReferenceEquals(session, _videoSession) ||
                !string.Equals(sourcePath, _videoSession?.Metadata.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _timelineAnnotations = markers;
            RenderTimelineMarkers();
            UpdateAnnotationControls();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _timelineAnnotations = [];
            RenderTimelineMarkers();
            TimelineAnnotationCountText.Text = $"Не удалось прочитать метки: {exception.Message}";
            UpdateAnnotationControls();
        }
    }

    private async Task NavigateToAnnotationAsync(bool previous)
    {
        if (_videoSession is null || _isFrameTransitionActive)
        {
            return;
        }

        var targetFrame = previous
            ? _timelineAnnotations
                .Where(marker => marker.FrameIndex < _currentFrameIndex)
                .Select(marker => (long?)marker.FrameIndex)
                .Max()
            : _timelineAnnotations
                .Where(marker => marker.FrameIndex > _currentFrameIndex)
                .Select(marker => (long?)marker.FrameIndex)
                .Min();
        if (targetFrame is null)
        {
            return;
        }

        StopPlayback();
        await ShowVideoFrameAsync(targetFrame.Value);
    }

    private void RenderTimelineMarkers()
    {
        if (!IsInitialized || TimelineMarkersCanvas is null || TimelineAnnotationCountText is null)
        {
            return;
        }

        TimelineMarkersCanvas.Children.Clear();
        TimelineAnnotationCountText.Text = $"Меток текущего видео: {_timelineAnnotations.Count:N0}";
        var session = _videoSession;
        var availableWidth = TimelineMarkersCanvas.ActualWidth;
        if (session is null || availableWidth <= 2 || session.Metadata.FrameCount <= 1)
        {
            return;
        }

        // Метка центрируется относительно позиции кадра, оставляя по пикселю на краях timeline.
        var maximumFrame = session.Metadata.FrameCount - 1d;
        foreach (var marker in _timelineAnnotations)
        {
            var x = 1 + Math.Clamp(marker.FrameIndex / maximumFrame, 0, 1) * (availableWidth - 2);
            var line = new Rectangle
            {
                Width = 3,
                Height = TimelineMarkersCanvas.Height,
                RadiusX = 1,
                RadiusY = 1,
                Fill = marker.AnnotationKind == ObbAnnotationKind.Negative
                    ? Brushes.Orange
                    : Brushes.LimeGreen
            };
            Canvas.SetLeft(line, x - line.Width / 2);
            TimelineMarkersCanvas.Children.Add(line);
        }
    }

    private void ApplyExistingAnnotationState()
    {
        var sourcePath = _videoSession?.Metadata.SourcePath ?? _currentImagePath;
        if (sourcePath is null)
        {
            SetExistingAnnotationStatus("Откройте видео или изображение.", GetSecondaryTextBrush());
            UpdateAnnotationInstruction();
            return;
        }

        if (string.IsNullOrWhiteSpace(_datasetRoot))
        {
            SetExistingAnnotationStatus("Выберите dataset для проверки кадра.", GetSecondaryTextBrush());
            UpdateAnnotationInstruction();
            return;
        }

        var existing = CurrentExistingAnnotation;
        if (existing is null)
        {
            SetExistingAnnotationStatus("Текущий кадр ещё не сохранён в dataset.", GetSecondaryTextBrush());
        }
        else if (_existingAnnotations.Count > 1)
        {
            var splits = string.Join(", ", _existingAnnotations.Select(annotation => annotation.Split));
            SetExistingAnnotationStatus(
                $"⚠ Кадр найден в нескольких split: {splits}. Следующее сохранение удалит лишние копии.",
                Brushes.Orange);
        }
        else
        {
            var moveHint = existing.Split == GetSelectedSplit()
                ? string.Empty
                : $" Сохранение перенесёт его в {GetSelectedSplit()}.";
            SetExistingAnnotationStatus(
                $"✓ Уже размечен: {existing.Split} · {FormatAnnotationKind(existing.AnnotationKind)}.{moveHint}",
                (Brush)FindResource("AccentBrush"));
        }

        RefreshAnnotationSuggestion();
        UpdateAnnotationInstruction();
        UpdateAnnotationControls();
    }

    private void UpdateAnnotationInstruction()
    {
        if (AnnotationModeCheckBox.IsChecked != true)
        {
            AnnotationStatusText.Text = "Режим разметки выключен.";
            return;
        }

        var existing = CurrentExistingAnnotation;
        if (existing is { AnnotationKind: ObbAnnotationKind.Negative })
        {
            AnnotationStatusText.Text = "Кадр сохранён как «рамки нет». Нажмите M, если хотите заменить его positive OBB.";
        }
        else if (existing is not null)
        {
            AnnotationStatusText.Text = "Сохранённая OBB показана зелёным. E — исправить, Enter — перезаписать.";
        }
        else if (_currentDetection?.IsDetected == true)
        {
            AnnotationStatusText.Text = "Detector предложил OBB. Примите или исправьте четыре точки.";
        }
        else
        {
            AnnotationStatusText.Text = "Предложения нет: нажмите M для ручной разметки или N, если рамки нет.";
        }
    }

    private static string FormatAnnotationKind(ObbAnnotationKind kind) => kind switch
    {
        ObbAnnotationKind.Accepted => "принято",
        ObbAnnotationKind.Corrected => "исправлено",
        ObbAnnotationKind.Manual => "вручную",
        ObbAnnotationKind.Negative => "рамки нет",
        _ => kind.ToString()
    };

    private void SetExistingAnnotationStatus(string text, Brush foreground)
    {
        ExistingAnnotationText.Text = text;
        ExistingAnnotationText.Foreground = foreground;
    }

    private Brush GetSecondaryTextBrush() => (Brush)FindResource("SecondaryTextBrush");

    private void SetCurrentDetection(PanelDetectionResult result)
    {
        _currentDetection = result;
        RefreshAnnotationSuggestion();
        UpdateAnnotationControls();
    }

    private void ResetCurrentDetection()
    {
        _currentDetection = null;
        _liveDetectionOverlay?.Clear();
        _existingAnnotations = [];
        _annotationFramePng = null;
        _annotationVideoFrame = null;
        _viewModel.SetTrainingBoundsPreview(null);
        SetExistingAnnotationStatus(
            string.IsNullOrWhiteSpace(_datasetRoot)
                ? "Выберите dataset для проверки кадра."
                : "Проверяю наличие разметки…",
            GetSecondaryTextBrush());
        if (AnnotationModeCheckBox.IsChecked == true)
        {
            _annotationOverlay?.Clear();
        }

        UpdateAnnotationControls();
    }

    private void RefreshAnnotationSuggestion()
    {
        if (_annotationOverlay is null || AnnotationModeCheckBox.IsChecked != true)
        {
            return;
        }

        var existing = CurrentExistingAnnotation;
        if (existing is { AnnotationKind: not ObbAnnotationKind.Negative, Corners.Count: 4 })
        {
            _annotationOverlay.ShowExisting(existing.Corners);
        }
        else if (existing is { AnnotationKind: ObbAnnotationKind.Negative })
        {
            _annotationOverlay.Clear();
        }
        else if (_currentDetection is { IsDetected: true, Corners.Count: 4 })
        {
            _annotationOverlay.ShowSuggestion(_currentDetection.Corners);
        }
        else
        {
            _annotationOverlay.Clear();
        }
    }

    private void AnnotationOverlay_Changed(object? sender, EventArgs e)
    {
        if (_annotationOverlay?.IsEditing == true)
        {
            AnnotationStatusText.Text = _annotationOverlay.HasCompleteBox
                ? "Четыре точки готовы. Enter — сохранить."
                : $"Поставлено точек: {_annotationOverlay.CornerCount}/4.";
        }

        ScheduleAnnotationPreview();
        UpdateAnnotationControls();
    }

    private async Task EnsureAnnotationFrameAsync()
    {
        if (_annotationFramePng is not null ||
            _annotationVideoFrame is { FrameIndex: var annotationFrameIndex } &&
            annotationFrameIndex == _currentFrameIndex)
        {
            return;
        }

        var session = _videoSession;
        var frameIndex = _currentFrameIndex;
        if (session is not null)
        {
            var videoFrame = await Task.Run(() => session.ReadFrame(frameIndex));
            if (ReferenceEquals(session, _videoSession) && frameIndex == _currentFrameIndex)
            {
                _annotationVideoFrame = videoFrame;
            }

            return;
        }

        if (_currentImagePath is not null)
        {
            var encodedImage = await File.ReadAllBytesAsync(_currentImagePath);
            _annotationFramePng = FramePngEncoder.NormalizeEncodedImage(encodedImage);
        }
    }

    private void ScheduleAnnotationPreview()
    {
        _annotationPreviewVersion++;
        _annotationPreviewTimer.Stop();
        if (AnnotationModeCheckBox.IsChecked != true ||
            (_annotationFramePng is null && _annotationVideoFrame is null) ||
            _annotationOverlay?.HasCompleteBox != true)
        {
            _viewModel.SetTrainingBoundsPreview(null);
            return;
        }

        _annotationPreviewTimer.Start();
    }

    private async void AnnotationPreviewTimer_Tick(object? sender, EventArgs e)
    {
        _annotationPreviewTimer.Stop();
        if (_isAnnotationPreviewRendering)
        {
            _annotationPreviewTimer.Start();
            return;
        }

        var encodedFrame = _annotationFramePng;
        var videoFrame = _annotationVideoFrame;
        var corners = _annotationOverlay?.GetCorners();
        if ((encodedFrame is null && videoFrame is null) || corners?.Count != 4)
        {
            _viewModel.SetTrainingBoundsPreview(null);
            return;
        }

        var version = _annotationPreviewVersion;
        _isAnnotationPreviewRendering = true;
        try
        {
            var preview = await Task.Run(
                () => videoFrame is not null
                    ? ObbPreviewRenderer.RenderBgr24(
                        videoFrame.Bgr24Pixels,
                        videoFrame.Width,
                        videoFrame.Height,
                        videoFrame.Stride,
                        corners)
                    : ObbPreviewRenderer.Render(encodedFrame!, corners));
            if (version == _annotationPreviewVersion)
            {
                _viewModel.SetTrainingBoundsPreview(preview);
            }
        }
        catch
        {
            if (version == _annotationPreviewVersion)
            {
                _viewModel.SetTrainingBoundsPreview(null);
            }
        }
        finally
        {
            _isAnnotationPreviewRendering = false;
            if (version != _annotationPreviewVersion &&
                AnnotationModeCheckBox.IsChecked == true &&
                _annotationOverlay?.HasCompleteBox == true)
            {
                _annotationPreviewTimer.Start();
            }
        }
    }

    private void UpdateAnnotationControls()
    {
        if (_annotationOverlay is null)
        {
            return;
        }

        var active = AnnotationModeCheckBox.IsChecked == true &&
                     _viewModel.SourcePreview is not null &&
                     !_isAnnotationSaveActive;
        AcceptAnnotationButton.IsEnabled = active && _annotationOverlay.HasCompleteBox;
        CorrectAnnotationButton.IsEnabled = active && _annotationOverlay.HasCompleteBox;
        ManualAnnotationButton.IsEnabled = active;
        NegativeAnnotationButton.IsEnabled = active;
        DeleteAnnotationButton.IsEnabled = active && _existingAnnotations.Count > 0;
        PreviousAnnotationButton.IsEnabled = active &&
                                             _timelineAnnotations.Any(marker => marker.FrameIndex < _currentFrameIndex);
        NextAnnotationButton.IsEnabled = active &&
                                         _timelineAnnotations.Any(marker => marker.FrameIndex > _currentFrameIndex);
    }

    private sealed record MlPaths(string RepositoryRoot, string PythonPath, string ScriptPath);

    private static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }
}
