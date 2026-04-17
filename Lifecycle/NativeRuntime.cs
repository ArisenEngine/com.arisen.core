using System;
using Arisen.Native.RHI;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenKernel.Services;

namespace ArisenEngine.Core.Lifecycle;

public static class NativeRuntime
{
    private static bool m_IsInitialized = false;

    public static bool Initialize(IServiceRegistry registry)
    {
        if (m_IsInitialized) return true;

        try
        {
            // Initialize Diagnostics first (Logging, Profiler)
#if ARISEN_ENGINE_EDITOR
            Diagnostics.Logger.Initialize(true);
#else
            Diagnostics.Logger.Initialize(false);
#endif
            
            // Register the primary engine logger
            registry.RegisterService<ILogger>(new EngineLogger());

            // 1. Initialize the global RHI System (Defaulting to Vulkan with validation)
            if (RHISystem.Initialize(GraphicsAPI.Vulkan, validationLayer: true))
            {
                // 2. Resolve the primary device for the Headless/Editor interop context.
                // Use the default virtual ID for engine-level headless RHI bootstrapping.
                var rhiDevice = RHISystem.GetOrCreateDevice(RHISystem.DefaultVirtualSurfaceID);
                
                // 3. Set a high-fidelity default resolution (1080p) for the virtual surface.
                // The modern RHI will lazily allocate the swapchain on the first frame using these dimensions.
                rhiDevice.SetResolution(1920, 1080);

                // 4. Register the IRHIDevice service using the shared Vulkan device handle
                // We wrap it in a VulkanRHIDevice provider class found in the core.native package.
                registry.RegisterService<ArisenKernel.Contracts.IRHIDevice>(
                    new ArisenEngine.Core.Native.VulkanRHIDevice(rhiDevice.Handle));

                m_IsInitialized = true;
                return true;
            }

            return false;
        }
        catch (Exception e)
        {
            KernelLog.ErrorFormat("[NativeRuntime] Failed to initialize native engine foundation: {0}", e.Message);
        }

        return false;
    }

    public static void Shutdown()
    {
        if (!m_IsInitialized) return;
        m_IsInitialized = false;
    }
}
