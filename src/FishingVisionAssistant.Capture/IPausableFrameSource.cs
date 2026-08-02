namespace FishingVisionAssistant.Capture;

/// <summary>
/// Дополняет live-источник возможностью временно прекратить выдачу кадров без закрытия выбранного окна.
/// </summary>
public interface IPausableFrameSource
{
    /// <summary>
    /// Возвращает true, когда выдача новых кадров приостановлена.
    /// </summary>
    bool IsPaused { get; }

    /// <summary>
    /// Приостанавливает выдачу кадров, сохраняя активную capture session.
    /// </summary>
    void Pause();

    /// <summary>
    /// Возобновляет выдачу кадров из той же capture session.
    /// </summary>
    void Resume();
}
