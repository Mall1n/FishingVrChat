#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <Windows.h>

#ifdef FVA_CAPTURE_BRIDGE_EXPORTS
#define FVA_CAPTURE_BRIDGE_API extern "C" __declspec(dllexport)
#else
#define FVA_CAPTURE_BRIDGE_API extern "C" __declspec(dllimport)
#endif

/// <summary>
/// Создаёт native D3D11 readback bridge для обычного Windows.Graphics.Capture device.
/// </summary>
FVA_CAPTURE_BRIDGE_API HRESULT FvaCaptureReadbackBridgeCreate(void** bridge);

/// <summary>
/// Копирует WinRT surface в staging texture, ожидает завершение GPU copy и заполняет BGRA32 buffer.
/// </summary>
FVA_CAPTURE_BRIDGE_API HRESULT FvaCaptureReadbackBridgeReadSurface(
    void* bridge,
    void* inspectableSurface,
    BYTE* output,
    UINT outputCapacity,
    UINT* width,
    UINT* height,
    UINT* stride);

/// <summary>
/// Освобождает staging texture и связанные native D3D11 объекты.
/// </summary>
FVA_CAPTURE_BRIDGE_API void FvaCaptureReadbackBridgeDestroy(void* bridge);
