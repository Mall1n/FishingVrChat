namespace FishingVisionAssistant.Capture;

/// <summary>
/// Представляет CPU-кадр с временной меткой и BGRA32 pixel buffer.
/// </summary>
public sealed record CapturedFrame(
    long SequenceNumber,
    DateTimeOffset Timestamp,
    TimeSpan CaptureCopyTime,
    DateTimeOffset ReadyTimestamp,
    int Width,
    int Height,
    int Stride,
    FramePixelFormat PixelFormat,
    byte[] PixelBuffer);
