namespace FishingVisionAssistant.Capture;

/// <summary>
/// Предоставляет единый асинхронный поток кадров для offline-файлов и live capture.
/// </summary>
public interface IFrameSource : IAsyncDisposable
{
    /// <summary>
    /// Возвращает описание открытого источника.
    /// </summary>
    FrameSourceDescriptor Descriptor { get; }

    /// <summary>
    /// Последовательно выдаёт кадры до завершения источника или отмены операции.
    /// </summary>
    IAsyncEnumerable<CapturedFrame> ReadFramesAsync(CancellationToken cancellationToken = default);
}
