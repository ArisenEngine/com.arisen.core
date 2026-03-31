using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Lifecycle;
using ArisenKernel.Lifecycle;
using ArisenEngine.Core.Automation;

namespace ArisenEngine.Core;

public class CorePackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        NativeRuntime.Initialize(registry);
        
        // Register early engine subsystems
        EngineKernel.Instance.RegisterSubsystem(new EnvironmentSubsystem());
        
        // Register core singleton services
        registry.RegisterService<ICommandManager>(new CommandManager());

        KernelLog.Info("[CorePackage] Loaded: Arisen Core Engine Foundation");
    }

    public void OnUnload(IServiceRegistry registry)
    {
    }
}
