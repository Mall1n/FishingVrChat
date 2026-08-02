using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Сохраняет чистые кадры, YOLO OBB labels и audit metadata выбранного split.
/// </summary>
public sealed partial class ObbDatasetWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<ObbDatasetSaveResult> SaveAsync(
        string datasetRoot,
        ObbDatasetSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);
        ArgumentNullException.ThrowIfNull(sample);
        ValidateSample(sample);
        EnsureStructure(datasetRoot);

        var splitName = sample.Split.ToString().ToLowerInvariant();
        var sampleId = CreateSampleId(sample.SourcePath, sample.FrameIndex);
        var imageDirectory = Path.Combine(datasetRoot, "images", splitName);
        var labelDirectory = Path.Combine(datasetRoot, "labels", splitName);
        var metadataDirectory = Path.Combine(datasetRoot, "metadata", splitName);

        var imagePath = Path.Combine(imageDirectory, $"{sampleId}.png");
        var labelPath = Path.Combine(labelDirectory, $"{sampleId}.txt");
        var metadataPath = Path.Combine(metadataDirectory, $"{sampleId}.json");
        await WriteBytesWithDirectoryRetryAsync(imagePath, sample.FramePng, cancellationToken);

        var orderedCorners = sample.Corners is { Count: 4 }
            ? OrderCorners(sample.Corners)
            : [];
        var label = orderedCorners.Count == 4
            ? CreateLabel(orderedCorners, sample.ImageWidth, sample.ImageHeight)
            : string.Empty;
        await WriteTextWithDirectoryRetryAsync(labelPath, label, cancellationToken);

        var metadata = new ObbDatasetMetadata(
            sampleId,
            sample.SourcePath,
            sample.FrameIndex,
            sample.Split.ToString(),
            sample.AnnotationKind.ToString(),
            sample.ImageWidth,
            sample.ImageHeight,
            orderedCorners,
            sample.LegacyDetection,
            DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        await WriteTextWithDirectoryRetryAsync(metadataPath, json, cancellationToken);
        await RemoveMatchingSamplesExceptAsync(
            datasetRoot,
            sample.SourcePath,
            sample.FrameIndex,
            sampleId,
            sample.Split,
            cancellationToken);
        return new ObbDatasetSaveResult(sampleId, imagePath, labelPath, metadataPath);
    }

    /// <summary>
    /// Создаёт полную структуру images, labels и metadata для всех dataset split.
    /// </summary>
    public void EnsureStructure(string datasetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);
        foreach (var section in new[] { "images", "labels", "metadata" })
        {
            foreach (var split in Enum.GetValues<DatasetSplit>())
            {
                Directory.CreateDirectory(Path.Combine(datasetRoot, section, split.ToString().ToLowerInvariant()));
            }
        }
    }

    public async Task<IReadOnlyList<ObbDatasetExistingSample>> FindExistingAsync(
        string datasetRoot,
        string sourcePath,
        long? frameIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var matches = new List<ObbDatasetExistingSample>();
        foreach (var split in Enum.GetValues<DatasetSplit>())
        {
            foreach (var metadataPath in EnumerateMetadataCandidates(datasetRoot, split, sourcePath, frameIndex))
            {
                var metadata = await TryReadMetadataAsync(metadataPath, cancellationToken);
                if (metadata is null ||
                    !MatchesSource(metadata, sourcePath, frameIndex) ||
                    !Enum.TryParse<ObbAnnotationKind>(metadata.AnnotationKind, ignoreCase: true, out var kind))
                {
                    continue;
                }

                var storedSampleId = Path.GetFileNameWithoutExtension(metadataPath);
                matches.Add(new ObbDatasetExistingSample(
                    storedSampleId,
                    split,
                    kind,
                    metadata.Corners,
                    metadataPath));
            }
        }

        return matches;
    }

    /// <summary>
    /// Возвращает все сохранённые annotation текущего видео для timeline и навигации между метками.
    /// </summary>
    public async Task<IReadOnlyList<ObbDatasetTimelineMarker>> FindTimelineMarkersAsync(
        string datasetRoot,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var sourceNamePrefix = CreateSourceNamePrefix(sourcePath);
        var markers = new Dictionary<long, ObbDatasetTimelineMarker>();
        foreach (var split in Enum.GetValues<DatasetSplit>())
        {
            var metadataDirectory = Path.Combine(
                datasetRoot,
                "metadata",
                split.ToString().ToLowerInvariant());
            if (!Directory.Exists(metadataDirectory))
            {
                continue;
            }

            foreach (var metadataPath in Directory.EnumerateFiles(metadataDirectory, $"{sourceNamePrefix}*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var metadata = await TryReadMetadataAsync(metadataPath, cancellationToken);
                    if (metadata?.FrameIndex is not long frameIndex ||
                        !string.Equals(metadata.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                        !Enum.TryParse<ObbAnnotationKind>(metadata.AnnotationKind, ignoreCase: true, out var kind))
                    {
                        continue;
                    }

                    markers.TryAdd(frameIndex, new ObbDatasetTimelineMarker(frameIndex, split, kind));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Повреждённый metadata-файл не должен скрывать остальные метки видео.
                }
            }
        }

        return markers.Values.OrderBy(marker => marker.FrameIndex).ToArray();
    }

    public async Task<ObbDatasetDeleteResult> DeleteExistingAsync(
        string datasetRoot,
        string sourcePath,
        long? frameIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var expectedSampleId = CreateSampleId(sourcePath, frameIndex);
        var deletedFiles = 0;
        var affectedSplits = new List<DatasetSplit>();
        foreach (var split in Enum.GetValues<DatasetSplit>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchingSampleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var metadataPath in EnumerateMetadataCandidates(datasetRoot, split, sourcePath, frameIndex))
            {
                var metadata = await TryReadMetadataAsync(metadataPath, cancellationToken);
                if (metadata is not null && MatchesSource(metadata, sourcePath, frameIndex))
                {
                    matchingSampleIds.Add(Path.GetFileNameWithoutExtension(metadataPath));
                }
            }

            // Текущий ID удаляется и при отсутствующем metadata, чтобы не оставлять неполную тройку файлов.
            matchingSampleIds.Add(expectedSampleId);
            var splitHadFiles = false;
            foreach (var sampleId in matchingSampleIds)
            {
                var deletedForSample = DeleteSampleFiles(datasetRoot, split, sampleId);
                deletedFiles += deletedForSample;
                splitHadFiles |= deletedForSample > 0;
            }

            if (splitHadFiles)
            {
                affectedSplits.Add(split);
            }
        }

        return new ObbDatasetDeleteResult(expectedSampleId, deletedFiles, affectedSplits);
    }

    private static void ValidateSample(ObbDatasetSample sample)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.SourcePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sample.ImageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sample.ImageHeight);
        ArgumentOutOfRangeException.ThrowIfZero(sample.FramePng.Length);
        if (sample.AnnotationKind != ObbAnnotationKind.Negative && sample.Corners?.Count != 4)
        {
            throw new InvalidOperationException("Positive OBB sample должен содержать четыре точки.");
        }
    }

    private static async Task RemoveMatchingSamplesExceptAsync(
        string datasetRoot,
        string sourcePath,
        long? frameIndex,
        string retainedSampleId,
        DatasetSplit retainedSplit,
        CancellationToken cancellationToken)
    {
        foreach (var split in Enum.GetValues<DatasetSplit>())
        {
            foreach (var metadataPath in EnumerateMetadataCandidates(datasetRoot, split, sourcePath, frameIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await TryReadMetadataAsync(metadataPath, cancellationToken);
                if (metadata is null || !MatchesSource(metadata, sourcePath, frameIndex))
                {
                    continue;
                }

                var storedSampleId = Path.GetFileNameWithoutExtension(metadataPath);
                if (split == retainedSplit &&
                    string.Equals(storedSampleId, retainedSampleId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DeleteSampleFiles(datasetRoot, split, storedSampleId);
            }
        }
    }

    private static IEnumerable<string> EnumerateMetadataCandidates(
        string datasetRoot,
        DatasetSplit split,
        string sourcePath,
        long? frameIndex)
    {
        var metadataDirectory = Path.Combine(
            datasetRoot,
            "metadata",
            split.ToString().ToLowerInvariant());
        if (!Directory.Exists(metadataDirectory))
        {
            return [];
        }

        var frameSuffix = CreateFrameSuffix(frameIndex);
        return Directory.EnumerateFiles(
            metadataDirectory,
            $"{CreateSourceNamePrefix(sourcePath)}*_{frameSuffix}.json");
    }

    private static async Task<ObbDatasetMetadata?> TryReadMetadataAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            return JsonSerializer.Deserialize<ObbDatasetMetadata>(json, JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool MatchesSource(
        ObbDatasetMetadata metadata,
        string sourcePath,
        long? frameIndex) =>
        metadata.FrameIndex == frameIndex &&
        string.Equals(metadata.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase);

    private static int DeleteSampleFiles(string datasetRoot, DatasetSplit split, string sampleId)
    {
        var splitName = split.ToString().ToLowerInvariant();
        var paths = new[]
        {
            Path.Combine(datasetRoot, "images", splitName, $"{sampleId}.png"),
            Path.Combine(datasetRoot, "labels", splitName, $"{sampleId}.txt"),
            Path.Combine(datasetRoot, "metadata", splitName, $"{sampleId}.json")
        };
        var deletedFiles = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            File.Delete(path);
            deletedFiles++;
        }

        return deletedFiles;
    }

    private static async Task WriteBytesWithDirectoryRetryAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllBytesAsync(path, content, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            // Каталог мог исчезнуть после подготовки dataset; один повтор безопасно восстанавливает его.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
        }
    }

    private static async Task WriteTextWithDirectoryRetryAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(path, content, Utf8WithoutBom, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, Utf8WithoutBom, cancellationToken);
        }
    }

    private static string CreateSampleId(string sourcePath, long? frameIndex)
    {
        return $"{CreateSourceSamplePrefix(sourcePath)}{CreateFrameSuffix(frameIndex)}";
    }

    private static string CreateFrameSuffix(long? frameIndex) =>
        frameIndex is null ? "image" : $"f{frameIndex.Value + 1:D6}";

    private static string CreateSourceNamePrefix(string sourcePath)
    {
        var sourceName = InvalidFileNameRegex().Replace(Path.GetFileNameWithoutExtension(sourcePath), "_");
        return $"{sourceName}_";
    }

    private static string CreateSourceSamplePrefix(string sourcePath)
    {
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)))[..8]
            .ToLowerInvariant();
        return $"{CreateSourceNamePrefix(sourcePath)}{pathHash}_";
    }

    private static string CreateLabel(
        IReadOnlyList<ImagePoint> corners,
        int imageWidth,
        int imageHeight)
    {
        var coordinates = corners.SelectMany(point => new[]
        {
            Math.Clamp(point.X / imageWidth, 0, 1),
            Math.Clamp(point.Y / imageHeight, 0, 1)
        });
        return "0 " + string.Join(
            ' ',
            coordinates.Select(value => value.ToString("0.######", CultureInfo.InvariantCulture)));
    }

    private static IReadOnlyList<ImagePoint> OrderCorners(IReadOnlyList<ImagePoint> corners)
    {
        var centerX = corners.Average(point => point.X);
        var centerY = corners.Average(point => point.Y);
        var ordered = corners
            .OrderBy(point => Math.Atan2(point.Y - centerY, point.X - centerX))
            .ToList();
        var startIndex = ordered
            .Select((point, index) => new { Point = point, Index = index })
            .MinBy(item => item.Point.X + item.Point.Y)!
            .Index;
        return ordered.Skip(startIndex).Concat(ordered.Take(startIndex)).ToArray();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}._-]+")]
    private static partial Regex InvalidFileNameRegex();
}

/// <summary>
/// Описывает split, annotation kind и исходные данные одного OBB sample.
/// </summary>
public sealed record ObbDatasetSample(
    string SourcePath,
    long? FrameIndex,
    DatasetSplit Split,
    ObbAnnotationKind AnnotationKind,
    int ImageWidth,
    int ImageHeight,
    byte[] FramePng,
    IReadOnlyList<ImagePoint>? Corners,
    LegacyDetectionMetadata? LegacyDetection);

/// <summary>
/// Фиксирует предложение legacy detector для анализа accepted, corrected и hard-negative samples.
/// </summary>
public sealed record LegacyDetectionMetadata(
    bool IsDetected,
    double Confidence,
    string Reason,
    IReadOnlyList<ImagePoint> Corners);

/// <summary>
/// Хранит audit metadata sample независимо от train label и позволяет восстановить источник разметки.
/// </summary>
public sealed record ObbDatasetMetadata(
    string SampleId,
    string SourcePath,
    long? FrameIndex,
    string Split,
    string AnnotationKind,
    int ImageWidth,
    int ImageHeight,
    IReadOnlyList<ImagePoint> Corners,
    LegacyDetectionMetadata? LegacyDetection,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Возвращает идентификатор и пути файлов, записанных одной операцией разметки.
/// </summary>
public sealed record ObbDatasetSaveResult(
    string SampleId,
    string ImagePath,
    string LabelPath,
    string MetadataPath);

/// <summary>
/// Восстанавливает split, тип и четыре точки ранее сохранённого sample текущего кадра.
/// </summary>
public sealed record ObbDatasetExistingSample(
    string SampleId,
    DatasetSplit Split,
    ObbAnnotationKind AnnotationKind,
    IReadOnlyList<ImagePoint> Corners,
    string MetadataPath);

/// <summary>
/// Представляет сохранённую annotation одного кадра для timeline и переходов внутри исходного видео.
/// </summary>
public sealed record ObbDatasetTimelineMarker(
    long FrameIndex,
    DatasetSplit Split,
    ObbAnnotationKind AnnotationKind);

/// <summary>
/// Описывает результат удаления файлов одного sample из всех dataset split.
/// </summary>
public sealed record ObbDatasetDeleteResult(
    string SampleId,
    int DeletedFiles,
    IReadOnlyList<DatasetSplit> AffectedSplits);

/// <summary>
/// Определяет независимую группу dataset, назначаемую исходному видео целиком.
/// </summary>
public enum DatasetSplit
{
    Train,
    Validation,
    Test
}

/// <summary>
/// Описывает способ получения ground-truth OBB либо отсутствие целевого объекта.
/// </summary>
public enum ObbAnnotationKind
{
    Accepted,
    Corrected,
    Manual,
    Negative
}
