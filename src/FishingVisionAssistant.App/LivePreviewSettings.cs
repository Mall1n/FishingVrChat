namespace FishingVisionAssistant.App;

/// <summary>
/// Управляет частотой и составом тяжёлых preview, не изменяя частоту основного live inference.
/// </summary>
/// <param name="UpdateSourcePreview">Разрешает заменять исходный кадр с OBB overlay.</param>
/// <param name="UpdateRectifiedPreview">Разрешает заменять выпрямленную шкалу найденной панели.</param>
/// <param name="UpdateOnnxDiagnosticPreview">Разрешает заменять letterbox-диагностику кандидатов ONNX.</param>
/// <param name="RefreshEveryNFrames">Задаёт число обработанных кадров между diagnostic output.</param>
public sealed record LivePreviewSettings(
    bool UpdateSourcePreview,
    bool UpdateRectifiedPreview,
    bool UpdateOnnxDiagnosticPreview,
    int RefreshEveryNFrames)
{
    /// <summary>
    /// Возвращает настройки по умолчанию: все preview обновляются на каждом четвёртом обработанном кадре.
    /// </summary>
    public static LivePreviewSettings Default { get; } = new(true, true, true, 4);

    /// <summary>
    /// Возвращает true, если хотя бы одному preview требуется diagnostic output detector.
    /// </summary>
    public bool HasActivePreview =>
        UpdateSourcePreview || UpdateRectifiedPreview || UpdateOnnxDiagnosticPreview;
}
