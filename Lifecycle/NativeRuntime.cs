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
    private static bool m_DiagnosticsInitialized;
    private static bool m_GraphicsInitialized;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private static void PreloadRenderDoc()
    {
        var handle = GetModuleHandle("renderdoc.dll");
        if (handle != IntPtr.Zero)
        {
            KernelLog.Info("[NativeRuntime] RenderDoc already loaded (injected). Skipping preload.");
            return;
        }

        Environment.SetEnvironmentVariable("ENABLE_VULKAN_RENDERDOC_CAPTURE", "1");

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

    /// <summary>
    /// Phase 1: diagnostics and logger. Safe from CorePackage.OnLoad; does not touch graphics APIs.
    /// </summary>
    public static bool InitializeDiagnostics(IServiceRegistry registry)
    {
        if (m_DiagnosticsInitialized) return true;

        try
        {
#if ARISEN_ENGINE_EDITOR
            Diagnostics.Logger.Initialize(true);
#else
            Diagnostics.Logger.Initialize(false);
#endif
            registry.RegisterService<ILogger>(new EngineLogger());
            m_DiagnosticsInitialized = true;
            return true;
        }
        catch (Exception e)
        {
            KernelLog.ErrorFormat("[NativeRuntime] Diagnostics init failed: {0}", e.Message);
            return false;
        }
    }

    /// <summary>
    /// Phase 2: RenderDoc preload + Vulkan RHI init + IRHIDevice registration.
    /// Must run AFTER Avalonia's WinUI compositor has created its ANGLE D3D11 device; otherwise
    /// RenderDoc's D3D11CreateDevice hooks corrupt the compositor device and crash
    /// __MicroComICompositorInteropProxy.CreateGraphicsDevice with 0xC0000005.
    /// </summary>
    public static bool InitializeGraphics(IServiceRegistry registry)
    {
        if (m_GraphicsInitialized) return true;

        try
        {
            PreloadRenderDoc();

            if (!RHISystem.Initialize(GraphicsAPI.Vulkan, validationLayer: true))
                return false;

            var rhiDevice = RHISystem.GetOrCreateDevice(RHISystem.DefaultVirtualSurfaceID);
            rhiDevice.SetResolution(1920, 1080);

            registry.RegisterService<ArisenKernel.Contracts.IRHIDevice>(
                new ArisenEngine.Core.Native.VulkanRHIDevice(rhiDevice.Handle));

            m_GraphicsInitialized = true;
            return true;
        }
        catch (Exception e)
        {
            KernelLog.ErrorFormat("[NativeRuntime] Graphics init failed: {0}", e.Message);
            return false;
        }
    }

    public static bool Initialize(IServiceRegistry registry)
        => InitializeDiagnostics(registry) && InitializeGraphics(registry);

    public static void Shutdown()
    {
        m_DiagnosticsInitialized = false;
        m_GraphicsInitialized = false;
    }
}
