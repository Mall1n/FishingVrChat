namespace FishingVisionAssistant.Core;

/// <summary>
/// Задаёт пороги достоверности, прогнозирования и безопасной области controller.
/// </summary>
public sealed class AdviceControllerOptions
{
    /// <summary>
    /// Минимальный совокупный confidence, разрешающий выдачу рекомендации.
    /// </summary>
    public double MinimumConfidence { get; init; } = 0.65;

    /// <summary>
    /// Доля высоты белой зоны, исключаемая с каждой стороны для формирования безопасной области.
    /// </summary>
    public double SafeMarginRatio { get; init; } = 0.2;

    /// <summary>
    /// Время прогнозирования относительного движения перед выбором рекомендации.
    /// </summary>
    public TimeSpan PredictionHorizon { get; init; } = TimeSpan.FromMilliseconds(80);
}
