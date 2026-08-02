using System.IO;
using System.Text;
using System.Windows;

namespace FishingVisionAssistant.App;

/// <summary>
/// Показывает полный журнал одного ML-запуска и дополняет его в реальном времени.
/// </summary>
public partial class TrainingLogWindow : Window
{
    public TrainingLogWindow(string logPath)
    {
        InitializeComponent();
        Title = $"Журнал обучения модели — {Path.GetFileName(logPath)}";
        if (File.Exists(logPath))
        {
            LogTextBox.Text = File.ReadAllText(logPath, Encoding.UTF8);
            LogTextBox.ScrollToEnd();
        }
    }

    public void AppendLine(string line)
    {
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }
}
