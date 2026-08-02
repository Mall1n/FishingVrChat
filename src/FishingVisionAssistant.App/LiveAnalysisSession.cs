using System.Diagnostics;
using System.IO;
using FishingVisionAssistant.Capture;
using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Последовательно анализирует свежие live-кадры, не накапливая очередь устаревших изображений.
/// </summary>
public sealed class LiveAnalysisSession : IAsyncDisposable
{
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromMilliseconds(250);

    private readonly IFrameSource _frameSource;
    private readonly IPanelDetector _detector;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _processingTask;
    private bool _isDisposed;

    public LiveAnalysisSession(IFrameSource frameSource, IPanelDetector detector)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    /// <summary>
    /// Возвращает описание выбранного live-источника.
    /// </summary>
    public FrameSourceDescriptor Descriptor => _frameSource.Descriptor;

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
        var lastDiagnostic = Stopwatch.StartNew();
        var isFirstFrame = true;
        try
        {
            await foreach (var frame in _frameSource.ReadFramesAsync(cancellationToken))
            {
                if (frame.PixelFormat != FramePixelFormat.Bgra32)
                {
                    throw new InvalidDataException($"Live source вернул неподдерживаемый формат {frame.PixelFormat}.");
                }

                var analysisStarted = DateTimeOffset.UtcNow;
                var includeDiagnostics = isFirstFrame || lastDiagnostic.Elapsed >= DiagnosticInterval;
                var detection = _detector.DetectBgra32(
                    frame.PixelBuffer,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    includeDiagnostics);
                var completed = DateTimeOffset.UtcNow;
                if (includeDiagnostics)
                {
                    isFirstFrame = false;
                    lastDiagnostic.Restart();
                }

                resultHandler(new LiveFrameAnalysis(
                    frame.SequenceNumber,
                    detection,
                    analysisStarted - frame.Timestamp,
                    completed - frame.Timestamp,
                    includeDiagnostics));
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
}
