namespace FishingVisionAssistant.Core;

/// <summary>
/// Содержит результат поиска рамки и изображения, необходимые для покадровой диагностики.
/// </summary>
public sealed record PanelDetectionResult(
    bool IsDetected,
    double Confidence,
    string Reason,
    IReadOnlyList<ImagePoint> Corners,
    byte[] OverlayPng,
    byte[] MaskPng,
    byte[]? RectifiedPanelPng,
    TimeSpan ProcessingTime);
