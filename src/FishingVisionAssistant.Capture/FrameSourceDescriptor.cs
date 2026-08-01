namespace FishingVisionAssistant.Capture;

/// <summary>
/// Описывает выбранный источник кадров для отображения и диагностического журнала.
/// </summary>
public sealed record FrameSourceDescriptor(string Id, string DisplayName, FrameSourceKind Kind);
