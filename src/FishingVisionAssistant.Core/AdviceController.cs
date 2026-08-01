namespace FishingVisionAssistant.Core;

/// <summary>
/// Реализует одномерный predictive controller с безопасным состоянием при потере detector.
/// </summary>
public sealed class AdviceController : IAdviceController
{
    private readonly AdviceControllerOptions _options;
    private AdviceState _lastActionableState = AdviceState.Unknown;

    public AdviceController(AdviceControllerOptions? options = null)
    {
        _options = options ?? new AdviceControllerOptions();
        ValidateOptions(_options);
    }

    /// <inheritdoc />
    public AdviceDecision Evaluate(DetectionSnapshot snapshot)
    {
        if (!snapshot.HasValidGeometry)
        {
            return AdviceDecision.Unknown("Detector не сформировал допустимую геометрию.", snapshot.Timestamp);
        }

        if (snapshot.CombinedConfidence < _options.MinimumConfidence)
        {
            return AdviceDecision.Unknown("Недостаточный confidence detector.", snapshot.Timestamp);
        }

        var zoneTop = snapshot.WhiteZoneTop!.Value;
        var zoneBottom = snapshot.WhiteZoneBottom!.Value;
        var fishCenter = snapshot.FishCenter!.Value;
        var zoneHeight = zoneBottom - zoneTop;
        var safeTop = zoneTop + zoneHeight * _options.SafeMarginRatio;
        var safeBottom = zoneBottom - zoneHeight * _options.SafeMarginRatio;

        // Прогноз компенсирует задержку захвата и обработки, используя относительную скорость объектов.
        var relativeVelocity = snapshot.FishVelocity - snapshot.WhiteZoneVelocity;
        var predictedFishCenter = fishCenter + relativeVelocity * _options.PredictionHorizon.TotalSeconds;

        if (predictedFishCenter < safeTop)
        {
            return Remember(AdviceState.Hold, "Рыба приближается к верхней границе безопасной области.", snapshot);
        }

        if (predictedFishCenter > safeBottom)
        {
            return Remember(AdviceState.Release, "Рыба приближается к нижней границе безопасной области.", snapshot);
        }

        if (_lastActionableState != AdviceState.Unknown)
        {
            return new AdviceDecision(
                _lastActionableState,
                "Рыба находится внутри безопасной области; рекомендация сохранена для hysteresis.",
                snapshot.CombinedConfidence,
                snapshot.Timestamp);
        }

        var initialState = predictedFishCenter < (safeTop + safeBottom) / 2
            ? AdviceState.Hold
            : AdviceState.Release;

        return Remember(initialState, "Выбрана начальная рекомендация относительно центра безопасной области.", snapshot);
    }

    private AdviceDecision Remember(AdviceState state, string reason, DetectionSnapshot snapshot)
    {
        _lastActionableState = state;
        return new AdviceDecision(state, reason, snapshot.CombinedConfidence, snapshot.Timestamp);
    }

    private static void ValidateOptions(AdviceControllerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumConfidence, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MinimumConfidence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SafeMarginRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(options.SafeMarginRatio, 0.5);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PredictionHorizon, TimeSpan.Zero);
    }
}
