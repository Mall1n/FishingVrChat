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
        RemoveCopiesFromOtherSplits(datasetRoot, sampleId, sample.Split);
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
        var sampleId = CreateSampleId(sourcePath, frameIndex);
        var matches = new List<ObbDatasetExistingSample>();
        foreach (var split in Enum.GetValues<DatasetSplit>())
        {
            var splitName = split.ToString().ToLowerInvariant();
            var metadataPath = Path.Combine(datasetRoot, "metadata", splitName, $"{sampleId}.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<ObbDatasetMetadata>(json, JsonOptions);
            if (metadata is null ||
                !Enum.TryParse<ObbAnnotationKind>(metadata.AnnotationKind, ignoreCase: true, out var kind))
            {
                continue;
            }

            matches.Add(new ObbDatasetExistingSample(sampleId, split, kind, metadata.Corners, metadataPath));
        }

        return matches;
    }

    public Task<ObbDatasetDeleteResult> DeleteExistingAsync(
        string datasetRoot,
        string sourcePath,
        long? frameIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        var sampleId = CreateSampleId(sourcePath, frameIndex);
        var deletedFiles = 0;
        var affectedSplits = new List<DatasetSplit>();
        foreach (var split in Enum.GetValues<DatasetSplit>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var splitName = split.ToString().ToLowerInvariant();
            var paths = new[]
            {
                Path.Combine(datasetRoot, "images", splitName, $"{sampleId}.png"),
                Path.Combine(datasetRoot, "labels", splitName, $"{sampleId}.txt"),
                Path.Combine(datasetRoot, "metadata", splitName, $"{sampleId}.json")
            };
            var splitHadFiles = false;
            foreach (var path in paths.Where(File.Exists))
            {
                File.Delete(path);
                deletedFiles++;
                splitHadFiles = true;
            }

            if (splitHadFiles)
            {
                affectedSplits.Add(split);
            }
        }

        return Task.FromResult(new ObbDatasetDeleteResult(sampleId, deletedFiles, affectedSplits));
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

    private static void RemoveCopiesFromOtherSplits(
        string datasetRoot,
        string sampleId,
        DatasetSplit targetSplit)
    {
        foreach (var split in Enum.GetValues<DatasetSplit>().Where(split => split != targetSplit))
        {
            var splitName = split.ToString().ToLowerInvariant();
            File.Delete(Path.Combine(datasetRoot, "images", splitName, $"{sampleId}.png"));
            File.Delete(Path.Combine(datasetRoot, "labels", splitName, $"{sampleId}.txt"));
            File.Delete(Path.Combine(datasetRoot, "metadata", splitName, $"{sampleId}.json"));
        }
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
        var sourceName = InvalidFileNameRegex().Replace(Path.GetFileNameWithoutExtension(sourcePath), "_");
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)))[..8]
            .ToLowerInvariant();
        var frameSuffix = frameIndex is null ? "image" : $"f{frameIndex.Value + 1:D6}";
        return $"{sourceName}_{pathHash}_{frameSuffix}";
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
