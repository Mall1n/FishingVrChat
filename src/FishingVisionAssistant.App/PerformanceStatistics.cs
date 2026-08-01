namespace FishingVisionAssistant.App;

/// <summary>
/// Накапливает ограниченную историю latency и рассчитывает cold start, median и p95.
/// </summary>
public sealed class PerformanceStatistics
{
    private const int MaximumSamples = 500;
    private readonly Queue<double> _samples = new();
    private double? _coldStartMilliseconds;

    public void Add(TimeSpan duration)
    {
        var milliseconds = duration.TotalMilliseconds;
        if (_coldStartMilliseconds is null)
        {
            _coldStartMilliseconds = milliseconds;
            return;
        }

        _samples.Enqueue(milliseconds);
        while (_samples.Count > MaximumSamples)
        {
            _samples.Dequeue();
        }
    }

    public PerformanceSnapshot GetSnapshot()
    {
        if (_samples.Count == 0)
        {
            return new PerformanceSnapshot(0, _coldStartMilliseconds ?? 0, 0, 0);
        }

        var sorted = _samples.Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
        var percentile95Index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);

        return new PerformanceSnapshot(
            sorted.Length,
            _coldStartMilliseconds!.Value,
            median,
            sorted[percentile95Index]);
    }
}
