using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Описывает результат одного live inference и задержку от получения кадра до готового решения.
/// </summary>
public sealed record LiveFrameAnalysis(
    long SequenceNumber,
    PanelDetectionResult PanelDetection,
    TimeSpan QueueTime,
    TimeSpan EndToEndTime,
    bool IncludesDiagnostics);
