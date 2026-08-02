using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FishingVisionAssistant.Capture;

/// <summary>
/// Захватывает выбранное окно или монитор через Windows.Graphics.Capture и хранит только свежий кадр.
/// </summary>
public sealed class WindowsGraphicsCaptureFrameSource : IFrameSource, IPausableFrameSource
{
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly Channel<CapturedFrame> _frames;
    private readonly object _sync = new();
    private Task? _copyTask;
    private long _sequenceNumber;
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
            FullMode = BoundedChannelFullMode.DropOldest,
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
    public IAsyncEnumerable<CapturedFrame> ReadFramesAsync(CancellationToken cancellationToken = default) =>
        _frames.Reader.ReadAllAsync(cancellationToken);

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

    private async Task CopyLatestFrameAsync(Direct3D11CaptureFramePool sender)
    {
        var captureTimestamp = DateTimeOffset.UtcNow;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || _isDisposed)
            {
                return;
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width <= 0 || contentSize.Height <= 0)
            {
                return;
            }

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface,
                BitmapAlphaMode.Ignore);
            using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? null
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var bgraBitmap = converted ?? bitmap;
            var byteCount = checked((uint)(bgraBitmap.PixelWidth * bgraBitmap.PixelHeight * 4));
            var buffer = new Windows.Storage.Streams.Buffer(byteCount);
            bgraBitmap.CopyToBuffer(buffer);
            buffer.Length = byteCount;
            using var reader = DataReader.FromBuffer(buffer);
            var pixels = new byte[byteCount];
            reader.ReadBytes(pixels);

            _frames.Writer.TryWrite(new CapturedFrame(
                Interlocked.Increment(ref _sequenceNumber),
                captureTimestamp,
                bgraBitmap.PixelWidth,
                bgraBitmap.PixelHeight,
                checked(bgraBitmap.PixelWidth * 4),
                FramePixelFormat.Bgra32,
                pixels));
        }
        catch (Exception exception) when (!_isDisposed)
        {
            _frames.Writer.TryComplete(exception);
        }
        catch (Exception) when (_isDisposed)
        {
        }
    }

    private void Item_Closed(GraphicsCaptureItem sender, object args) => _frames.Writer.TryComplete();
}
