namespace FishingVisionAssistant.Capture;

/// <summary>
/// Представляет неизменяемый кадр с временной меткой и параметрами pixel buffer.
/// </summary>
public sealed record CapturedFrame(
    long SequenceNumber,
    DateTimeOffset Timestamp,
    int Width,
    int Height,
    int Stride,
    FramePixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelBuffer);
