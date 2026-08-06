using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Содержит rolling-показатели full-frame live pipeline.
/// </summary>
public sealed record LiveInspectorSnapshot(
    int SampleCount,
    double PipelineFramesPerSecond,
    TimeSpan MedianEndToEndTime,
    TimeSpan Percentile95EndToEndTime,
    TimeSpan MedianCaptureCopyTime,
    TimeSpan MedianQueueTime,
    PanelDetectionTimings? MedianTimings);
