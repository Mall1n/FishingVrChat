namespace FishingVisionAssistant.App;

/// <summary>
/// Хранит ограниченный LRU-набор сжатых diagnostic result для быстрой покадровой навигации.
/// </summary>
public sealed class FrameAnalysisCache
{
    private readonly int _capacity;
    private readonly Dictionary<long, LinkedListNode<VideoFrameAnalysis>> _entries = [];
    private readonly LinkedList<VideoFrameAnalysis> _usageOrder = [];

    public FrameAnalysisCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public bool TryGet(long frameIndex, out VideoFrameAnalysis analysis)
    {
        if (!_entries.TryGetValue(frameIndex, out var node))
        {
            analysis = null!;
            return false;
        }

        _usageOrder.Remove(node);
        _usageOrder.AddFirst(node);
        analysis = node.Value with { IsFromCache = true };
        return true;
    }

    public void Add(VideoFrameAnalysis analysis)
    {
        if (_entries.Remove(analysis.FrameIndex, out var existing))
        {
            _usageOrder.Remove(existing);
        }

        var node = _usageOrder.AddFirst(analysis with { IsFromCache = false });
        _entries.Add(analysis.FrameIndex, node);

        if (_entries.Count <= _capacity)
        {
            return;
        }

        var last = _usageOrder.Last!;
        _usageOrder.RemoveLast();
        _entries.Remove(last.Value.FrameIndex);
    }

    public void Clear()
    {
        _entries.Clear();
        _usageOrder.Clear();
    }
}
