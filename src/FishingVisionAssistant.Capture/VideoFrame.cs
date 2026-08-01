namespace FishingVisionAssistant.Capture;

/// <summary>
/// Представляет декодированный BGR24-кадр с позицией в исходном видео.
/// </summary>
public sealed record VideoFrame(
    long FrameIndex,
    TimeSpan Position,
    int Width,
    int Height,
    int Stride,
    byte[] Bgr24Pixels,
    TimeSpan DecodeTime);
