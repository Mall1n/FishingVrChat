namespace FishingVisionAssistant.Core;

/// <summary>
/// Выбирает независимые preview, которые detector должен построить после inference.
/// </summary>
[Flags]
public enum PanelPreviewOutputs
{
    /// <summary>
    /// Не создаёт preview и оставляет только геометрию и метрики fast path.
    /// </summary>
    None = 0,

    /// <summary>
    /// Создаёт исходный кадр с найденной OBB или сообщением об отсутствии панели.
    /// </summary>
    SourceOverlay = 1 << 0,

    /// <summary>
    /// Создаёт letterbox-вход модели с диагностикой всех OBB-кандидатов.
    /// </summary>
    OnnxDiagnostic = 1 << 1,

    /// <summary>
    /// Создаёт perspective-corrected изображение найденной шкалы.
    /// </summary>
    RectifiedPanel = 1 << 2,

    /// <summary>
    /// Создаёт полный набор preview для offline-анализа.
    /// </summary>
    All = SourceOverlay | OnnxDiagnostic | RectifiedPanel
}
