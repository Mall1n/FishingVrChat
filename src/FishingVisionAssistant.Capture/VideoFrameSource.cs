using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace FishingVisionAssistant.Capture;

/// <summary>
/// Декодирует локальное видео через OpenCV и поддерживает последовательное чтение и seek по кадрам.
/// </summary>
public sealed class VideoFrameSource : ISeekableVideoSource
{
    private readonly object _sync = new();
    private readonly VideoCapture _capture;
    private long? _lastReadFrameIndex;
    private bool _disposed;

    public VideoFrameSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Видеофайл не найден.", path);
        }

        _capture = new VideoCapture(path);
        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            throw new InvalidDataException("OpenCV не удалось открыть видеозапись.");
        }

        var framesPerSecond = _capture.Get(VideoCaptureProperties.Fps);
        var frameCount = (long)Math.Round(_capture.Get(VideoCaptureProperties.FrameCount));
        var width = (int)Math.Round(_capture.Get(VideoCaptureProperties.FrameWidth));
        var height = (int)Math.Round(_capture.Get(VideoCaptureProperties.FrameHeight));

        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0 || frameCount <= 0 || width <= 0 || height <= 0)
        {
            _capture.Dispose();
            throw new InvalidDataException("Видеозапись содержит неполные FPS, frame count или размеры кадра.");
        }

        Metadata = new VideoMetadata(
            Path.GetFullPath(path),
            frameCount,
            framesPerSecond,
            TimeSpan.FromSeconds(frameCount / framesPerSecond),
            width,
            height);
    }

    /// <inheritdoc />
    public VideoMetadata Metadata { get; }

    /// <inheritdoc />
    public VideoFrame ReadFrame(long frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, Metadata.FrameCount);

        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();

            // Последовательное воспроизведение не выполняет дорогой seek перед каждым кадром.
            if (_lastReadFrameIndex is null || _lastReadFrameIndex + 1 != frameIndex)
            {
                if (!_capture.Set(VideoCaptureProperties.PosFrames, frameIndex))
                {
                    throw new InvalidOperationException($"Не удалось перейти к кадру {frameIndex}.");
                }
            }

            using var decoded = new Mat();
            if (!_capture.Read(decoded) || decoded.Empty())
            {
                throw new EndOfStreamException($"Не удалось декодировать кадр {frameIndex}.");
            }

            using var bgr = NormalizeToBgr(decoded);
            using var contiguous = bgr.IsContinuous() ? null : bgr.Clone();
            var source = contiguous ?? bgr;
            var stride = checked(source.Width * 3);
            var pixels = new byte[checked(stride * source.Height)];
            Marshal.Copy(source.Data, pixels, 0, pixels.Length);

            stopwatch.Stop();
            _lastReadFrameIndex = frameIndex;
            return new VideoFrame(
                frameIndex,
                TimeSpan.FromSeconds(frameIndex / Metadata.FramesPerSecond),
                source.Width,
                source.Height,
                stride,
                pixels,
                stopwatch.Elapsed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _capture.Dispose();
            _disposed = true;
        }
    }

    private static Mat NormalizeToBgr(Mat source)
    {
        var result = new Mat();
        switch (source.Channels())
        {
            case 1:
                Cv2.CvtColor(source, result, ColorConversionCodes.GRAY2BGR);
                break;
            case 3:
                source.CopyTo(result);
                break;
            case 4:
                Cv2.CvtColor(source, result, ColorConversionCodes.BGRA2BGR);
                break;
            default:
                result.Dispose();
                throw new InvalidDataException($"Неподдерживаемое число каналов кадра: {source.Channels()}.");
        }

        return result;
    }
}
