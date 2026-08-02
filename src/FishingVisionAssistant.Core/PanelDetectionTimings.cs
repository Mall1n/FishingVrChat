namespace FishingVisionAssistant.Core;

/// <summary>
/// Разделяет время neural detector на стадии подготовки входа, выполнение модели и обработку результата.
/// </summary>
public sealed record PanelDetectionTimings(
    TimeSpan Preprocess,
    TimeSpan ColorConversion,
    TimeSpan Letterbox,
    TimeSpan TensorCreation,
    TimeSpan Inference,
    TimeSpan Postprocess);
