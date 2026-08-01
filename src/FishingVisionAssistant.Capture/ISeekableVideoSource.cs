namespace FishingVisionAssistant.Capture;

/// <summary>
/// Предоставляет синхронный произвольный доступ к кадрам offline-видео для Frame Inspector.
/// </summary>
public interface ISeekableVideoSource : IDisposable
{
    /// <summary>
    /// Возвращает метаданные открытого видео.
    /// </summary>
    VideoMetadata Metadata { get; }

    /// <summary>
    /// Декодирует кадр по нулевому индексу, выполняя seek только при необходимости.
    /// </summary>
    VideoFrame ReadFrame(long frameIndex);
}
