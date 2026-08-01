namespace FishingVisionAssistant.Core;

/// <summary>
/// Содержит нормализованное состояние мини-игры, полученное detector и tracking на одном кадре.
/// </summary>
public sealed record DetectionSnapshot(
    DateTimeOffset Timestamp,
    double PanelConfidence,
    double WhiteZoneConfidence,
    double FishConfidence,
    double? WhiteZoneTop,
    double? WhiteZoneBottom,
    double? FishCenter,
    double WhiteZoneVelocity,
    double FishVelocity)
{
    /// <summary>
    /// Показывает, что нормализованные координаты образуют допустимую геометрию.
    /// </summary>
    public bool HasValidGeometry =>
        WhiteZoneTop is >= 0 and <= 1 &&
        WhiteZoneBottom is >= 0 and <= 1 &&
        FishCenter is >= 0 and <= 1 &&
        WhiteZoneTop < WhiteZoneBottom;

    /// <summary>
    /// Возвращает минимальный confidence среди обязательных результатов detector.
    /// </summary>
    public double CombinedConfidence => Math.Min(PanelConfidence, Math.Min(WhiteZoneConfidence, FishConfidence));
}
