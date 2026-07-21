using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenKernel.Packages;

namespace ArisenEngine.Core.Assets;

/// <summary>
/// Package-aware build-stage host. The generated entry executable dispatches here after package
/// assemblies have been built; ArisenBuildTool never loads those assemblies into its own process.
/// </summary>
public static class RuntimeAssetCookHost
{
    public const string Command = "--arisen-cook-runtime-assets";

    public static bool IsCookCommand(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument => string.Equals(argument, Command, StringComparison.Ordinal));
    }

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        EngineKernel kernel = EngineKernel.Instance;
        ProjectSubsystem? projectSubsystem = null;
        try
        {
            RuntimeAssetCookHostOptions options = RuntimeAssetCookHostOptions.Parse(args);
            kernel.Reset();

            projectSubsystem = new ProjectSubsystem();
            kernel.Services.RegisterService<ProjectSubsystem>(projectSubsystem);
            projectSubsystem.LoadFromWorkspace(options.WorkspaceRoot);
            ProjectManifest project = projectSubsystem.ActiveProject
                ?? throw new InvalidDataException(
                    $"Workspace '{options.WorkspaceRoot}' could not be loaded for asset cooking.");

            EnginePackageGraphResolution packageGraph = EngineBootstrapper.ResolvePackageGraph(
                options.WorkspaceRoot,
                options.TargetProfile,
                resolvedManifestPathOverride: options.ResolvedManifestPath);
            kernel.MountPackageGraph(new EngineConfig
            {
                ProjectRoot = packageGraph.WorkspacePath,
                ProjectName = Path.GetFileName(packageGraph.WorkspacePath),
                PackageUrls = packageGraph.PackageUrls.ToList(),
                Platform = RuntimePlatform.Windows,
                ExecutionMode = EngineExecutionMode.RuntimeAssetCook
            });

            IRuntimeAssetCookerRegistry cookerRegistry =
                kernel.Services.GetService<IRuntimeAssetCookerRegistry>();
            RuntimeAssetCookResult result = RuntimeAssetCookCoordinator.Cook(
                new RuntimeAssetCookContext(
                    packageGraph.WorkspacePath,
                    options.TargetProfile,
                    options.Configuration,
                    options.RuntimeIdentifier,
                    options.StagingRoot,
                    options.ForceRebuild),
                CreateRootRequests(project),
                cookerRegistry);

            string catalogPath = WriteIntermediateCatalog(options.StagingRoot, result.Catalog);
            KernelLog.InfoFormat(
                "[RuntimeAssetCookHost] Cooked {0} root(s) into {1} artifact(s). Intermediate catalog: {2}",
                result.Catalog.Roots.Count,
                result.Catalog.Artifacts.Count,
                catalogPath);

            if (options.OutputRoot != null)
            {
                RuntimeAssetDeploymentResult deployment = RuntimeAssetDeployment.Deploy(
                    result,
                    options.OutputRoot);
                KernelLog.InfoFormat(
                    "[RuntimeAssetCookHost] Deployed {0} artifact(s) to {1}: {2} reused, " +
                    "{3} copied. Runtime catalog: {4}",
                    deployment.ArtifactCount,
                    deployment.ContentRoot,
                    deployment.ReusedArtifactCount,
                    deployment.CopiedArtifactCount,
                    deployment.CatalogPath);
            }

            foreach (RuntimeAssetCookedFile file in result.Files)
            {
                KernelLog.InfoFormat(
                    "[RuntimeAssetCookHost]   {0} <- {1}",
                    file.Artifact.OutputRelativePath,
                    file.SourcePath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            KernelLog.FatalFormat("[RuntimeAssetCookHost] FATAL ERROR: {0}", ex.Message);
            return 1;
        }
        finally
        {
            if (kernel.IsPackageGraphMounted)
            {
                kernel.Shutdown();
            }

            projectSubsystem?.Shutdown();
        }
    }

    private static RuntimeAssetCookRootRequest[] CreateRootRequests(ProjectManifest project)
    {
        ProjectAssetReference startupScene = project.StartupScene is { IsValid: true } scene
            ? scene
            : throw new InvalidDataException(
                "Workspace asset cooking requires StartupScene.Guid and StartupScene.PackageId.");
        ProjectAssetReference renderPipeline = project.RenderPipeline is { IsValid: true } pipeline
            ? pipeline
            : throw new InvalidDataException(
                "Workspace asset cooking requires RenderPipeline.Guid and RenderPipeline.PackageId.");

        var roots = new List<RuntimeAssetCookRootRequest>
        {
            new RuntimeAssetCookRootRequest(
                "startupScene",
                startupScene.Guid,
                startupScene.PackageId,
                "Scene"),
            new RuntimeAssetCookRootRequest(
                "renderPipeline",
                renderPipeline.Guid,
                renderPipeline.PackageId,
                "RenderPipelineSettings")
        };

        if (project.StartupWorld != null)
        {
            if (!project.StartupWorld.IsValid)
            {
                throw new InvalidDataException(
                    "Workspace StartupWorld requires both a valid Guid and PackageId.");
            }

            roots.Add(new RuntimeAssetCookRootRequest(
                "startupWorld",
                project.StartupWorld.Guid,
                project.StartupWorld.PackageId,
                "World"));
        }

        return roots.ToArray();
    }

    private static string WriteIntermediateCatalog(
        string stagingRoot,
        RuntimeAssetCatalog catalog)
    {
        string fullStagingRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(fullStagingRoot);
        string catalogPath = Path.Combine(fullStagingRoot, RuntimeAssetCatalog.DefaultFileName);
        string temporaryPath = catalogPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, catalog.Serialize());
            File.Move(temporaryPath, catalogPath, overwrite: true);
            return catalogPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record RuntimeAssetCookHostOptions(
        string WorkspaceRoot,
        string TargetProfile,
        string Configuration,
        string RuntimeIdentifier,
        string StagingRoot,
        string? OutputRoot,
        string? ResolvedManifestPath,
        bool ForceRebuild)
    {
        public static RuntimeAssetCookHostOptions Parse(IReadOnlyList<string> args)
        {
            string workspaceRoot = string.Empty;
            string targetProfile = "Production";
            string configuration = "Release";
            string runtimeIdentifier = "win-x64";
            string stagingRoot = string.Empty;
            string? outputRoot = null;
            string? resolvedManifestPath = null;
            bool forceRebuild = false;
            for (int index = 0; index < args.Count; index++)
            {
                string argument = args[index];
                switch (argument)
                {
                    case "--workspace":
                        workspaceRoot = ReadValue(args, ref index, argument);
                        break;
                    case "--profile":
                        targetProfile = ReadValue(args, ref index, argument);
                        break;
                    case "--configuration":
                        configuration = ReadValue(args, ref index, argument);
                        break;
                    case "--runtime-identifier":
                        runtimeIdentifier = ReadValue(args, ref index, argument);
                        break;
                    case "--staging-root":
                        stagingRoot = ReadValue(args, ref index, argument);
                        break;
                    case "--output-root":
                        outputRoot = ReadValue(args, ref index, argument);
                        break;
                    case "--resolved-manifest":
                        resolvedManifestPath = ReadValue(args, ref index, argument);
                        break;
                    case "--force-rebuild":
                        forceRebuild = true;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                throw new ArgumentException(
                    $"{Command} requires --workspace <absolute-or-relative-path>.");
            }

            workspaceRoot = Path.GetFullPath(workspaceRoot);
            if (resolvedManifestPath == null)
            {
                string generatedSourceManifest = Path.Combine(
                    workspaceRoot,
                    ".arisen",
                    "Projects",
                    targetProfile,
                    "manifest.source.resolved.json");
                if (File.Exists(generatedSourceManifest))
                {
                    resolvedManifestPath = generatedSourceManifest;
                }
            }

            if (string.IsNullOrWhiteSpace(stagingRoot))
            {
                stagingRoot = Path.Combine(
                    workspaceRoot,
                    ".arisen",
                    "Intermediate",
                    "Cook",
                    targetProfile,
                    configuration);
            }

            return new RuntimeAssetCookHostOptions(
                workspaceRoot,
                targetProfile,
                configuration,
                runtimeIdentifier,
                Path.GetFullPath(stagingRoot),
                outputRoot == null ? null : Path.GetFullPath(outputRoot),
                resolvedManifestPath == null ? null : Path.GetFullPath(resolvedManifestPath),
                forceRebuild);
        }

        private static string ReadValue(
            IReadOnlyList<string> args,
            ref int index,
            string argument)
        {
            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"{argument} requires a non-empty value.");
            }

            index++;
            return args[index];
        }
    }
}
