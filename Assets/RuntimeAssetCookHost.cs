using System.Diagnostics;
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
        bool ownsKernelLifetime = false;
        try
        {
            RuntimeAssetCookHostOptions options = RuntimeAssetCookHostOptions.Parse(args);
            StartupTargetOptions startupTargetOptions = StartupTargetOptions.Parse(args);
            RuntimeAssetCookConsole.InfoFormat(
                "[RuntimeAssetCookHost] Runtime asset cook starting: profile '{0}', configuration '{1}', " +
                "workspace '{2}'",
                options.TargetProfile,
                options.Configuration,
                options.WorkspaceRoot);
            long mountStarted = Stopwatch.GetTimestamp();
            RuntimeAssetBuildStageHost.Mount mount = RuntimeAssetBuildStageHost.Open(
                options.WorkspaceRoot,
                options.TargetProfile,
                options.ResolvedManifestPath);
            kernel = mount.Kernel;
            projectSubsystem = mount.ProjectSubsystem;
            ownsKernelLifetime = true;
            RuntimeAssetCookConsole.InfoFormat(
                "[RuntimeAssetCookHost] Packages mounted in {0:N1} s; planning cook roots.",
                Stopwatch.GetElapsedTime(mountStarted).TotalSeconds);

            IRuntimeAssetCookerRegistry cookerRegistry =
                kernel.Services.GetService<IRuntimeAssetCookerRegistry>();
            RuntimeAssetCookResult result = RuntimeAssetCookCoordinator.Cook(
                new RuntimeAssetCookContext(
                    mount.WorkspacePath,
                    options.TargetProfile,
                    options.Configuration,
                    options.RuntimeIdentifier,
                    options.StagingRoot,
                    options.ForceRebuild),
                CreateRootRequests(
                    mount.Project,
                    startupTargetOptions.Resolve(
                        mount.Project,
                        ResolveProfileDefinition(mount.Project, options.TargetProfile))),
                cookerRegistry);

            string catalogPath = WriteIntermediateCatalog(options.StagingRoot, result.Catalog);
            RuntimeAssetCookConsole.InfoFormat(
                "[RuntimeAssetCookHost] Cooked {0} root(s) into {1} artifact(s). Intermediate catalog: {2}",
                result.Catalog.Roots.Count,
                result.Catalog.Artifacts.Count,
                catalogPath);

            if (options.OutputRoot != null)
            {
                RuntimeAssetDeploymentResult deployment = RuntimeAssetDeployment.Deploy(
                    result,
                    options.OutputRoot);
                RuntimeAssetCookConsole.InfoFormat(
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
            RuntimeAssetCookConsole.FatalFormat("[RuntimeAssetCookHost] FATAL ERROR: {0}", ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                if (ownsKernelLifetime)
                {
                    kernel.Shutdown();
                }
            }
            finally
            {
                projectSubsystem?.Shutdown();
            }
        }
    }

    private static RuntimeAssetCookRootRequest[] CreateRootRequests(
        ProjectManifest project,
        ResolvedStartupTarget startupTarget)
    {
        ProjectAssetReference renderPipeline = project.RenderPipeline is { IsValid: true } pipeline
            ? pipeline
            : throw new InvalidDataException(
                "Workspace asset cooking requires RenderPipeline.Guid and RenderPipeline.PackageId.");

        // A malformed declaration is a workspace defect whether or not an argument replaces it.
        if (project.StartupWorld is { IsValid: false })
        {
            throw new InvalidDataException(
                "Workspace StartupWorld requires both a valid Guid and PackageId.");
        }

        var roots = new List<RuntimeAssetCookRootRequest>
        {
            new RuntimeAssetCookRootRequest(
                "renderPipeline",
                renderPipeline.Guid,
                renderPipeline.PackageId,
                "RenderPipelineSettings")
        };

        if (startupTarget.HasSample)
        {
            // The sample's content arrives through the catalogue cooker's dependencies, so a
            // cooked-only profile can boot any declared sample.
            ProjectAssetReference catalogue = project.SampleCatalogue is { IsValid: true } declared
                ? declared
                : throw new InvalidDataException(
                    $"Sample '{startupTarget.Sample}' was selected, so the workspace must declare " +
                    "SampleCatalogue.Guid and SampleCatalogue.PackageId.");
            roots.Add(new RuntimeAssetCookRootRequest(
                "sampleCatalogue",
                catalogue.Guid,
                catalogue.PackageId,
                "SampleCatalogue"));
            return roots.ToArray();
        }

        ProjectAssetReference startupScene = startupTarget.Scene ?? throw new InvalidDataException(
            "Workspace asset cooking requires a startup scene; declare StartupScene, select a " +
            "profile with one, or pass --startup-scene.");
        roots.Add(new RuntimeAssetCookRootRequest(
            "startupScene",
            startupScene.Guid,
            startupScene.PackageId,
            "Scene"));

        if (startupTarget.World is { } startupWorld)
        {
            roots.Add(new RuntimeAssetCookRootRequest(
                "startupWorld",
                startupWorld.Guid,
                startupWorld.PackageId,
                "World"));
        }

        return roots.ToArray();
    }

    private static ProfileDefinition? ResolveProfileDefinition(ProjectManifest project, string profile)
    {
        if (project.Profiles == null ||
            string.IsNullOrWhiteSpace(profile) ||
            !project.Profiles.TryGetValue(profile, out ProfileDefinition? definition))
        {
            return null;
        }

        return definition;
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
            string targetProfile = string.Empty;
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

            if (string.IsNullOrWhiteSpace(targetProfile))
            {
                throw new ArgumentException($"{Command} requires --profile <name>.");
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
