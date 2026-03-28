using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Core;

public class CorePackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        // Register the primary engine logger
        registry.RegisterService<ILogger>(new EngineLogger());

        KernelLog.Info("[CorePackage] Loaded: Arisen Core Engine Foundation");
    }

    public void OnUnload(IServiceRegistry registry)
    {
    }
}
