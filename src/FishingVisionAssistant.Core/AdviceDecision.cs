namespace FishingVisionAssistant.Core;

/// <summary>
/// Описывает решение controller и причину, отображаемую в диагностическом интерфейсе.
/// </summary>
public sealed record AdviceDecision(
    AdviceState State,
    string Reason,
    double Confidence,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Создаёт безопасное решение при отсутствии достоверных данных.
    /// </summary>
    public static AdviceDecision Unknown(string reason, DateTimeOffset timestamp) =>
        new(AdviceState.Unknown, reason, 0, timestamp);
}
