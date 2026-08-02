#include "CaptureReadbackBridge.h"

#include <d3d11_4.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <cstring>
#include <memory>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

#define RETURN_IF_FAILED(expression) \
    do \
    { \
        const HRESULT result = (expression); \
        if (FAILED(result)) \
        { \
            return result; \
        } \
    } while (false)

namespace
{
    /// <summary>
    /// Хранит staging texture и выполняет прямой GPU→CPU readback текущего capture surface.
    /// </summary>
    struct CaptureReadbackBridge final
    {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<ID3D11Texture2D> stagingTexture;
        D3D11_TEXTURE2D_DESC textureDescription{};
        bool isInitialized = false;
    };

    bool IsCompatible(const CaptureReadbackBridge& bridge, const D3D11_TEXTURE2D_DESC& description)
    {
        return bridge.isInitialized &&
               bridge.textureDescription.Width == description.Width &&
               bridge.textureDescription.Height == description.Height &&
               bridge.textureDescription.Format == description.Format &&
               bridge.textureDescription.SampleDesc.Count == description.SampleDesc.Count;
    }

    HRESULT EnsureStagingTexture(CaptureReadbackBridge& bridge, ID3D11Texture2D* source)
    {
        D3D11_TEXTURE2D_DESC sourceDescription{};
        source->GetDesc(&sourceDescription);
        if (sourceDescription.Format != DXGI_FORMAT_B8G8R8A8_UNORM || sourceDescription.SampleDesc.Count != 1)
        {
            return E_NOTIMPL;
        }

        ComPtr<ID3D11Device> sourceDevice;
        source->GetDevice(&sourceDevice);
        if (!bridge.device)
        {
            bridge.device = sourceDevice;
            bridge.device->GetImmediateContext(&bridge.context);
        }
        else if (bridge.device.Get() != sourceDevice.Get())
        {
            return E_INVALIDARG;
        }

        if (IsCompatible(bridge, sourceDescription))
        {
            return S_OK;
        }

        D3D11_TEXTURE2D_DESC stagingDescription = sourceDescription;
        stagingDescription.BindFlags = 0;
        stagingDescription.MiscFlags = 0;
        stagingDescription.Usage = D3D11_USAGE_STAGING;
        stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        RETURN_IF_FAILED(bridge.device->CreateTexture2D(&stagingDescription, nullptr, &bridge.stagingTexture));
        bridge.textureDescription = sourceDescription;
        bridge.isInitialized = true;
        return S_OK;
    }
}

HRESULT FvaCaptureReadbackBridgeCreate(void** bridge)
{
    if (bridge == nullptr)
    {
        return E_POINTER;
    }

    try
    {
        *bridge = new CaptureReadbackBridge();
        return S_OK;
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
}

HRESULT FvaCaptureReadbackBridgeReadSurface(
    void* bridge,
    void* inspectableSurface,
    BYTE* output,
    UINT outputCapacity,
    UINT* width,
    UINT* height,
    UINT* stride)
{
    if (bridge == nullptr || inspectableSurface == nullptr || output == nullptr || width == nullptr || height == nullptr ||
        stride == nullptr)
    {
        return E_INVALIDARG;
    }

    auto& instance = *static_cast<CaptureReadbackBridge*>(bridge);
    try
    {
        ComPtr<Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess> surfaceAccess;
        RETURN_IF_FAILED(static_cast<IUnknown*>(inspectableSurface)->QueryInterface(IID_PPV_ARGS(&surfaceAccess)));
        ComPtr<ID3D11Texture2D> sourceTexture;
        RETURN_IF_FAILED(surfaceAccess->GetInterface(IID_PPV_ARGS(&sourceTexture)));
        RETURN_IF_FAILED(EnsureStagingTexture(instance, sourceTexture.Get()));

        const UINT outputStride = instance.textureDescription.Width * 4;
        const UINT requiredBytes = outputStride * instance.textureDescription.Height;
        if (outputCapacity < requiredBytes)
        {
            return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        }

        instance.context->CopyResource(instance.stagingTexture.Get(), sourceTexture.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        RETURN_IF_FAILED(instance.context->Map(instance.stagingTexture.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        for (UINT row = 0; row < instance.textureDescription.Height; ++row)
        {
            std::memcpy(
                output + static_cast<size_t>(row) * outputStride,
                static_cast<const BYTE*>(mapped.pData) + static_cast<size_t>(row) * mapped.RowPitch,
                outputStride);
        }
        instance.context->Unmap(instance.stagingTexture.Get(), 0);
        *width = instance.textureDescription.Width;
        *height = instance.textureDescription.Height;
        *stride = outputStride;
        return S_OK;
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }
}

void FvaCaptureReadbackBridgeDestroy(void* bridge)
{
    delete static_cast<CaptureReadbackBridge*>(bridge);
}
