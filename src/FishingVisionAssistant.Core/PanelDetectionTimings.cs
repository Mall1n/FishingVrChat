namespace FishingVisionAssistant.Core;

/// <summary>
/// Разделяет время neural detector на подготовку tensor, выполнение модели и обработку результата.
/// </summary>
public sealed record PanelDetectionTimings(
    TimeSpan Preprocess,
    TimeSpan Inference,
    TimeSpan Postprocess);
