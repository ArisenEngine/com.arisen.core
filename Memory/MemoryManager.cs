using ArisenKernel.Lifecycle;

namespace ArisenEngine.Core.Memory;

/// <summary>
/// Owns the process frame arena. The core package creates it on load and releases it on unload,
/// and the kernel frame boundary reclaims it after every subsystem has ticked, so the arena holds
/// exactly one frame of transient data. Consumers allocate frame-scoped data from
/// <see cref="FrameArena"/>; anything that must outlive its frame belongs to an asset, a pool, or
/// ECS memory instead.
/// </summary>
public static class MemoryManager
{
    /// <summary>
    /// Per-frame capacity, not a total: one frame of the current showcase valley (mesh items,
    /// draw list, camera and light snapshots) uses a few hundred kilobytes, so this budget leaves
    /// headroom for a far heavier frame while a frame that exceeds it fails loudly.
    /// </summary>
    public const uint DefaultFrameArenaSizeMB = 128;

    private static FrameArena? s_FrameArena;

    public static FrameArena FrameArena =>
        s_FrameArena ?? throw new InvalidOperationException(
            "The frame arena is unavailable because MemoryManager is not initialized. The core package initializes it on load.");

    public static bool IsInitialized => s_FrameArena != null;

    public static void Initialize(uint frameArenaSizeMB = DefaultFrameArenaSizeMB)
    {
        // A package reload replaces the arena exactly once: retire the previous generation's
        // subscription and buffer before installing the new one.
        Shutdown();
        s_FrameArena = new FrameArena(frameArenaSizeMB);
        EngineKernel.Instance.OnFrameEnd += OnFrameEnd;
    }

    public static void Shutdown()
    {
        EngineKernel.Instance.OnFrameEnd -= OnFrameEnd;
        s_FrameArena?.Dispose();
        s_FrameArena = null;
    }

    /// <summary>
    /// Reclaims every frame-scoped allocation. The kernel invokes this after all subsystems have
    /// ticked, so no consumer can still be holding a span into the arena.
    /// </summary>
    private static void OnFrameEnd()
    {
        s_FrameArena?.Reset();
    }
}
