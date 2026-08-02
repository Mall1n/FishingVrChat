using System.Windows;

namespace FishingVisionAssistant.App;

/// <summary>
/// Редактирует два deployment-порога ONNX detector без изменения весов модели.
/// </summary>
public partial class OnnxGateSettingsWindow : Window
{
    private const double DefaultMinimumConfidence = 0.5;
    private const double DefaultMinimumAspectRatio = 10;

    public OnnxGateSettingsWindow(double minimumConfidence, double minimumAspectRatio)
    {
        InitializeComponent();
        ConfidenceSlider.Value = Math.Clamp(minimumConfidence, ConfidenceSlider.Minimum, ConfidenceSlider.Maximum);
        AspectRatioSlider.Value = Math.Clamp(
            minimumAspectRatio,
            AspectRatioSlider.Minimum,
            AspectRatioSlider.Maximum);
        UpdateValues();
    }

    /// <summary>
    /// Возвращает подтверждённый пользователем минимальный confidence.
    /// </summary>
    public double MinimumConfidence { get; private set; }

    /// <summary>
    /// Возвращает подтверждённое пользователем минимальное отношение сторон OBB.
    /// </summary>
    public double MinimumAspectRatio { get; private set; }

    private void GateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConfidenceValueText is not null && AspectRatioValueText is not null)
        {
            UpdateValues();
        }
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        ConfidenceSlider.Value = DefaultMinimumConfidence;
        AspectRatioSlider.Value = DefaultMinimumAspectRatio;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        MinimumConfidence = ConfidenceSlider.Value;
        MinimumAspectRatio = AspectRatioSlider.Value;
        DialogResult = true;
    }

    private void UpdateValues()
    {
        ConfidenceValueText.Text = ConfidenceSlider.Value.ToString("P0");
        AspectRatioValueText.Text = AspectRatioSlider.Value.ToString("F1");
    }
}
