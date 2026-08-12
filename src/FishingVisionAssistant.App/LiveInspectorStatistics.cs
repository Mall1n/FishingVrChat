using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Накапливает пять секунд raw-метрик full-frame live pipeline.
/// </summary>
public sealed class LiveInspectorStatistics
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(5);
    private readonly Queue<Sample> _samples = new();

    /// <summary>
    /// Добавляет raw-метрики кадра и рассчитывает rolling snapshot.
    /// </summary>
    public LiveInspectorSnapshot Add(LiveFrameAnalysis analysis)
    {
        var now = DateTimeOffset.UtcNow;
        _samples.Enqueue(new Sample(now, analysis));
        while (_samples.TryPeek(out var oldest) && now - oldest.Timestamp > WindowDuration)
        {
            _samples.Dequeue();
        }

        var allSamples = _samples.Select(sample => sample.Analysis).ToArray();
        var elapsedSeconds = Math.Max(
            1,
            (now - _samples.Peek().Timestamp).TotalSeconds);

        return new LiveInspectorSnapshot(
            allSamples.Length,
            allSamples.Length / elapsedSeconds,
            Median(allSamples, sample => sample.EndToEndTime),
            Percentile(allSamples, sample => sample.EndToEndTime, 0.95),
            Median(allSamples, sample => sample.CaptureCopyTime),
            Median(allSamples, sample => sample.QueueTime),
            MedianTimings(allSamples));
    }

    /// <summary>
    /// Очищает историю при запуске новой live session.
    /// </summary>
    public void Clear() => _samples.Clear();

    private static PanelDetectionTimings? MedianTimings(IReadOnlyList<LiveFrameAnalysis> samples)
    {
        var timings = samples
            .Select(sample => sample.PanelDetection.Timings)
            .Where(timing => timing is not null)
            .Cast<PanelDetectionTimings>()
            .ToArray();
        if (timings.Length == 0)
        {
            return null;
        }

        return new PanelDetectionTimings(
            Median(timings, timing => timing.Preprocess),
            Median(timings, timing => timing.ColorConversion),
            Median(timings, timing => timing.Letterbox),
            Median(timings, timing => timing.TensorCreation),
            Median(timings, timing => timing.Inference),
            Median(timings, timing => timing.Postprocess));
    }

    private static TimeSpan Median<T>(IReadOnlyList<T> samples, Func<T, TimeSpan> selector) =>
        Percentile(samples, selector, 0.5);

    private static TimeSpan Percentile<T>(
        IReadOnlyList<T> samples,
        Func<T, TimeSpan> selector,
        double percentile)
    {
        if (samples.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var orderedTicks = samples.Select(selector).Select(value => value.Ticks).Order().ToArray();
        var index = Math.Clamp(
            (int)Math.Ceiling(orderedTicks.Length * percentile) - 1,
            0,
            orderedTicks.Length - 1);
        return TimeSpan.FromTicks(orderedTicks[index]);
    }

    private sealed record Sample(DateTimeOffset Timestamp, LiveFrameAnalysis Analysis);
}
