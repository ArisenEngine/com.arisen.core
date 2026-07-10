using System.Collections.Concurrent;
using Arisen.Native.RHI;

namespace ArisenEngine.Core.RHI;

public static class RHISystem
{
    private static RHIInstance? m_Instance;
    private static readonly ConcurrentDictionary<uint, RHIDevice> m_DeviceWrappers = new();
    private static RHIDevice? m_MasterDevice;
    private static readonly object m_SyncRoot = new();
    private static string m_LastInitializationError = string.Empty;
    
    /// <summary>High-bit flag identifying a virtual/headless surface that does not own a native window.</summary>
    public const uint VirtualSurfaceIDMask = 0x80000000;
    
    /// <summary>Default virtual surface ID used for engine-level headless RHI bootstrapping.</summary>
    public const uint DefaultVirtualSurfaceID = VirtualSurfaceIDMask | 0x0;
    private static bool m_PhysicalDevicePicked = false;

    public static RHIInstance? Instance => m_Instance;
    public static string LastInitializationError => m_LastInitializationError;
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
            if (m_DeviceWrappers.TryRemove(windowId, out var device))
            {
                // Note: The native device is destroyed in RHI 
                // when the C# RHIDevice object is disposed or collected.
            }
        }
    }

    public static bool Initialize(GraphicsAPI api, string appName = "ArisenApp", bool validationLayer = false)
    {
        m_LastInitializationError = string.Empty;

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

    public static void Shutdown()
    {
        m_DeviceWrappers.Clear();

        if (m_Instance != null)
        {
            m_Instance = null;
        }

        RHILoaderAPI.RHILoader_Dispose();
        m_LastInitializationError = string.Empty;
    }
}
