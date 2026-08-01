namespace FishingVisionAssistant.Core;

/// <summary>
/// Представляет координату изображения в пикселях без зависимости публичного API от OpenCV.
/// </summary>
public readonly record struct ImagePoint(double X, double Y);
