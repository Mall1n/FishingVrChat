using System.IO;
using System.Text;
using System.Windows;

namespace FishingVisionAssistant.App;

/// <summary>
/// Показывает полный журнал одного ML-запуска и дополняет его в реальном времени.
/// </summary>
public partial class TrainingLogWindow : Window
{
    private const int MaximumDisplayedLines = 10_000;
    private readonly LinkedList<int> _displayedLineLengths = [];
    private bool _lastLineIsProgress;

    public TrainingLogWindow(string logPath)
    {
        InitializeComponent();
        Title = $"Журнал обучения модели — {Path.GetFileName(logPath)}";
        if (File.Exists(logPath))
        {
            AppendLines(File.ReadLines(logPath, Encoding.UTF8)
                .TakeLast(MaximumDisplayedLines)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(TrainingLogLine.Create));
        }
    }

    /// <summary>
    /// Добавляет batch строк и сохраняет в TextBox только последние 10 000 строк.
    /// </summary>
    public void AppendLines(IEnumerable<TrainingLogLine> lines)
    {
        var appended = false;
        foreach (var line in lines)
        {
            AppendLine(line);
            appended = true;
        }

        if (appended)
        {
            LogTextBox.ScrollToEnd();
        }
    }

    private void AppendLine(TrainingLogLine line)
    {
        var text = line.Text + Environment.NewLine;
        if (line.ReplacesPreviousProgress &&
            _lastLineIsProgress &&
            _displayedLineLengths.Last is not null)
        {
            var replacedLength = _displayedLineLengths.Last.Value;
            LogTextBox.Select(Math.Max(0, LogTextBox.Text.Length - replacedLength), replacedLength);
            LogTextBox.SelectedText = text;
            _displayedLineLengths.Last.Value = text.Length;
        }
        else
        {
            LogTextBox.AppendText(text);
            _displayedLineLengths.AddLast(text.Length);
        }

        _lastLineIsProgress = line.ReplacesPreviousProgress;
        TrimOldLines();
    }

    private void TrimOldLines()
    {
        var removedLength = 0;
        while (_displayedLineLengths.Count > MaximumDisplayedLines)
        {
            removedLength += _displayedLineLengths.First!.Value;
            _displayedLineLengths.RemoveFirst();
        }

        if (removedLength > 0)
        {
            LogTextBox.Select(0, Math.Min(removedLength, LogTextBox.Text.Length));
            LogTextBox.SelectedText = string.Empty;
        }
    }
}
}
