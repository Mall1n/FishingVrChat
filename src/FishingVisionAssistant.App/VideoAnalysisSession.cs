using FishingVisionAssistant.Capture;
using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Координирует seekable video source, detector и LRU-кэш в рамках одной offline-сессии.
/// </summary>
public sealed class VideoAnalysisSession : IDisposable
{
    private readonly FrameAnalysisCache _cache = new(24);
    private IPanelDetector _panelDetector;
    private readonly ISeekableVideoSource _videoSource;

    public VideoAnalysisSession(ISeekableVideoSource videoSource, IPanelDetector panelDetector)
    {
        _videoSource = videoSource;
        _panelDetector = panelDetector;
    }

    public VideoMetadata Metadata => _videoSource.Metadata;

    public VideoFrameAnalysisResult? AnalyzeFrame(long frameIndex, bool includeSourceFrame)
    {
        if (!includeSourceFrame && _cache.TryGet(frameIndex, out var cached))
        {
            return new VideoFrameAnalysisResult(cached, null);
        }

        // Для разметки detector и исходный buffer должны быть получены из одного декодирования.
        var frame = _videoSource.ReadFrame(frameIndex);
        if (frame is null)
        {
            return null;
        }

        var detection = _panelDetector.DetectBgr24(
            frame.Bgr24Pixels,
            frame.Width,
            frame.Height,
            frame.Stride);
        var analysis = new VideoFrameAnalysis(
            frame.FrameIndex,
            frame.Position,
            frame.DecodeTime,
            detection,
            false);
        _cache.Add(analysis);
        return new VideoFrameAnalysisResult(analysis, includeSourceFrame ? frame : null);
    }

    public void UpdateDetector(IPanelDetector panelDetector)
    {
        _panelDetector = panelDetector ?? throw new ArgumentNullException(nameof(panelDetector));
        _cache.Clear();
    }

    public VideoFrame? ReadFrame(long frameIndex) => _videoSource.ReadFrame(frameIndex);

    public void Dispose() => _videoSource.Dispose();
}
