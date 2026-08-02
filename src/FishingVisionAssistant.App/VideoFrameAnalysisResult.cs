using FishingVisionAssistant.Capture;

namespace FishingVisionAssistant.App;

/// <summary>
/// Объединяет diagnostic result offline-кадра и необязательный исходный buffer для текущей разметки.
/// </summary>
public sealed record VideoFrameAnalysisResult(
    VideoFrameAnalysis Analysis,
    VideoFrame? SourceFrame);
