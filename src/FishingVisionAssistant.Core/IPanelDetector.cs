namespace FishingVisionAssistant.Core;

/// <summary>
/// Находит рамку мини-игры на закодированном кадре и формирует диагностический результат.
/// </summary>
public interface IPanelDetector
{
    /// <summary>
    /// Анализирует PNG, JPEG или BMP и возвращает найденную геометрию с preview-изображениями.
    /// </summary>
    PanelDetectionResult Detect(ReadOnlyMemory<byte> encodedImage);
}
