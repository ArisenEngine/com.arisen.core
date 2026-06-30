using System;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Diagnostics;
using ArisenKernel.Services;

namespace ArisenEngine.Core.Lifecycle;

public static class NativeRuntime
{
    private static bool m_DiagnosticsInitialized;

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

    public static bool Initialize(IServiceRegistry registry)
        => InitializeDiagnostics(registry);

    public static void Shutdown()
    {
        m_DiagnosticsInitialized = false;
    }
}
