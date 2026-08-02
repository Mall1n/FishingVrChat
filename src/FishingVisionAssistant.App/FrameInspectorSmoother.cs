using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Усредняет метрики Frame Inspector по скользящему окну в одну секунду, сохраняя данные последнего кадра.
/// </summary>
public sealed class FrameInspectorSmoother
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(1);
    private readonly Queue<Sample> _samples = new();

    /// <summary>
    /// Добавляет результат кадра и возвращает его копию с усреднёнными метриками pipeline.
    /// </summary>
    public LiveFrameAnalysis Add(LiveFrameAnalysis analysis)
    {
        var now = DateTimeOffset.UtcNow;
        _samples.Enqueue(new Sample(now, analysis));
        while (_samples.TryPeek(out var oldest) && now - oldest.Timestamp > WindowDuration)
        {
            _samples.Dequeue();
        }

        var samples = _samples.Select(sample => sample.Analysis).ToArray();
        var timings = analysis.PanelDetection.Timings;
        var averagedTimings = timings is null
            ? null
            : new PanelDetectionTimings(
                Average(samples, sample => sample.PanelDetection.Timings?.Preprocess),
                Average(samples, sample => sample.PanelDetection.Timings?.ColorConversion),
                Average(samples, sample => sample.PanelDetection.Timings?.Letterbox),
                Average(samples, sample => sample.PanelDetection.Timings?.TensorCreation),
                Average(samples, sample => sample.PanelDetection.Timings?.Inference),
                Average(samples, sample => sample.PanelDetection.Timings?.Postprocess));
        var detection = analysis.PanelDetection with
        {
            ProcessingTime = Average(samples, sample => sample.PanelDetection.ProcessingTime),
            Timings = averagedTimings
        };

        return analysis with
        {
            PanelDetection = detection,
            CaptureCopyTime = Average(samples, sample => sample.CaptureCopyTime),
            QueueTime = Average(samples, sample => sample.QueueTime),
            EndToEndTime = Average(samples, sample => sample.EndToEndTime)
        };
    }

    /// <summary>
    /// Очищает окно при переключении режима или запуске нового live-источника.
    /// </summary>
    public void Clear() => _samples.Clear();

    private static TimeSpan Average(
        IReadOnlyList<LiveFrameAnalysis> samples,
        Func<LiveFrameAnalysis, TimeSpan?> selector)
    {
        var values = samples
            .Select(selector)
            .Where(value => value.HasValue)
            .Select(value => value!.Value.Ticks)
            .ToArray();
        return values.Length == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)values.Average());
    }

    private sealed record Sample(DateTimeOffset Timestamp, LiveFrameAnalysis Analysis);
}
