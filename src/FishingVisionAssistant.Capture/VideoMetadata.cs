namespace FishingVisionAssistant.Capture;

/// <summary>
/// Описывает геометрию, длительность и номинальную частоту кадров seekable-видео.
/// </summary>
public sealed record VideoMetadata(
    string SourcePath,
    long FrameCount,
    double FramesPerSecond,
    TimeSpan Duration,
    int Width,
    int Height);
