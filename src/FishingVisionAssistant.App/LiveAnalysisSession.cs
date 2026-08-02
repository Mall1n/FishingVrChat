using System.IO;
using FishingVisionAssistant.Capture;
using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Последовательно анализирует свежие live-кадры, не накапливая очередь устаревших изображений.
/// </summary>
public sealed class LiveAnalysisSession : IAsyncDisposable
{
    private readonly IFrameSource _frameSource;
    private readonly IPanelDetector _detector;
    private readonly CancellationTokenSource _cancellation = new();
    private LivePreviewSettings _previewSettings;
    private Task? _processingTask;
    private int _forcePreviewFrame = 1;
    private int _isPaused;
    private bool _isDisposed;

    public LiveAnalysisSession(
        IFrameSource frameSource,
        IPanelDetector detector,
        LivePreviewSettings? previewSettings = null)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _previewSettings = ValidatePreviewSettings(previewSettings ?? LivePreviewSettings.Default);
    }

    /// <summary>
    /// Возвращает описание выбранного live-источника.
    /// </summary>
    public FrameSourceDescriptor Descriptor => _frameSource.Descriptor;

    /// <summary>
    /// Возвращает true, когда capture session сохранена, но новые кадры не анализируются.
    /// </summary>
    public bool IsPaused => Volatile.Read(ref _isPaused) != 0;

    /// <summary>
    /// Приостанавливает захват и анализ, не закрывая выбранный источник и последние preview.
    /// </summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (Interlocked.Exchange(ref _isPaused, 1) != 0)
        {
            return;
        }

        if (_frameSource is IPausableFrameSource pausableSource)
        {
            pausableSource.Pause();
        }
    }

    /// <summary>
    /// Продолжает захват и запрашивает свежий кадр с активными preview.
    /// </summary>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (Interlocked.Exchange(ref _isPaused, 0) == 0)
        {
            return;
        }

        Interlocked.Exchange(ref _forcePreviewFrame, 1);
        if (_frameSource is IPausableFrameSource pausableSource)
        {
            pausableSource.Resume();
        }
    }

    /// <summary>
    /// Применяет настройки следующих preview без перезапуска capture session.
    /// </summary>
    public void UpdatePreviewSettings(LivePreviewSettings settings)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        Volatile.Write(ref _previewSettings, ValidatePreviewSettings(settings));
        Interlocked.Exchange(ref _forcePreviewFrame, 1);
    }

    /// <summary>
    /// Запускает единственный цикл анализа и передаёт готовые результаты вызывающему коду.
    /// </summary>
    public void Start(
        Action<LiveFrameAnalysis> resultHandler,
        Action<Exception> errorHandler,
        Action completedHandler)
    {
        ArgumentNullException.ThrowIfNull(resultHandler);
        ArgumentNullException.ThrowIfNull(errorHandler);
        ArgumentNullException.ThrowIfNull(completedHandler);
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_processingTask is not null)
        {
            throw new InvalidOperationException("Live analysis уже запущен.");
        }

        _processingTask = Task.Run(
            () => ProcessFramesAsync(resultHandler, errorHandler, completedHandler, _cancellation.Token),
            _cancellation.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _cancellation.CancelAsync();
        if (_processingTask is not null)
        {
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _frameSource.DisposeAsync();
        _cancellation.Dispose();
    }

    private async Task ProcessFramesAsync(
        Action<LiveFrameAnalysis> resultHandler,
        Action<Exception> errorHandler,
        Action completedHandler,
        CancellationToken cancellationToken)
    {
        var analyzedFrameCount = 0L;
        try
        {
            await foreach (var frame in _frameSource.ReadFramesAsync(cancellationToken))
            {
                if (IsPaused)
                {
                    continue;
                }

                if (frame.PixelFormat != FramePixelFormat.Bgra32)
                {
                    throw new InvalidDataException($"Live source вернул неподдерживаемый формат {frame.PixelFormat}.");
                }

                var analysisStarted = DateTimeOffset.UtcNow;
                var previewSettings = Volatile.Read(ref _previewSettings);
                var isForcedPreview = Interlocked.Exchange(ref _forcePreviewFrame, 0) != 0;
                var requestedPreviewOutputs = previewSettings.HasActivePreview &&
                                              (isForcedPreview ||
                                               analyzedFrameCount % previewSettings.RefreshEveryNFrames == 0)
                    ? previewSettings.PreviewOutputs
                    : PanelPreviewOutputs.None;
                var detectorPreviewOutputs = requestedPreviewOutputs & ~PanelPreviewOutputs.SourceOverlay;
                analyzedFrameCount++;
                var detection = _detector.DetectBgra32(
                    frame.PixelBuffer,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    detectorPreviewOutputs);
                var completed = DateTimeOffset.UtcNow;
                if (IsPaused)
                {
                    continue;
                }

                resultHandler(new LiveFrameAnalysis(
                    frame.SequenceNumber,
                    detection,
                    analysisStarted - frame.Timestamp,
                    completed - frame.Timestamp,
                    requestedPreviewOutputs,
                    requestedPreviewOutputs.HasFlag(PanelPreviewOutputs.SourceOverlay) ? frame : null));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            errorHandler(exception);
        }
        finally
        {
            completedHandler();
        }
    }

    private static LivePreviewSettings ValidatePreviewSettings(LivePreviewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.RefreshEveryNFrames is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "Интервал preview должен составлять 1, 2, 4 или 8 кадров.");
        }

        return settings;
    }
}
