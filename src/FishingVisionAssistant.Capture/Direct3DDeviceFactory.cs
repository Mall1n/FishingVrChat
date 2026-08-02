using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;

namespace FishingVisionAssistant.Capture;

/// <summary>
/// Создаёт WinRT Direct3D device, необходимый Windows.Graphics.Capture для выдачи кадров.
/// </summary>
internal static class Direct3DDeviceFactory
{
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private static readonly Guid DxgiDeviceInterfaceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static IDirect3DDevice Create()
    {
        var result = D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverType.Hardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            7,
            out var d3dDevice,
            out _,
            out var immediateContext);
        Marshal.ThrowExceptionForHR(result);

        try
        {
            return CreateFromD3D11Device(d3dDevice);
        }
        finally
        {
            if (immediateContext != IntPtr.Zero)
            {
                Marshal.Release(immediateContext);
            }

            if (d3dDevice != IntPtr.Zero)
            {
                Marshal.Release(d3dDevice);
            }
        }
    }

    private static IDirect3DDevice CreateFromD3D11Device(IntPtr d3dDevice)
    {
        var dxgiDevice = IntPtr.Zero;
        var inspectable = IntPtr.Zero;
        try
        {
            var result = Marshal.QueryInterface(d3dDevice, in DxgiDeviceInterfaceId, out dxgiDevice);
            Marshal.ThrowExceptionForHR(result);
            result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable);
            Marshal.ThrowExceptionForHR(result);
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != IntPtr.Zero)
            {
                Marshal.Release(inspectable);
            }

            if (dxgiDevice != IntPtr.Zero)
            {
                Marshal.Release(dxgiDevice);
            }
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    private enum D3DDriverType : uint
    {
        Hardware = 1
    }
}
