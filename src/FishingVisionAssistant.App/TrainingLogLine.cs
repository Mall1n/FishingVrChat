namespace FishingVisionAssistant.App;

/// <summary>
/// Представляет строку журнала ML-процесса и определяет, заменяет ли она предыдущий progress bar.
/// </summary>
public sealed record TrainingLogLine(string Text, bool ReplacesPreviousProgress)
{
    private const string ClearLineEscapeSequence = "\u001B[K";

    /// <summary>
    /// Нормализует строку вывода консоли для отображения в журнале.
    /// </summary>
    public static TrainingLogLine Create(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = text.Replace(ClearLineEscapeSequence, string.Empty, StringComparison.Ordinal);
        var replacesPreviousProgress = text.Contains(ClearLineEscapeSequence, StringComparison.Ordinal) &&
                                       (normalized.IndexOfAny(['━', '─', '╸', '█']) >= 0 ||
                                        (normalized.Contains("it/s", StringComparison.Ordinal) &&
                                         normalized.Contains('/')));
        return new TrainingLogLine(normalized, replacesPreviousProgress);
    }
}
