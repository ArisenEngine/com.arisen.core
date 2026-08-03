using Arisen.Native.RHI;
using System.Runtime.InteropServices;

namespace ArisenEngine.Core.RHI;

public readonly struct RHIPipelineState
{
    internal IntPtr NativePtr { get; }
    public bool IsValid => NativePtr != IntPtr.Zero;

    internal RHIPipelineState(IntPtr ptr) => NativePtr = ptr;

    public void AddProgram(RHIShaderProgramHandle handle)
    {
        RHIPipelineAPI.RHIPipelineState_AddProgram(NativePtr, handle.Index, handle.Generation);
    }

    public void SetBindPoint(EPipelineBindPoint bindPoint)
    {
        RHIPipelineAPI.RHIPipelineState_SetBindPoint(NativePtr, (int)bindPoint);
    }

    public void SetInputAssemblyState(EPrimitiveTopology topology, bool primitiveRestart = false)
    {
        RHIPipelineAPI.RHIPipelineState_SetInputAssemblyState(NativePtr, (int)topology, primitiveRestart ? 1 : 0);
    }

    public void AddVertexBindingDescription(uint binding, uint stride, EVertexInputRate inputRate)
    {
        RHIPipelineAPI.RHIPipelineState_AddVertexBindingDescription(NativePtr, binding, stride, (int)inputRate);
    }

    public void AddVertexInputAttributeDescription(uint location, uint binding, EFormat format, uint offset)
    {
        RHIPipelineAPI.RHIPipelineState_AddVertexInputAttributeDescription(
            NativePtr,
            location,
            binding,
            (int)format,
            offset);
    }

    public void ClearVertexInputDescriptions()
    {
        RHIPipelineAPI.RHIPipelineState_ClearVertexInputDescriptions(NativePtr);
    }

    public void SetRasterizationState(EPolygonMode polygonMode, ECullModeFlagBits cullMode, EFrontFace frontFace)
    {
        RHIPipelineAPI.RHIPipelineState_SetRasterizationState(NativePtr, (int)polygonMode, (int)cullMode,
            (int)frontFace);
    }

    public void SetRasterizationStateWithDepthBias(
        EPolygonMode polygonMode,
        ECullModeFlagBits cullMode,
        EFrontFace frontFace,
        float depthBiasConstantFactor,
        float depthBiasClamp,
        float depthBiasSlopeFactor)
    {
        RHIPipelineAPI.RHIPipelineState_SetRasterizationStateWithDepthBias(
            NativePtr,
            (int)polygonMode,
            (int)cullMode,
            (int)frontFace,
            depthBiasConstantFactor,
            depthBiasClamp,
            depthBiasSlopeFactor);
    }

    public unsafe void SetRenderingFormats(EFormat[] colorFormats, EFormat depthFormat)
    {
        fixed (EFormat* pFormats = colorFormats)
        {
            RHIPipelineAPI.RHIPipelineState_SetRenderingFormats(NativePtr, (IntPtr)pFormats, (uint)colorFormats.Length,
                (int)depthFormat);
        }
    }

    public void SetColorBlendState(bool blendEnable = false, EBlendFactor srcColor = EBlendFactor.BLEND_FACTOR_ZERO,
        EBlendFactor dstColor = EBlendFactor.BLEND_FACTOR_ZERO, EBlendOp colorOp = EBlendOp.BLEND_OP_ADD)
    {
        RHIPipelineAPI.RHIPipelineState_SetColorBlendState(NativePtr, blendEnable ? 1 : 0, (int)srcColor, (int)dstColor,
            (int)colorOp);
    }

    public void SetDepthStencilState(bool depthTestEnable, bool depthWriteEnable, ECompareOp depthCompareOp)
    {
        RHIPipelineAPI.RHIPipelineState_SetDepthStencilState(
            NativePtr,
            depthTestEnable ? 1 : 0,
            depthWriteEnable ? 1 : 0,
            (int)depthCompareOp);
    }

    public void SetDynamicStateMask(ulong mask)
    {
        RHIPipelineAPI.RHIPipelineState_SetDynamicStateMask(NativePtr, mask);
    }

    public void Release()
    {
        RHIPipelineAPI.RHIPipelineState_Delete(NativePtr);
    }

    public unsafe void UpdateDescriptorSet(uint layoutIndex, uint binding, RHIBufferHandle[] bufferHandles)
    {
        uint[] indices = new uint[bufferHandles.Length];
        uint[] generations = new uint[bufferHandles.Length];
        for (int i = 0; i < bufferHandles.Length; i++)
        {
            indices[i] = bufferHandles[i].Index;
            generations[i] = bufferHandles[i].Generation;
        }

        fixed (uint* pIndices = indices)
        fixed (uint* pGenerations = generations)
        {
            RHIPipelineAPI.RHIPipelineState_UpdateDescriptorSetBuffer(NativePtr, layoutIndex, binding, (IntPtr)pIndices,
                (IntPtr)pGenerations, (uint)bufferHandles.Length);
        }
    }

    public void BuildDescriptorSetLayout()
    {
        RHIPipelineAPI.RHIPipelineState_BuildDescriptorSetLayout(NativePtr);
    }
}
