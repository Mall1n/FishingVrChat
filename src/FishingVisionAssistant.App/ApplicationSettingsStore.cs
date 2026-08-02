using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishingVisionAssistant.App;

/// <summary>
/// Хранит пользовательское состояние offline-инструмента между запусками в LocalApplicationData.
/// </summary>
public sealed class ApplicationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FishingVisionAssistant",
        "settings.json");

    public ApplicationSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new ApplicationSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<ApplicationSettings>(json, JsonOptions) ?? new ApplicationSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ApplicationSettings();
        }
    }

    public bool Save(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Временный файл не влияет на корректность уже сохранённых настроек.
            }
        }
    }
}

/// <summary>
/// Описывает восстанавливаемые параметры разметки, detector и последней video-сессии.
/// </summary>
public sealed class ApplicationSettings
{
    public string? LastVideoPath { get; init; }

    public long LastVideoFrameIndex { get; init; }

    public string? DatasetRoot { get; init; }

    public DatasetSplit DatasetSplit { get; init; } = DatasetSplit.Train;

    public bool IsAnnotationModeEnabled { get; init; }

    public string? OnnxModelPath { get; init; }

    public bool UseOnnxDetector { get; init; }

    public double OnnxMinimumConfidence { get; init; } = 0.5;

    public double OnnxMinimumAspectRatio { get; init; } = 10;

    public double MinimumHue { get; init; } = 115;

    public double MaximumHue { get; init; } = 145;

    public double MinimumSaturation { get; init; } = 141;

    public double MinimumValue { get; init; } = 59;

    public int PlaybackSpeedIndex { get; init; } = 2;
}
