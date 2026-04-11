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

    public IntPtr GetSharedWin32Handle(uint index)
    {
        if (!IsValid) return IntPtr.Zero;
        return RHISwapChainAPI.RHISwapChain_GetSharedWin32Handle(Handle, index);
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
