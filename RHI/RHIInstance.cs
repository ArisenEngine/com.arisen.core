using Arisen.Native.RHI;

namespace ArisenEngine.Core.RHI;

public readonly struct RHIInstance
{
    public IntPtr Handle { get; }

    public bool IsValid => Handle != IntPtr.Zero;

    public bool IsPhysicalDeviceAvailable =>
        IsValid && RHIInstanceAPI.RHIInstance_IsPhysicalDeviceAvailable(Handle) != 0;

    public bool AreSurfacesAvailable =>
        IsValid && RHIInstanceAPI.RHIInstance_IsSurfacesAvailable(Handle) != 0;

    public bool IsValidationEnabled =>
        IsValid && RHIInstanceAPI.RHIInstance_IsEnableValidation(Handle) != 0;

    public uint MaxFramesInFlight =>
        IsValid ? RHIInstanceAPI.RHIInstance_GetMaxFramesInFlight(Handle) : 0;

    public RHIInstance(IntPtr handle)
    {
        Handle = handle;
    }

    public void PickPhysicalDevice(bool considerSurface)
    {
        RHIInstanceAPI.RHIInstance_PickPhysicalDevice(Handle, considerSurface ? 1 : 0);
    }

    internal void InitLogicDevices()
    {
        RHIInstanceAPI.RHIInstance_InitLogicDevices(Handle);
    }

    public void CreateSurface(uint windowId, uint width = 0, uint height = 0)
    {
        RHIInstanceAPI.RHIInstance_CreateSurface(Handle, windowId, width, height);
    }

    public bool IsLinearColorSpaceSupported(uint surfaceId)
    {
        return IsValid && RHIInstanceAPI.RHIInstance_IsSupportLinearColorSpace(Handle, surfaceId) != 0;
    }

    public bool IsPresentModeSupported(uint surfaceId, EPresentMode presentMode)
    {
        return IsValid && RHIInstanceAPI.RHIInstance_PresentModeSupported(
            Handle,
            surfaceId,
            unchecked((int)(uint)presentMode)) != 0;
    }

    public EFormat GetSuitableSwapChainFormat(uint surfaceId)
    {
        if (!IsValid) return EFormat.FORMAT_UNDEFINED;
        return (EFormat)RHIInstanceAPI.RHIInstance_GetSuitableSwapChainFormat(Handle, surfaceId);
    }

    public EPresentMode GetSuitablePresentMode(uint surfaceId)
    {
        if (!IsValid) return EPresentMode.PRESENT_MODE_MAX_ENUM;
        return (EPresentMode)RHIInstanceAPI.RHIInstance_GetSuitablePresentMode(Handle, surfaceId);
    }

    /// <summary>
    /// Creates a logic device for the picked physical device.
    /// In the future, this might allow selecting a specific physical device.
    /// </summary>
    internal RHIDevice CreateDevice(uint windowId = 0)
    {
        RHIInstanceAPI.RHIInstance_CreateLogicDevice(Handle, windowId);
        var deviceHandle = RHIInstanceAPI.RHIInstance_GetLogicalDevice(Handle, windowId);
        return new RHIDevice(deviceHandle);
    }

    [Obsolete("Use CreateDevice instead")]
    public RHIDevice GetLogicalDevice(uint windowId)
    {
        var deviceHandle = RHIInstanceAPI.RHIInstance_GetLogicalDevice(Handle, windowId);
        return new RHIDevice(deviceHandle);
    }
}
