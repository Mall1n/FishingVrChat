namespace FishingVisionAssistant.Core;

/// <summary>
/// Преобразует состояние mini-game в объяснимую визуальную рекомендацию без воздействия на ввод.
/// </summary>
public interface IAdviceController
{
    /// <summary>
    /// Оценивает состояние текущего кадра и возвращает безопасное решение.
    /// </summary>
    AdviceDecision Evaluate(DetectionSnapshot snapshot);
}
