using System;
using System.Runtime.InteropServices;
using Arisen.Native.RHI;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenKernel.Services;

namespace ArisenEngine.Core.Lifecycle;

public static class NativeRuntime
{
    private static bool m_IsInitialized = false;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    /// <summary>
    /// Preloads renderdoc.dll BEFORE any graphics API initialization.
    /// RenderDoc must be loaded before vkCreateInstance so it can hook Vulkan API calls.
    /// Without this, StartFrameCapture/EndFrameCapture will fail with device mismatch.
    /// </summary>
    private static void PreloadRenderDoc()
    {
        // Check if already injected (e.g. launched from RenderDoc UI)
        var handle = GetModuleHandle("renderdoc.dll");
        if (handle != IntPtr.Zero)
        {
            KernelLog.Info("[NativeRuntime] RenderDoc already loaded (injected). Skipping preload.");
            return;
        }

        // Enable the RenderDoc Vulkan implicit layer before vkCreateInstance
        Environment.SetEnvironmentVariable("ENABLE_VULKAN_RENDERDOC_CAPTURE", "1");

        // Try to load from common installation paths
        string[] paths = {
            "C:\\Program Files\\RenderDoc\\renderdoc.dll",
            "C:\\renderdoc\\renderdoc.dll"
        };

        foreach (var p in paths)
        {
            if (System.IO.File.Exists(p))
            {
                handle = LoadLibrary(p);
                if (handle != IntPtr.Zero)
                {
                    KernelLog.Info($"[NativeRuntime] RenderDoc preloaded from: {p}");
                    return;
                }
            }
        }

        KernelLog.Info("[NativeRuntime] RenderDoc not found. Frame capture will be unavailable.");
    }

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

            // 0. Preload RenderDoc BEFORE any graphics API calls.
            // This is critical: RenderDoc hooks vkCreateInstance, so it must be loaded first.
            PreloadRenderDoc();

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
