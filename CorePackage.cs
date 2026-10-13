using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Lifecycle;
using ArisenEngine.Core.Automation;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Memory;

namespace ArisenEngine.Core;

public class CorePackage : IPackageEntry
{
    private IRuntimeAssetCookerRegistry? m_RuntimeAssetCookerRegistry;
    private IAssetSourceReimporterRegistry? m_AssetSourceReimporterRegistry;

    public void OnLoad(IServiceRegistry registry)
    {
        if (!NativeRuntime.InitializeDiagnostics(registry))
        {
            throw new InvalidOperationException("Core diagnostics initialization failed.");
        }

        // The frame arena lives for the whole package lifetime. The kernel frame boundary
        // reclaims it after every subsystem has ticked, so producers such as MeshSystem and
        // RenderSubsystem only ever see one frame of transient data in it.
        MemoryManager.Initialize();

        // Register core singleton services
        registry.RegisterService<ICommandManager>(new CommandManager());
        m_RuntimeAssetCookerRegistry = new RuntimeAssetCookerRegistry();
        registry.RegisterService<IRuntimeAssetCookerRegistry>(m_RuntimeAssetCookerRegistry);
        m_AssetSourceReimporterRegistry = new AssetSourceReimporterRegistry();
        registry.RegisterService<IAssetSourceReimporterRegistry>(m_AssetSourceReimporterRegistry);

        KernelLog.Info("[CorePackage] Loaded: Arisen Core Engine Foundation");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        MemoryManager.Shutdown();
        m_RuntimeAssetCookerRegistry = null;
        m_AssetSourceReimporterRegistry = null;
        KernelLog.Info("[CorePackage] Completing diagnostics logging.");
        NativeRuntime.Shutdown();
    }
}
