using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace FishingVisionAssistant.Capture;

/// <summary>
/// Выполняет прямой D3D11 readback capture surface через staging texture.
/// </summary>
internal sealed class CaptureReadbackBridge : IDisposable
{
    private IntPtr _bridge;

    public CaptureReadbackBridge()
    {
        ThrowIfFailed(FvaCaptureReadbackBridgeCreate(out _bridge));
    }

    /// <summary>
    /// Копирует surface и возвращает готовый BGRA32 buffer.
    /// </summary>
    public CaptureReadbackResult Read(
        IDirect3DSurface surface,
        byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_bridge == IntPtr.Zero, this);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(buffer);
        if (surface is not WinRT.IWinRTObject winRtSurface)
        {
            throw new InvalidOperationException("Windows.Graphics.Capture вернул surface без WinRT ABI.");
        }

        var inspectable = winRtSurface
            .GetObjectReferenceForType(typeof(IDirect3DSurface).TypeHandle)
            .ThisPtr;
        var result = FvaCaptureReadbackBridgeReadSurface(
            _bridge,
            inspectable,
            buffer,
            checked((uint)buffer.Length),
            out var width,
            out var height,
            out var stride);

        ThrowIfFailed(result);
        return new CaptureReadbackResult(
            checked((int)width),
            checked((int)height),
            checked((int)stride));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var bridge = Interlocked.Exchange(ref _bridge, IntPtr.Zero);
        if (bridge != IntPtr.Zero)
        {
            FvaCaptureReadbackBridgeDestroy(bridge);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    [DllImport("FishingVisionAssistant.CaptureBridge.dll", ExactSpelling = true)]
    private static extern int FvaCaptureReadbackBridgeCreate(out IntPtr bridge);

    [DllImport("FishingVisionAssistant.CaptureBridge.dll", ExactSpelling = true)]
    private static extern int FvaCaptureReadbackBridgeReadSurface(
        IntPtr bridge,
        IntPtr inspectableSurface,
        [Out] byte[] output,
        uint outputCapacity,
        out uint width,
        out uint height,
        out uint stride);

    [DllImport("FishingVisionAssistant.CaptureBridge.dll", ExactSpelling = true)]
    private static extern void FvaCaptureReadbackBridgeDestroy(IntPtr bridge);
}

/// <summary>
/// Описывает CPU buffer, извлечённый из staging texture.
/// </summary>
internal sealed record CaptureReadbackResult(int Width, int Height, int Stride);
