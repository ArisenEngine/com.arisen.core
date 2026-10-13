using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenKernel.Packages;

namespace ArisenEngine.Core.Assets;

/// <summary>
/// Shared mount for build-stage hosts. Both the runtime asset cook and the source reimport run the
/// real package graph with source access enabled, so the graph resolution, ownership, and kernel
/// lifetime live here rather than being repeated per command.
/// </summary>
internal static class RuntimeAssetBuildStageHost
{
    internal readonly record struct Mount(
        EngineKernel Kernel,
        ProjectSubsystem ProjectSubsystem,
        ProjectManifest Project,
        string WorkspacePath);

    internal static Mount Open(
        string workspaceRoot,
        string profile,
        string? resolvedManifestPath)
    {
        EngineKernel kernel = EngineKernel.Instance;
        kernel.Reset();

        var projectSubsystem = new ProjectSubsystem();
        kernel.RegisterKernelOwnedService<ProjectSubsystem>(projectSubsystem);
        projectSubsystem.LoadFromWorkspace(workspaceRoot);
        ProjectManifest project = projectSubsystem.ActiveProject
            ?? throw new InvalidDataException(
                $"Workspace '{workspaceRoot}' could not be loaded for build-stage asset work.");

        string sourceResolvedManifestPath = resolvedManifestPath
            ?? throw new FileNotFoundException(
                $"Source resolved manifest was not found for profile '{profile}'.");
        EnginePackageGraphResolution packageGraph =
            EngineBootstrapper.ResolveBuildStagePackageGraph(
                workspaceRoot,
                profile,
                sourceResolvedManifestPath,
                Path.Combine(AppContext.BaseDirectory, "manifest.resolved.json"));
        kernel.MountPackageGraph(new EngineConfig
        {
            ProjectRoot = packageGraph.WorkspacePath,
            ProjectName = Path.GetFileName(packageGraph.WorkspacePath),
            Profile = profile,
            PackageUrls = packageGraph.PackageUrls.ToList(),
            PackageRequirements = packageGraph.PackageRequirements.ToList(),
            Platform = RuntimePlatform.Windows,
            ExecutionMode = EngineExecutionMode.RuntimeAssetCook
        });

        return new Mount(kernel, projectSubsystem, project, packageGraph.WorkspacePath);
    }

    internal static string ResolveSourceResolvedManifest(string workspaceRoot, string profile)
    {
        return Path.Combine(
            Path.GetFullPath(workspaceRoot),
            ".arisen",
            "Projects",
            profile,
            "manifest.source.resolved.json");
    }
}

/// <summary>
/// Build-stage command that reimports every source asset an importer owns. Generated child
/// identity embeds the owning package id, so content that changed owner has to be reimported by
/// its producer for its children and the references to them to be re-keyed.
/// </summary>
public static class RuntimeAssetSourceReimportHost
{
    public const string Command = "--arisen-reimport-models";

    public static bool IsReimportCommand(IReadOnlyList<string> args)
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
            string workspaceRoot = string.Empty;
            string profile = string.Empty;
            string? resolvedManifestPath = null;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--workspace":
                        workspaceRoot = ReadValue(args, ref index, "--workspace");
                        break;
                    case "--profile":
                        profile = ReadValue(args, ref index, "--profile");
                        break;
                    case "--resolved-manifest":
                        resolvedManifestPath = ReadValue(args, ref index, "--resolved-manifest");
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                throw new ArgumentException($"{Command} requires --workspace <path>.");
            }

            if (string.IsNullOrWhiteSpace(profile))
            {
                throw new ArgumentException($"{Command} requires --profile <name>.");
            }

            workspaceRoot = Path.GetFullPath(workspaceRoot);
            resolvedManifestPath ??= RuntimeAssetBuildStageHost.ResolveSourceResolvedManifest(
                workspaceRoot,
                profile);

            RuntimeAssetBuildStageHost.Mount mount = RuntimeAssetBuildStageHost.Open(
                workspaceRoot,
                profile,
                resolvedManifestPath);
            kernel = mount.Kernel;
            projectSubsystem = mount.ProjectSubsystem;
            ownsKernelLifetime = true;

            IAssetSourceReimporterRegistry registry =
                kernel.Services.GetService<IAssetSourceReimporterRegistry>();
            IAssetDatabase database = kernel.Services.GetService<IAssetDatabase>();
            if (!database.CanReadSourceAssets)
            {
                throw new InvalidOperationException(
                    $"{Command} requires source asset access, but the mounted database reports " +
                    $"'{database.SourceAccessMode}'.");
            }

            IReadOnlyList<IAssetSourceReimporter> reimporters = registry.Reimporters;
            if (reimporters.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{Command} found no registered source reimporter; no package in profile " +
                    $"'{profile}' provides one.");
            }

            int reimportedSources = 0;
            int reimportedChildren = 0;
            for (int index = 0; index < reimporters.Count; index++)
            {
                IAssetSourceReimporter reimporter = reimporters[index];
                AssetRecord[] sources = database.Assets
                    .Where(asset => string.Equals(
                        asset.AssetType,
                        reimporter.SourceAssetType,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(asset => asset.SourcePath, StringComparer.Ordinal)
                    .ToArray();
                if (sources.Length == 0)
                {
                    KernelLog.InfoFormat(
                        "[RuntimeAssetSourceReimportHost] Reimporter '{0}' owns no '{1}' source in this workspace.",
                        reimporter.ImporterId,
                        reimporter.SourceAssetType);
                    continue;
                }

                KernelLog.InfoFormat(
                    "[RuntimeAssetSourceReimportHost] Reimporting {0} '{1}' source(s) through '{2}'.",
                    sources.Length,
                    reimporter.SourceAssetType,
                    reimporter.ImporterId);
                int children = reimporter.ReimportAll(database, sources);
                KernelLog.InfoFormat(
                    "[RuntimeAssetSourceReimportHost] Reimported {0} source(s) and re-keyed {1} generated output(s) through '{2}'.",
                    sources.Length,
                    children,
                    reimporter.ImporterId);
                reimportedSources += sources.Length;
                reimportedChildren += children;
            }

            if (reimportedSources == 0)
            {
                throw new InvalidOperationException(
                    $"{Command} matched no source asset in profile '{profile}'; the workspace " +
                    "declares the mounted reimporters but nothing of theirs is indexed.");
            }

            KernelLog.InfoFormat(
                "[RuntimeAssetSourceReimportHost] Reimported {0} source asset(s) and {1} generated output(s).",
                reimportedSources,
                reimportedChildren);
            return 0;
        }
        catch (Exception ex)
        {
            KernelLog.FatalFormat("[RuntimeAssetSourceReimportHost] FATAL ERROR: {0}", ex.Message);
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
            catch (Exception ex)
            {
                KernelLog.ErrorFormat(
                    "[RuntimeAssetSourceReimportHost] Shutdown failed: {0}",
                    ex.Message);
            }
            finally
            {
                projectSubsystem?.Shutdown();
            }
        }
    }

    private static string ReadValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{argument} requires a non-empty value.");
        }

        index++;
        return args[index];
    }
}
