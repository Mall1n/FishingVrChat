namespace FishingVisionAssistant.App;

/// <summary>
/// Содержит устойчивые показатели pipeline по реально обработанным, а не кэшированным кадрам.
/// </summary>
public sealed record PerformanceSnapshot(
    int SampleCount,
    double ColdStartMilliseconds,
    double MedianMilliseconds,
    double Percentile95Milliseconds)
{
    public double FramesPerSecond => MedianMilliseconds <= 0 ? 0 : 1000 / MedianMilliseconds;
}
