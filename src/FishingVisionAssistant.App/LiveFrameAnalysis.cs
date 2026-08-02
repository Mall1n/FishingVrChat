using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Описывает результат одного live inference, созданные preview и задержку до готового решения.
/// </summary>
public sealed record LiveFrameAnalysis(
    long SequenceNumber,
    PanelDetectionResult PanelDetection,
    TimeSpan QueueTime,
    TimeSpan EndToEndTime,
    PanelPreviewOutputs PreviewOutputs);
