namespace FishingVisionAssistant.Core;

/// <summary>
/// Определяет backend, на котором ONNX Runtime выполняет inference модели detector.
/// </summary>
public enum OnnxExecutionProvider
{
    /// <summary>
    /// Сначала пробует DirectML и автоматически переходит на CPU, если GPU session недоступна.
    /// </summary>
    Auto,

    /// <summary>
    /// Выполняет inference на GPU через DirectML.
    /// </summary>
    DirectMl,

    /// <summary>
    /// Выполняет inference на CPU.
    /// </summary>
    Cpu
}
