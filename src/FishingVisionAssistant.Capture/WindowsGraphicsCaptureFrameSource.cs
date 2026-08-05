using System.Diagnostics;
using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace FishingVisionAssistant.Capture;

/// <summary>
/// Захватывает выбранное окно или монитор через Windows.Graphics.Capture и извлекает BGRA32 через D3D11 staging textures.
/// </summary>
public sealed class WindowsGraphicsCaptureFrameSource : IFrameSource, IPausableFrameSource
{
    private readonly GraphicsCaptureItem _item;
    private readonly Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly Channel<CapturedFrame> _frames;
    private readonly CaptureReadbackBridge _readbackBridge = new();
    private readonly object _sync = new();
    private Task? _copyTask;
    private long _sequenceNumber;
    private int _hasQueuedFrame;
    private bool _isPaused;
    private bool _isDisposed;

    public WindowsGraphicsCaptureFrameSource(GraphicsCaptureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows.Graphics.Capture не поддерживается этой системой.");
        }

        _item = item;
        _device = Direct3DDeviceFactory.Create();
        _frames = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        _session = _framePool.CreateCaptureSession(item);
        Descriptor = new FrameSourceDescriptor(item.DisplayName, item.DisplayName, FrameSourceKind.Window);
        _framePool.FrameArrived += FramePool_FrameArrived;
        _item.Closed += Item_Closed;
        _session.StartCapture();
    }

    /// <inheritdoc />
    public FrameSourceDescriptor Descriptor { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<CapturedFrame> ReadFramesAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Exchange(ref _hasQueuedFrame, 0);
            yield return frame;
        }
    }

    /// <inheritdoc />
    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _isPaused;
            }
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _isPaused = true;
        }
    }

    /// <inheritdoc />
    public void Resume()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _isPaused = false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? copyTask;
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _framePool.FrameArrived -= FramePool_FrameArrived;
            _item.Closed -= Item_Closed;
            _frames.Writer.TryComplete();
            copyTask = _copyTask;
        }

        if (copyTask is not null)
        {
            await copyTask;
        }

        _readbackBridge.Dispose();
        _session.Dispose();
        _framePool.Dispose();
        _device.Dispose();
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_sync)
        {
            if (_isDisposed || _copyTask is { IsCompleted: false })
            {
                return;
            }

            if (_isPaused)
            {
                // Освобождаем surface, чтобы frame pool продолжил выдавать события после Resume.
                using var ignoredFrame = sender.TryGetNextFrame();
                return;
            }

            _copyTask = CopyLatestFrameAsync(sender);
        }
    }

    private Task CopyLatestFrameAsync(Direct3D11CaptureFramePool sender)
    {
        var captureTimestamp = DateTimeOffset.UtcNow;
        var copyStartedAt = Stopwatch.GetTimestamp();
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || _isDisposed)
            {
                return Task.CompletedTask;
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width <= 0 || contentSize.Height <= 0)
            {
                return Task.CompletedTask;
            }

            // Пока detector занят, один CPU-кадр уже ожидает обработки. Новый surface освобождается
            // без staging readback: иначе он будет вытеснен из latest-frame queue до inference.
            if (Interlocked.CompareExchange(ref _hasQueuedFrame, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }

            var sourceToken = Interlocked.Increment(ref _sequenceNumber);
            var requiredBytes = checked(contentSize.Width * contentSize.Height * 4);
            var pixels = GC.AllocateUninitializedArray<byte>(requiredBytes);

            var readback = _readbackBridge.Read(
                frame.Surface,
                pixels);
            var readyTimestamp = DateTimeOffset.UtcNow;
            if (!_frames.Writer.TryWrite(new CapturedFrame(
                sourceToken,
                captureTimestamp,
                Stopwatch.GetElapsedTime(copyStartedAt),
                readyTimestamp,
                readback.Width,
                readback.Height,
                readback.Stride,
                FramePixelFormat.Bgra32,
                pixels)))
            {
                Interlocked.Exchange(ref _hasQueuedFrame, 0);
            }
        }
        catch (Exception exception) when (!_isDisposed)
        {
            Interlocked.Exchange(ref _hasQueuedFrame, 0);
            _frames.Writer.TryComplete(exception);
        }
        catch (Exception) when (_isDisposed)
        {
            Interlocked.Exchange(ref _hasQueuedFrame, 0);
        }

        return Task.CompletedTask;
    }

    private void Item_Closed(GraphicsCaptureItem sender, object args) => _frames.Writer.TryComplete();
}
