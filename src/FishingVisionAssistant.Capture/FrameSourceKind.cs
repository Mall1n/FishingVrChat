namespace FishingVisionAssistant.Capture;

/// <summary>
/// Различает offline-файлы и live-источники без привязки к конкретному API захвата.
/// </summary>
public enum FrameSourceKind
{
    Image,
    Video,
    Window,
    Monitor
}
