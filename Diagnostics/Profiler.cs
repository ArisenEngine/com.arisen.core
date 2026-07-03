using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using Arisen.Native.Diagnostics;

namespace ArisenEngine.Core.Diagnostics;

/// <summary>
/// High-level wrapper for the Arisen Engine Profiler.
/// </summary>
public static class Profiler
{
    /// <summary>
    /// Starts a profiling zone. Should be used with the 'using' statement.
    /// </summary>
    /// <param name="name">Name of the zone.</param>
    /// <returns>A disposable zone context.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ProfilerZone Zone(string name)
    {
#if ARISEN_PROFILER_ENABLED
        return new ProfilerZone(ProfilerAPI.Profiler_BeginZone(name));
#else
        return default;
#endif
    }

    /// <summary>
    /// Marks a frame.
    /// </summary>
    [Conditional("ARISEN_PROFILER_ENABLED")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FrameMark()
    {
        ProfilerAPI.Profiler_FrameMark();
    }

    /// <summary>
    /// Marks a named frame.
    /// </summary>
    /// <param name="name">Name of the frame.</param>
    [Conditional("ARISEN_PROFILER_ENABLED")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FrameMarkNamed(string name)
    {
#if ARISEN_PROFILER_ENABLED
        if (string.IsNullOrEmpty(name))
        {
            ProfilerAPI.Profiler_FrameMark();
            return;
        }

        NativeProfilerLabelAPI.Profiler_FrameMarkNamed(ProfilerLabelCache.Get(name));
#endif
    }

    /// <summary>
    /// Plots a numerical value.
    /// </summary>
    /// <param name="name">Name of the plot.</param>
    /// <param name="value">Value to plot.</param>
    [Conditional("ARISEN_PROFILER_ENABLED")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PlotValue(string name, double value)
    {
#if ARISEN_PROFILER_ENABLED
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        NativeProfilerLabelAPI.Profiler_PlotValue(ProfilerLabelCache.Get(name), value);
#endif
    }

    /// <summary>
    /// Sets the name of the current thread for the profiler.
    /// </summary>
    /// <param name="name">Name of the thread.</param>
    [Conditional("ARISEN_PROFILER_ENABLED")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetThreadName(string name)
    {
#if ARISEN_PROFILER_ENABLED
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        NativeProfilerLabelAPI.Profiler_SetThreadName(ProfilerLabelCache.Get(name));
#endif
    }
}

#if ARISEN_PROFILER_ENABLED
internal static class ProfilerLabelCache
{
    private static readonly ConcurrentDictionary<string, IntPtr> s_Labels = new(StringComparer.Ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntPtr Get(string? name)
    {
        return s_Labels.GetOrAdd(name!, static value => Marshal.StringToCoTaskMemUTF8(value));
    }
}

internal static class NativeProfilerLabelAPI
{
    private const string DllName = "Core.Diagnostic.dll";

    [SuppressUnmanagedCodeSecurity, DllImport(DllName, EntryPoint = "Profiler_FrameMarkNamed", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Profiler_FrameMarkNamed(IntPtr name);

    [SuppressUnmanagedCodeSecurity, DllImport(DllName, EntryPoint = "Profiler_PlotValue", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Profiler_PlotValue(IntPtr name, double value);

    [SuppressUnmanagedCodeSecurity, DllImport(DllName, EntryPoint = "Profiler_SetThreadName", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Profiler_SetThreadName(IntPtr name);
}
#endif

/// <summary>
/// A disposable handle for a profiling zone.
/// </summary>
public readonly struct ProfilerZone : IDisposable
{
#if ARISEN_PROFILER_ENABLED
    private readonly ProfilerZoneContext _ctx;

    internal ProfilerZone(ProfilerZoneContext ctx)
    {
        _ctx = ctx;
    }
#endif

    /// <summary>
    /// Ends the profiling zone.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
#if ARISEN_PROFILER_ENABLED
        if (_ctx.Handle != 0)
        {
            ProfilerAPI.Profiler_EndZone(_ctx);
        }
#endif
    }
}
