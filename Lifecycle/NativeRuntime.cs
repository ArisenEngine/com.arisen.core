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
            // Initialize Diagnostics first
#if ARISEN_ENGINE_EDITOR
            
            Diagnostics.Logger.Initialize(true);
#else
           
            Diagnostics.Logger.Initialize(false);
#endif
            
            // Register the primary engine logger
            registry.RegisterService<ILogger>(new EngineLogger());

            // Initialize Graphics RHI (Default to Vulkan for now)
            if (RHISystem.Initialize(GraphicsAPI.Vulkan, validationLayer: true))
            {
                // Defer physical device picking and surface creation to actual surface registration
                m_IsInitialized = true;
                return true;
            }

            return false;
        }
        catch (Exception e)
        {
            // Fallback to console if logger is not ready, but usually EngineInit handles logger
            KernelLog.ErrorFormat("[NativeRuntime] Failed to initialize native engine: {0}", e.Message);
        }

        return false;
    }

    public static void Shutdown()
    {
        if (!m_IsInitialized) return;

        try
        {
            RHISystem.Shutdown();
            // Arisen.Native.Core.EngineInit.Shutdown();
            m_IsInitialized = false;
        }
        catch (Exception e)
        {
            Logger.Fatal($"[NativeRuntime] Error during native engine shutdown: {e.Message}");
        }
    }
}
