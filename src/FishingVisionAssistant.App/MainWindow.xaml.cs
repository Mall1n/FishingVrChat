using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private static readonly double[] PlaybackSpeeds = [0.25, 0.5, 1, 1.5, 2];

    private IPanelDetector _panelDetector;
    private readonly DispatcherTimer _hsvDebounceTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };
    private readonly DispatcherTimer _annotationPreviewTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(80)
    };
    private readonly DispatcherTimer _playbackTimer = new(DispatcherPriority.Render);
    private readonly ApplicationSettingsStore _settingsStore = new();
    private readonly ObbDatasetWriter _datasetWriter = new();
    private readonly MainWindowViewModel _viewModel = new();
    private ObbAnnotationOverlay? _annotationOverlay;
    private PerformanceStatistics _performanceStatistics = new();
    private VideoAnalysisSession? _videoSession;
    private PanelDetectionResult? _currentDetection;
    private string? _datasetRoot;
    private string? _currentImagePath;
    private string? _lastVideoPath;
    private long _currentFrameIndex;
    private long _lastVideoFrameIndex;
    private byte[]? _annotationFramePng;
    private IReadOnlyList<ObbDatasetExistingSample> _existingAnnotations = [];
    private double _playbackSpeed = 1;
    private int _playbackSpeedIndex = 2;
    private bool _isFrameTransitionActive;
    private bool _isAnnotationSaveActive;
    private bool _isAnnotationPreviewRendering;
    private bool _isRestoringSettings;
    private string? _onnxModelPath;
    private OnnxPanelDetector? _onnxPanelDetector;
    private int _annotationPreviewVersion;

    public MainWindow()
    {
        _panelDetector = CreateLegacyPanelDetector();
        InitializeComponent();
        DataContext = _viewModel;
        _annotationOverlay = new ObbAnnotationOverlay(SourcePreviewImage, AnnotationCanvas);
        _annotationOverlay.Changed += AnnotationOverlay_Changed;
        UpdateAnnotationControls();
        _annotationPreviewTimer.Tick += AnnotationPreviewTimer_Tick;
        _hsvDebounceTimer.Tick += HsvDebounceTimer_Tick;
        _playbackTimer.Tick += PlaybackTimer_Tick;
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

        await RestoreDetectorAsync(settings.UseOnnxDetector);
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

        if (sourcePath is not null && IsImage(sourcePath))
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
            ResetCurrentDetection();
            _currentImagePath = path;
            _viewModel.BeginImageAnalysis(path);
            var encodedImage = await File.ReadAllBytesAsync(path);
            _annotationFramePng = FramePngEncoder.NormalizeEncodedImage(encodedImage);
            var result = await Task.Run(() => _panelDetector.Detect(encodedImage));
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
        DisposeVideoSession();
        ResetCurrentDetection();
        _currentImagePath = null;
        _viewModel.BeginVideoOpen(path);

        try
        {
            var session = await Task.Run(
                () => new VideoAnalysisSession(new VideoFrameSource(path), _panelDetector));
            _videoSession = session;
            _lastVideoPath = Path.GetFullPath(path);
            _performanceStatistics = new PerformanceStatistics();
            _currentFrameIndex = Math.Clamp(initialFrameIndex, 0, session.Metadata.FrameCount - 1);
            _lastVideoFrameIndex = _currentFrameIndex;
            _viewModel.InitializeVideo(session.Metadata);
            UpdatePlaybackInterval();
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
            _lastVideoFrameIndex = analysis.FrameIndex;
            _viewModel.ApplyVideoFrame(
                session.Metadata,
                analysis,
                _performanceStatistics.GetSnapshot());
            if (AnnotationModeCheckBox.IsChecked == true)
            {
                _annotationFramePng = await Task.Run(() => session.ExportFramePng(analysis.FrameIndex));
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
                case Key.S:
                    await SkipAnnotationAsync();
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

    private void PlaybackSpeed_Click(object sender, RoutedEventArgs e)
    {
        _playbackSpeedIndex = (_playbackSpeedIndex + 1) % PlaybackSpeeds.Length;
        _playbackSpeed = PlaybackSpeeds[_playbackSpeedIndex];
        _viewModel.SetPlaybackSpeed(_playbackSpeed);
        UpdatePlaybackInterval();
    }

    private void HsvSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ScheduleHsvReanalysis();

    private void ResetHsv_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetHsv();
        ScheduleHsvReanalysis();
    }

    private void ScheduleHsvReanalysis()
    {
        if (!IsLoaded || _isRestoringSettings)
        {
            return;
        }

        if (!_hsvDebounceTimer.IsEnabled)
        {
            _hsvDebounceTimer.Start();
        }
    }

    private async void HsvDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _hsvDebounceTimer.Stop();
        if (_isFrameTransitionActive)
        {
            ScheduleHsvReanalysis();
            return;
        }

        StopPlayback();
        if (_panelDetector is OnnxPanelDetector)
        {
            return;
        }

        _panelDetector = CreateLegacyPanelDetector();
        if (_videoSession is not null)
        {
            _videoSession.UpdateDetector(_panelDetector);
            _performanceStatistics = new PerformanceStatistics();
            await ShowVideoFrameAsync(_currentFrameIndex);
            return;
        }

        if (_currentImagePath is not null)
        {
            await AnalyzeImageAsync(_currentImagePath);
        }
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

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _settingsStore.Save(CaptureSettings());
        _annotationPreviewTimer.Stop();
        _hsvDebounceTimer.Stop();
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
            OnnxModelPathText.Text = _onnxModelPath ?? "Модель не выбрана";
            OnnxDetectorCheckBox.IsChecked = settings.UseOnnxDetector;

            var split = Enum.IsDefined(typeof(DatasetSplit), settings.DatasetSplit)
                ? settings.DatasetSplit
                : DatasetSplit.Train;
            SelectDatasetSplit(split);

            // Сначала расширяем Hue-диапазон, чтобы независимые setters не обрезали сохранённую пару min/max.
            _viewModel.MinimumHue = 0;
            _viewModel.MaximumHue = 179;
            _viewModel.MinimumHue = settings.MinimumHue;
            _viewModel.MaximumHue = settings.MaximumHue;
            _viewModel.MinimumSaturation = settings.MinimumSaturation;
            _viewModel.MinimumValue = settings.MinimumValue;

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
        UseOnnxDetector = OnnxDetectorCheckBox.IsChecked == true && _panelDetector is OnnxPanelDetector,
        MinimumHue = _viewModel.MinimumHue,
        MaximumHue = _viewModel.MaximumHue,
        MinimumSaturation = _viewModel.MinimumSaturation,
        MinimumValue = _viewModel.MinimumValue,
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

    private IPanelDetector CreateLegacyPanelDetector() =>
        new PanelDetector(_viewModel.CreatePanelDetectorOptions());

    private async Task RestoreDetectorAsync(bool useOnnxDetector)
    {
        _panelDetector = CreateLegacyPanelDetector();
        SetDetectorUi(isOnnxActive: false);
        if (!useOnnxDetector)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_onnxModelPath) || !File.Exists(_onnxModelPath))
        {
            SetOnnxCheckBox(false);
            OnnxDetectorStatusText.Text = "Сохранённая ONNX-модель не найдена; используется legacy OpenCV detector.";
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
        OnnxModelPathText.Text = _onnxModelPath;
        SetOnnxCheckBox(true);
        await ActivateOnnxDetectorAsync(reanalyzeCurrentSource: true);
    }

    private async void OnnxDetectorMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRestoringSettings || !IsLoaded)
        {
            return;
        }

        if (_isFrameTransitionActive)
        {
            SetOnnxCheckBox(_panelDetector is OnnxPanelDetector);
            OnnxDetectorStatusText.Text = "Дождитесь завершения анализа текущего кадра.";
            return;
        }

        if (OnnxDetectorCheckBox.IsChecked == true)
        {
            await ActivateOnnxDetectorAsync(reanalyzeCurrentSource: true);
            return;
        }

        await ActivateLegacyDetectorAsync(reanalyzeCurrentSource: true);
    }

    private async Task ActivateOnnxDetectorAsync(bool reanalyzeCurrentSource)
    {
        if (string.IsNullOrWhiteSpace(_onnxModelPath) || !File.Exists(_onnxModelPath))
        {
            SetOnnxCheckBox(false);
            OnnxDetectorStatusText.Text = "Выберите существующий .onnx файл.";
            return;
        }

        StopPlayback();
        SetOnnxControlsEnabled(false);
        OnnxDetectorStatusText.Text = "Загрузка модели и подготовка DirectML…";
        try
        {
            var modelPath = _onnxModelPath;
            var detector = await Task.Run(() => new OnnxPanelDetector(new OnnxPanelDetectorOptions
            {
                ModelPath = modelPath,
                MinimumConfidence = 0.5,
                MinimumAspectRatio = 10,
                ExecutionProvider = OnnxExecutionProvider.DirectMl
            }));

            var previous = _onnxPanelDetector;
            _onnxPanelDetector = detector;
            await ReplaceActiveDetectorAsync(detector, reanalyzeCurrentSource);
            previous?.Dispose();
            SetOnnxCheckBox(true);
            OnnxDetectorStatusText.Text = $"Модель загружена · {detector.ProviderName} · 1024 × 1024.";
        }
        catch (Exception exception)
        {
            SetOnnxCheckBox(_panelDetector is OnnxPanelDetector);
            OnnxDetectorStatusText.Text = $"ONNX не загружен: {exception.Message}";
            MessageBox.Show(
                this,
                $"Не удалось загрузить ONNX detector: {exception.Message}",
                "Ошибка ONNX detector",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetOnnxControlsEnabled(true);
        }
    }

    private async Task ActivateLegacyDetectorAsync(bool reanalyzeCurrentSource)
    {
        var previous = _onnxPanelDetector;
        _onnxPanelDetector = null;
        await ReplaceActiveDetectorAsync(CreateLegacyPanelDetector(), reanalyzeCurrentSource);
        previous?.Dispose();
        SetDetectorUi(isOnnxActive: false);
        OnnxDetectorStatusText.Text = "Используется legacy OpenCV detector; HSV Lab снова активен.";
    }

    private async Task ReplaceActiveDetectorAsync(IPanelDetector detector, bool reanalyzeCurrentSource)
    {
        _panelDetector = detector;
        SetDetectorUi(detector is OnnxPanelDetector);
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

    private void SetDetectorUi(bool isOnnxActive)
    {
        _viewModel.SetOnnxDetectorActive(isOnnxActive);
        HsvLabPanel.IsEnabled = !isOnnxActive;
        HsvLabPanel.Opacity = isOnnxActive ? 0.55 : 1;
    }

    private void SetOnnxCheckBox(bool isChecked)
    {
        _isRestoringSettings = true;
        try
        {
            OnnxDetectorCheckBox.IsChecked = isChecked;
        }
        finally
        {
            _isRestoringSettings = false;
        }
    }

    private void SetOnnxControlsEnabled(bool isEnabled)
    {
        OnnxDetectorCheckBox.IsEnabled = isEnabled;
        ChooseOnnxModelButton.IsEnabled = isEnabled;
    }

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
                return;
            }

            RefreshAnnotationSuggestion();
            UpdateAnnotationInstruction();
        }
        else
        {
            _annotationFramePng = null;
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
        UpdateAnnotationControls();
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

    private async void SkipAnnotation_Click(object sender, RoutedEventArgs e) =>
        await SkipAnnotationAsync();

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
            var framePng = session is not null
                ? await Task.Run(() => session.ExportFramePng(_currentFrameIndex))
                : FramePngEncoder.NormalizeEncodedImage(await File.ReadAllBytesAsync(sourcePath));
            var corners = kind == ObbAnnotationKind.Negative
                ? null
                : _annotationOverlay!.GetCorners();
            var legacyDetection = _currentDetection is null
                ? null
                : new LegacyDetectionMetadata(
                    _currentDetection.IsDetected,
                    _currentDetection.Confidence,
                    _currentDetection.Reason,
                    _currentDetection.Corners);
            var sample = new ObbDatasetSample(
                sourcePath,
                frameIndex,
                GetSelectedSplit(),
                kind,
                _viewModel.SourcePreview.PixelWidth,
                _viewModel.SourcePreview.PixelHeight,
                framePng,
                corners,
                legacyDetection);
            var result = await _datasetWriter.SaveAsync(_datasetRoot!, sample);
            await RefreshExistingAnnotationAsync();
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

    private async Task SkipAnnotationAsync()
    {
        if (_videoSession is null || _isAnnotationSaveActive)
        {
            return;
        }

        AnnotationStatusText.Text = "Кадр пропущен.";
        await NavigateRelativeAsync(1);
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
        _existingAnnotations = [];
        _annotationFramePng = null;
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
        if (_annotationFramePng is not null)
        {
            return;
        }

        var session = _videoSession;
        var frameIndex = _currentFrameIndex;
        if (session is not null)
        {
            var encodedFrame = await Task.Run(() => session.ExportFramePng(frameIndex));
            if (ReferenceEquals(session, _videoSession) && frameIndex == _currentFrameIndex)
            {
                _annotationFramePng = encodedFrame;
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
            _annotationFramePng is null ||
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
        var corners = _annotationOverlay?.GetCorners();
        if (encodedFrame is null || corners?.Count != 4)
        {
            _viewModel.SetTrainingBoundsPreview(null);
            return;
        }

        var version = _annotationPreviewVersion;
        _isAnnotationPreviewRendering = true;
        try
        {
            var preview = await Task.Run(() => ObbPreviewRenderer.Render(encodedFrame, corners));
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
        SkipAnnotationButton.IsEnabled = active && _videoSession is not null;
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
