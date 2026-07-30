using System.Collections.Concurrent;
using Arisen.Native.RHI;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Core.RHI;

public static class RHISystem
{
    private static RHIInstance? m_Instance;
    private static readonly ConcurrentDictionary<uint, RHIDevice> m_DeviceWrappers = new();
    private static readonly ConcurrentDictionary<uint, RHISurface> m_SurfaceWrappers = new();
    private static RHIDevice? m_MasterDevice;
    private static readonly object m_SyncRoot = new();
    private static string m_LastInitializationError = string.Empty;
    
    /// <summary>High-bit flag identifying a virtual/headless surface that does not own a native window.</summary>
    public const uint VirtualSurfaceIDMask = 0x80000000;
    
    /// <summary>Default virtual surface ID used for engine-level headless RHI bootstrapping.</summary>
    public const uint DefaultVirtualSurfaceID = VirtualSurfaceIDMask | 0x0;
    private static bool m_PhysicalDevicePicked = false;

    public static RHIInstance? Instance
    {
        get
        {
            lock (m_SyncRoot)
            {
                return m_Instance;
            }
        }
    }

    public static string LastInitializationError => m_LastInitializationError;

    public static RHISurface GetSurface(uint surfaceId)
    {
        if (m_SurfaceWrappers.TryGetValue(surfaceId, out var cachedSurface))
            return cachedSurface;

        lock (m_SyncRoot)
        {
            if (!m_Instance.HasValue || !m_Instance.Value.IsValid)
                throw new InvalidOperationException("RHISystem must be initialized before resolving surfaces.");

            if (m_SurfaceWrappers.TryGetValue(surfaceId, out cachedSurface))
                return cachedSurface;

            var surface = m_Instance.Value.GetSurface(surfaceId);
            m_SurfaceWrappers.TryAdd(surfaceId, surface);
            return surface;
        }
    }

    public static RHIDevice GetOrCreateDevice(uint windowId, uint width = 0, uint height = 0)
    {
        if (m_Instance == null)
            throw new InvalidOperationException("RHISystem must be initialized before creating devices.");

        // Fast path for existing devices
        if (m_DeviceWrappers.TryGetValue(windowId, out var cachedDevice))
            return cachedDevice;

        lock (m_SyncRoot)
        {
            // Always ensure the surface exists for the specific windowId
            m_Instance.Value.CreateSurface(windowId, width, height);

            // Re-check after acquiring lock
            if (m_MasterDevice.HasValue && m_MasterDevice.Value.IsValid)
            {
                m_DeviceWrappers.TryAdd(windowId, m_MasterDevice.Value);
                return m_MasterDevice.Value;
            }

            if (!m_PhysicalDevicePicked)
            {
                m_Instance.Value.PickPhysicalDevice(true);
                m_PhysicalDevicePicked = true;
            }

            Console.WriteLine($"[RHI] Creating Unified Logical Device for initial Window: 0x{windowId:X}");
            
            // Create the first logical device.
            var device = m_Instance.Value.CreateDevice(windowId);
            
            if (device.IsValid)
            {
                m_MasterDevice = device;
                m_DeviceWrappers.TryAdd(windowId, device);
            }

            return device;
        }
    }

    public static void RemoveDevice(uint windowId)
    {
        lock (m_SyncRoot)
        {
            m_DeviceWrappers.TryRemove(windowId, out _);
            m_SurfaceWrappers.TryRemove(windowId, out _);

            // The bootstrap surface owns the shared logical device and remains alive until
            // RHISystem shutdown. Viewport surfaces own only their surface/swapchain state.
            if (windowId == DefaultVirtualSurfaceID ||
                !m_Instance.HasValue ||
                !m_Instance.Value.IsValid)
            {
                return;
            }

            if (m_MasterDevice.HasValue && m_MasterDevice.Value.IsValid)
            {
                try
                {
                    m_MasterDevice.Value.WaitIdle();
                }
                catch (Exception e)
                {
                    KernelLog.WarningFormat(
                        "[RHISystem] DeviceWaitIdle failed before removing surface 0x{0:X}: {1}",
                        windowId,
                        e.Message);
                }
            }

            m_Instance.Value.DestroySurface(windowId);
        }
    }

    public static bool Initialize(GraphicsAPI api, string appName = "ArisenApp", bool validationLayer = false)
    {
        lock (m_SyncRoot)
        {
            m_LastInitializationError = string.Empty;

            if (m_Instance.HasValue && m_Instance.Value.IsValid)
            {
                return true;
            }

            try
            {
                // 1. Set the graphics API
                RHILoaderAPI.RHILoader_SetCurrentGraphicsAPI((int)api);

                // 2. Create Instance
                var instHandle = RHILoaderAPI.RHILoader_CreateInstance(
                    appName, "ArisenEngine", validationLayer ? 1 : 0,
                    0, 1, 3, 0, // Variant, Major, Minor, Patch (Vulkan 1.3)
                    1, 0, 0, // App version
                    1, 0, 0, // Engine version
                    2 // Max frames in flight
                );

                if (instHandle == IntPtr.Zero)
                {
                    m_LastInitializationError = RHILoader.GetLastErrorMessage();
                    if (string.IsNullOrWhiteSpace(m_LastInitializationError))
                    {
                        m_LastInitializationError = "Native RHI instance creation returned null without a diagnostic message.";
                    }

                    return false;
                }

                m_Instance = new RHIInstance(instHandle);

                // 3. Defer Physical Device picking until Surface is created (handled by user/test framework).
                return true;
            }
            catch (Exception e)
            {
                m_LastInitializationError = e.Message;
                return false;
            }
        }
    }

    public static void Shutdown()
    {
        lock (m_SyncRoot)
        {
            if (m_MasterDevice.HasValue && m_MasterDevice.Value.IsValid)
            {
                try
                {
                    m_MasterDevice.Value.WaitIdle();
                }
                catch (Exception e)
                {
                    KernelLog.WarningFormat("[RHISystem] DeviceWaitIdle failed during shutdown: {0}", e.Message);
                }
            }

            m_DeviceWrappers.Clear();
            m_SurfaceWrappers.Clear();
            m_MasterDevice = null;
            m_Instance = null;
            m_PhysicalDevicePicked = false;

            try
            {
                RHILoaderAPI.RHILoader_Dispose();
            }
            catch (Exception e)
            {
                KernelLog.WarningFormat("[RHISystem] RHILoader dispose failed during shutdown: {0}", e.Message);
            }

            m_LastInitializationError = string.Empty;
        }
    }
}
