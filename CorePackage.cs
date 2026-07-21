using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Lifecycle;
using ArisenEngine.Core.Automation;
using ArisenEngine.Core.Assets;

namespace ArisenEngine.Core;

public class CorePackage : IPackageEntry
{
    private IRuntimeAssetCookerRegistry? m_RuntimeAssetCookerRegistry;

    public void OnLoad(IServiceRegistry registry)
    {
        NativeRuntime.InitializeDiagnostics(registry);
        
        // Register core singleton services
        registry.RegisterService<ICommandManager>(new CommandManager());
        m_RuntimeAssetCookerRegistry = new RuntimeAssetCookerRegistry();
        registry.RegisterService<IRuntimeAssetCookerRegistry>(m_RuntimeAssetCookerRegistry);

        KernelLog.Info("[CorePackage] Loaded: Arisen Core Engine Foundation");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_RuntimeAssetCookerRegistry = null;
    }
}
