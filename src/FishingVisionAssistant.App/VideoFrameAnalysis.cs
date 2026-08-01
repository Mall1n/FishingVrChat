using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Объединяет позицию видеокадра, время декодирования и diagnostic result detector.
/// </summary>
public sealed record VideoFrameAnalysis(
    long FrameIndex,
    TimeSpan Position,
    TimeSpan DecodeTime,
    PanelDetectionResult PanelDetection,
    bool IsFromCache)
{
    public TimeSpan ProcessingTime => DecodeTime + PanelDetection.ProcessingTime;
}
