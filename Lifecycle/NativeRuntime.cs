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

        bool loggerInitializedHere = false;
        try
        {
#if ARISEN_ENGINE_EDITOR
            loggerInitializedHere = !Diagnostics.Logger.IsInitialized;
            bool loggerInitialized = Diagnostics.Logger.Initialize(true);
#else
            loggerInitializedHere = !Diagnostics.Logger.IsInitialized;
            bool loggerInitialized = Diagnostics.Logger.Initialize(false);
#endif
            if (!loggerInitialized)
            {
                throw new InvalidOperationException("The native diagnostics logger rejected initialization.");
            }

            registry.RegisterService<ILogger>(new EngineLogger());
            m_DiagnosticsInitialized = true;
            return true;
        }
        catch (Exception e)
        {
            if (loggerInitializedHere)
            {
                try
                {
                    Diagnostics.Logger.Dispose();
                }
                catch (Exception shutdownError)
                {
                    e = new AggregateException(
                        "Diagnostics initialization failed and logger rollback also failed.",
                        e,
                        shutdownError);
                }
            }

            KernelLog.InvalidateCache();
            KernelLog.ErrorFormat("[NativeRuntime] Diagnostics init failed: {0}", e.Message);
            return false;
        }
    }

    public static bool Initialize(IServiceRegistry registry)
        => InitializeDiagnostics(registry);

    public static void Shutdown()
    {
        if (!m_DiagnosticsInitialized) return;

        try
        {
            Diagnostics.Logger.Dispose();
        }
        finally
        {
            m_DiagnosticsInitialized = false;
            KernelLog.InvalidateCache();
        }
    }
}
