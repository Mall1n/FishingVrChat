namespace FishingVisionAssistant.Core;

/// <summary>
/// Настраивает ONNX OBB detector и geometry gate, отсекающий неподходящие предсказания модели.
/// </summary>
public sealed class OnnxPanelDetectorOptions
{
    /// <summary>
    /// Полный путь к экспортированной YOLO OBB модели.
    /// </summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// Минимальная уверенность модели для принятия OBB.
    /// </summary>
    public double MinimumConfidence { get; init; } = 0.5;

    /// <summary>
    /// Минимальное отношение длинной стороны рамки к короткой.
    /// </summary>
    public double MinimumAspectRatio { get; init; } = 10;

    /// <summary>
    /// Backend ONNX Runtime; DirectML использует доступный Windows GPU.
    /// </summary>
    public OnnxExecutionProvider ExecutionProvider { get; init; } = OnnxExecutionProvider.DirectMl;

    /// <summary>
    /// Индекс DirectML-устройства, обычно 0 для основного GPU.
    /// </summary>
    public int DeviceId { get; init; }
}
