using System;
using Arisen.Native.RHI;

namespace ArisenEngine.Core.RHI;

public readonly struct RHISwapChain
{
    public IntPtr Handle { get; }

    public bool IsValid => Handle != IntPtr.Zero;

    public RHISwapChain(IntPtr handle)
    {
        Handle = handle;
    }

    public RHIImageHandle BeginFrame(uint frameIndex)
    {
        if (!IsValid) return RHIImageHandle.Invalid;
        ulong packed = RHISwapChainAPI.RHISwapChain_BeginFrame(Handle, frameIndex);
        unsafe { return *(RHIImageHandle*)&packed; }
    }

    public void EndFrame(uint frameIndex)
    {
        if (!IsValid) return;
        RHISwapChainAPI.RHISwapChain_EndFrame(Handle, frameIndex);
    }

    public IntPtr GetSharedWin32Handle(uint index)
    {
        if (!IsValid) return IntPtr.Zero;
        return RHISwapChainAPI.RHISwapChain_GetSharedWin32Handle(Handle, index);
    }

    public ulong GetSharedMemorySize(uint index)
    {
        if (!IsValid) return 0;
        return RHISwapChainAPI.RHISwapChain_GetSharedMemorySize(Handle, index);
    }

    public IntPtr GetRenderFinishedSemaphoreWin32Handle(uint frameIndex)
    {
        if (!IsValid) return IntPtr.Zero;
        return RHISwapChainAPI.RHISwapChain_GetRenderFinishedSemaphoreWin32Handle(Handle, frameIndex);
    }

    public IntPtr CreateConsumedSemaphoreWin32Handle(uint frameIndex)
    {
        if (!IsValid) return IntPtr.Zero;
        return RHISwapChainAPI.RHISwapChain_CreateConsumedSemaphoreWin32Handle(Handle, frameIndex);
    }

    public void CompleteConsumedSemaphoreWin32Handle(IntPtr handle)
    {
        if (!IsValid || handle == IntPtr.Zero) return;
        RHISwapChainAPI.RHISwapChain_CompleteConsumedSemaphoreWin32Handle(Handle, handle);
    }

    public void ReleaseConsumedSemaphoreWin32Handle(IntPtr handle)
    {
        if (!IsValid || handle == IntPtr.Zero) return;
        RHISwapChainAPI.RHISwapChain_ReleaseConsumedSemaphoreWin32Handle(Handle, handle);
    }

    public bool AcknowledgeExternalConsumerRelease()
    {
        return IsValid && RHISwapChainAPI.RHISwapChain_AcknowledgeExternalConsumerRelease(Handle);
    }

    public RHIImageViewHandle GetImageView(uint frameIndex)
    {
        if (!IsValid) return RHIImageViewHandle.Invalid;
        ulong packed = RHISwapChainAPI.RHISwapChain_GetImageView(Handle, frameIndex);
        unsafe { return *(RHIImageViewHandle*)&packed; }
    }

    public void SetResolution(uint width, uint height)
    {
        if (!IsValid) return;
        RHISwapChainAPI.RHISwapChain_SetResolution(Handle, width, height);
    }
}
