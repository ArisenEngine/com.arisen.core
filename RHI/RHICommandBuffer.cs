using Arisen.Native.RHI;
using System.Runtime.InteropServices;

namespace ArisenEngine.Core.RHI;

[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct RHIClearValue
{
    [FieldOffset(0)] public float R;
    [FieldOffset(4)] public float G;
    [FieldOffset(8)] public float B;
    [FieldOffset(12)] public float A;

    [FieldOffset(0)] public float Depth;
    [FieldOffset(4)] public uint Stencil;

    public static RHIClearValue Color(float r, float g, float b, float a)
        => new RHIClearValue { R = r, G = g, B = b, A = a };

    public static RHIClearValue DepthStencil(float depth, uint stencil)
        => new RHIClearValue { Depth = depth, Stencil = stencil };
}

public readonly struct RHICommandBuffer
{
    private readonly uint _frameIndex;

    internal IntPtr NativePtr { get; }
    public RHICommandBufferHandle RHIHandle { get; }

    public bool IsValid => NativePtr != IntPtr.Zero;

    internal RHICommandBuffer(uint frameIndex, IntPtr nativePtr, RHICommandBufferHandle handle)
    {
        _frameIndex = frameIndex;
        NativePtr = nativePtr;
        RHIHandle = handle;
    }

    public void Begin()
    {
        RHICommandBufferAPI.RHICommandBuffer_Begin(NativePtr, _frameIndex);
    }

    public void End()
    {
        RHICommandBufferAPI.RHICommandBuffer_End(NativePtr);
    }

    public unsafe void BeginRenderPass(RHIRenderPassHandle renderPass, RHIFrameBufferHandle frameBuffer,
        ESubpassContents contents, RHIClearValue[] clearValues)
    {
        fixed (RHIClearValue* pValues = clearValues)
        {
            RHICommandBufferAPI.RHICommandBuffer_BeginRenderPass(NativePtr, renderPass, frameBuffer, (int)contents,
                (uint)clearValues.Length, (IntPtr)pValues);
        }
    }

    public void EndRenderPass()
    {
        RHICommandBufferAPI.RHICommandBuffer_EndRenderPass(NativePtr);
    }

    public void BeginRendering(RHIImageViewHandle colorImageView, EImageLayout imageLayout,
        EAttachmentLoadOp loadOp, EAttachmentStoreOp storeOp,
        float clearR, float clearG, float clearB, float clearA,
        int x, int y, uint width, uint height)
    {
        RHICommandBufferAPI.RHICommandBuffer_BeginRendering(NativePtr,
            colorImageView.Index, colorImageView.Generation,
            (int)imageLayout, (int)loadOp, (int)storeOp,
            clearR, clearG, clearB, clearA,
            x, y, width, height);
    }

    public void BeginRendering(
        RHIImageViewHandle colorImageView,
        EImageLayout imageLayout,
        EAttachmentLoadOp loadOp,
        EAttachmentStoreOp storeOp,
        float clearR,
        float clearG,
        float clearB,
        float clearA,
        RHIImageViewHandle depthImageView,
        EImageLayout depthImageLayout,
        EAttachmentLoadOp depthLoadOp,
        EAttachmentStoreOp depthStoreOp,
        float clearDepth,
        uint clearStencil,
        int x,
        int y,
        uint width,
        uint height)
    {
        RHICommandBufferAPI.RHICommandBuffer_BeginRenderingWithDepth(
            NativePtr,
            colorImageView.Index,
            colorImageView.Generation,
            (int)imageLayout,
            (int)loadOp,
            (int)storeOp,
            clearR,
            clearG,
            clearB,
            clearA,
            depthImageView.Index,
            depthImageView.Generation,
            (int)depthImageLayout,
            (int)depthLoadOp,
            (int)depthStoreOp,
            clearDepth,
            clearStencil,
            x,
            y,
            width,
            height);
    }

    public void EndRendering()
    {
        RHICommandBufferAPI.RHICommandBuffer_EndRendering(NativePtr);
    }

    public void BeginRenderingDepthOnly(
        RHIImageViewHandle depthImageView,
        EImageLayout depthImageLayout,
        EAttachmentLoadOp depthLoadOp,
        EAttachmentStoreOp depthStoreOp,
        float clearDepth,
        uint clearStencil,
        int x,
        int y,
        uint width,
        uint height)
    {
        RHICommandBufferAPI.RHICommandBuffer_BeginRenderingDepthOnly(
            NativePtr,
            depthImageView.Index,
            depthImageView.Generation,
            (int)depthImageLayout,
            (int)depthLoadOp,
            (int)depthStoreOp,
            clearDepth,
            clearStencil,
            x,
            y,
            width,
            height);
    }

    public void BindPipeline(RHIPipelineHandle pipeline)
    {
        RHICommandBufferAPI.RHICommandBuffer_BindPipeline(NativePtr, pipeline);
    }

    public void SetViewport(float x, float y, float width, float height, float minDepth = 0.0f, float maxDepth = 1.0f)
    {
        RHICommandBufferAPI.RHICommandBuffer_SetViewport(NativePtr, x, y, width, height, minDepth, maxDepth);
    }

    public void SetScissor(uint offsetX, uint offsetY, uint width, uint height)
    {
        RHICommandBufferAPI.RHICommandBuffer_SetScissor(NativePtr, offsetX, offsetY, width, height);
    }

    public void BindVertexBuffers(RHIBufferHandle buffer, ulong offset = 0)
    {
        RHICommandBufferAPI.RHICommandBuffer_BindVertexBuffers(NativePtr, buffer, offset);
    }

    public void BindIndexBuffer(RHIBufferHandle buffer, ulong offset, EIndexType indexType)
    {
        RHICommandBufferAPI.RHICommandBuffer_BindIndexBuffer(NativePtr, buffer, offset, (int)indexType);
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0,
        uint firstBinding = 0)
    {
        RHICommandBufferAPI.RHICommandBuffer_Draw(NativePtr, vertexCount, instanceCount, firstVertex, firstInstance,
            firstBinding);
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0,
        uint firstInstance = 0, uint firstBinding = 0)
    {
        RHICommandBufferAPI.RHICommandBuffer_DrawIndexed(NativePtr, indexCount, instanceCount, firstIndex, vertexOffset,
            firstInstance, firstBinding);
    }

    public void TransitionImageLayout(RHIImageHandle image, EImageLayout targetLayout)
    {
        RHICommandBufferAPI.RHICommandBuffer_TransitionImageLayout(NativePtr, image, (int)targetLayout);
    }

    public void TransitionImageLayout(RHIImageHandle image, EImageLayout oldLayout, EImageLayout targetLayout)
    {
        RHICommandBufferAPI.RHICommandBuffer_TransitionImageLayoutExplicit(NativePtr, image, (int)oldLayout,
            (int)targetLayout);
    }

    /// <summary>
    /// Transition with a Vulkan-style queue-family ownership transfer. Pass
    /// <see cref="RHIQueueFamily.External"/> as <paramref name="dstQueueFamilyIndex"/> to release
    /// a shared image to an external API (e.g. D3D11 via a Win32 NT handle), or as
    /// <paramref name="srcQueueFamilyIndex"/> to acquire ownership back before writing again.
    /// </summary>
    public void TransitionImageLayout(RHIImageHandle image, EImageLayout oldLayout, EImageLayout targetLayout,
                                       uint srcQueueFamilyIndex, uint dstQueueFamilyIndex)
    {
        RHICommandBufferExtAPI.RHICommandBuffer_TransitionImageLayoutWithQueueFamily(
            NativePtr, image, (int)oldLayout, (int)targetLayout,
            srcQueueFamilyIndex, dstQueueFamilyIndex);
    }

    public void BindDescriptorSets(EPipelineBindPoint bindPoint, uint firstSet, RHIDescriptorPoolHandle poolHandle,
        uint poolId)
    {
        RHICommandBufferAPI.RHICommandBuffer_BindDescriptorSets(NativePtr, (int)bindPoint, firstSet, poolHandle,
            poolId);
    }

    public unsafe void PushConstants(uint offset, uint size, IntPtr data, EShaderStage stageFlags)
    {
        RHICommandBufferAPI.RHICommandBuffer_PushConstants(NativePtr, offset, size, data, (uint)stageFlags);
    }

    public void CopyBuffer(RHIBufferHandle src, ulong srcOffset, RHIBufferHandle dst, ulong dstOffset, ulong size)
    {
        RHICommandBufferAPI.RHICommandBuffer_CopyBuffer(NativePtr, src, srcOffset, dst, dstOffset, size);
    }

    public unsafe void PipelineBarrier(
        EPipelineStageFlagBits srcStage,
        EPipelineStageFlagBits dstStage,
        ReadOnlySpan<RHIBufferMemoryBarrier> bufferBarriers,
        uint dependency = 0)
    {
        fixed (RHIBufferMemoryBarrier* pBufferBarriers = bufferBarriers)
        {
            RHICommandBufferAPI.RHICommandBuffer_PipelineBarrier(
                NativePtr,
                (int)srcStage,
                (int)dstStage,
                dependency,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                (IntPtr)pBufferBarriers,
                checked((uint)bufferBarriers.Length));
        }
    }

    public unsafe void PipelineBarrier(
        EPipelineStageFlagBits srcStage,
        EPipelineStageFlagBits dstStage,
        ReadOnlySpan<RHIImageMemoryBarrier> imageBarriers,
        uint dependency = 0)
    {
        fixed (RHIImageMemoryBarrier* pImageBarriers = imageBarriers)
        {
            RHICommandBufferAPI.RHICommandBuffer_PipelineBarrier(
                NativePtr,
                (int)srcStage,
                (int)dstStage,
                dependency,
                IntPtr.Zero,
                0,
                (IntPtr)pImageBarriers,
                checked((uint)imageBarriers.Length),
                IntPtr.Zero,
                0);
        }
    }

    public unsafe void PipelineBarrier(
        EPipelineStageFlagBits srcStage,
        EPipelineStageFlagBits dstStage,
        ReadOnlySpan<RHIImageMemoryBarrier> imageBarriers,
        ReadOnlySpan<RHIBufferMemoryBarrier> bufferBarriers,
        uint dependency = 0)
    {
        fixed (RHIImageMemoryBarrier* pImageBarriers = imageBarriers)
        fixed (RHIBufferMemoryBarrier* pBufferBarriers = bufferBarriers)
        {
            RHICommandBufferAPI.RHICommandBuffer_PipelineBarrier(
                NativePtr,
                (int)srcStage,
                (int)dstStage,
                dependency,
                IntPtr.Zero,
                0,
                (IntPtr)pImageBarriers,
                checked((uint)imageBarriers.Length),
                (IntPtr)pBufferBarriers,
                checked((uint)bufferBarriers.Length));
        }
    }

    public void CopyBufferToImage2D(
        RHIBufferHandle src,
        RHIImageHandle dst,
        EImageLayout dstImageLayout,
        ulong bufferOffset,
        uint width,
        uint height)
    {
        CopyBufferToImage2D(
            src,
            dst,
            dstImageLayout,
            bufferOffset,
            0,
            width,
            height);
    }

    public void CopyBufferToImage2D(
        RHIBufferHandle src,
        RHIImageHandle dst,
        EImageLayout dstImageLayout,
        ulong bufferOffset,
        uint mipLevel,
        uint width,
        uint height)
    {
        RHICommandBufferExtAPI.RHICommandBuffer_CopyBufferToImage2DSubresource(
            NativePtr,
            src,
            dst,
            (int)dstImageLayout,
            bufferOffset,
            mipLevel,
            width,
            height);
    }

    public void CopyImageToBuffer2D(
        RHIImageHandle src,
        EImageLayout srcImageLayout,
        EImageAspectFlagBits srcImageAspect,
        RHIBufferHandle dst,
        ulong bufferOffset,
        uint width,
        uint height)
    {
        RHICommandBufferExtAPI.RHICommandBuffer_CopyImageToBuffer2D(
            NativePtr,
            src,
            (int)srcImageLayout,
            (uint)srcImageAspect,
            dst,
            bufferOffset,
            width,
            height);
    }

    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        RHICommandBufferAPI.RHICommandBuffer_Dispatch(NativePtr, groupCountX, groupCountY, groupCountZ);
    }

    public void BindDescriptorSet(EPipelineBindPoint bindPoint, uint firstSet, RHIDescriptorPoolHandle poolHandle, uint poolId, uint setIdx)
    {
        RHICommandBufferAPI.RHICommandBuffer_BindDescriptorSet(NativePtr, (int)bindPoint, firstSet, poolHandle, poolId, setIdx);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct RHIBufferMemoryBarrier
{
    public EAccessFlag SrcAccessMask;
    public EAccessFlag DstAccessMask;
    public uint SrcQueueFamilyIndex;
    public uint DstQueueFamilyIndex;
    public RHIBufferHandle Buffer;
    public EPipelineStageFlagBits SrcStageMask;
    public EPipelineStageFlagBits DstStageMask;
}

[StructLayout(LayoutKind.Sequential)]
public struct RHIImageSubresourceRange
{
    public EImageAspectFlagBits AspectMask;
    public uint BaseMipLevel;
    public uint LevelCount;
    public uint BaseArrayLayer;
    public uint LayerCount;

    public static RHIImageSubresourceRange Color2D()
    {
        return new RHIImageSubresourceRange
        {
            AspectMask = EImageAspectFlagBits.IMAGE_ASPECT_COLOR_BIT,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
    }

    public static RHIImageSubresourceRange Depth2D()
    {
        return new RHIImageSubresourceRange
        {
            AspectMask = EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct RHIImageMemoryBarrier
{
    public EAccessFlag SrcAccessMask;
    public EAccessFlag DstAccessMask;
    public EImageLayout OldLayout;
    public EImageLayout NewLayout;
    public uint SrcQueueFamilyIndex;
    public uint DstQueueFamilyIndex;
    public RHIImageHandle Image;
    public RHIImageSubresourceRange SubresourceRange;
    public EPipelineStageFlagBits SrcStageMask;
    public EPipelineStageFlagBits DstStageMask;
}
