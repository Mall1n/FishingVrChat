namespace FishingVisionAssistant.Core;

/// <summary>
/// Задаёт цветовые, геометрические и выходные параметры первого detector рамки мини-игры.
/// </summary>
public sealed class PanelDetectorOptions
{
    /// <summary>
    /// Нижняя граница синего и фиолетового Hue в шкале OpenCV от 0 до 179.
    /// </summary>
    public int MinimumHue { get; init; } = 100;

    /// <summary>
    /// Верхняя граница Hue, исключающая большую часть розового текста рядом со шкалой.
    /// </summary>
    public int MaximumHue { get; init; } = 155;

    public int MinimumSaturation { get; init; } = 85;

    public int MinimumValue { get; init; } = 35;

    /// <summary>
    /// Минимальная доля высоты кадра, занимаемая рамкой-кандидатом.
    /// </summary>
    public double MinimumHeightRatio { get; init; } = 0.18;

    public double MinimumAspectRatio { get; init; } = 8;

    public double MaximumAspectRatio { get; init; } = 32;

    public int NormalizedWidth { get; init; } = 96;

    public int NormalizedHeight { get; init; } = 640;
}
