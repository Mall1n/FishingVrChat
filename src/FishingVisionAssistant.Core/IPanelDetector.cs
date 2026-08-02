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

    /// <summary>
    /// Анализирует декодированный BGR24-кадр без промежуточного PNG или JPEG.
    /// </summary>
    PanelDetectionResult DetectBgr24(
        byte[] pixels,
        int width,
        int height,
        int stride,
        PanelPreviewOutputs previewOutputs = PanelPreviewOutputs.All);

    /// <summary>
    /// Анализирует BGRA32-кадр live capture без промежуточного кодирования изображения.
    /// </summary>
    PanelDetectionResult DetectBgra32(
        byte[] pixels,
        int width,
        int height,
        int stride,
        PanelPreviewOutputs previewOutputs = PanelPreviewOutputs.All);
}
