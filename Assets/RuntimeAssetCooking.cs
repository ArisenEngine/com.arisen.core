using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace ArisenEngine.Core.Assets;

public sealed record RuntimeAssetCookContext(
    string WorkspaceRoot,
    string TargetProfile,
    string Configuration,
    string RuntimeIdentifier,
    string StagingRoot,
    bool ForceRebuild);

public readonly record struct RuntimeAssetCookRootRequest(
    string Name,
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant = "");

public readonly record struct RuntimeAssetCookRequest(
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant = "");

public readonly record struct RuntimeAssetCookDependencyRequest(
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant,
    bool Required);

public sealed class RuntimeAssetCookerOutput
{
    public RuntimeAssetCookerOutput(
        RuntimeAssetCatalogArtifact artifact,
        string sourcePath,
        IEnumerable<RuntimeAssetCookDependencyRequest>? dependencies = null)
    {
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        SourcePath = sourcePath;
        Dependencies = new ReadOnlyCollection<RuntimeAssetCookDependencyRequest>(
            dependencies?.ToArray() ?? Array.Empty<RuntimeAssetCookDependencyRequest>());
    }

    public RuntimeAssetCatalogArtifact Artifact { get; }

    /// <summary>
    /// Machine-local cooked file used as deployment input. It is never serialized into the
    /// runtime catalog.
    /// </summary>
    public string SourcePath { get; }

    public IReadOnlyList<RuntimeAssetCookDependencyRequest> Dependencies { get; }

    public static RuntimeAssetCookerOutput FromFile(
        RuntimeAssetCookRequest request,
        string resolvedVariant,
        string outputRelativePath,
        string sourcePath,
        int formatVersion,
        IEnumerable<RuntimeAssetCookDependencyRequest>? dependencies = null)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        using var stream = new FileStream(
            fullSourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        long sizeInBytes = stream.Length;
        string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new RuntimeAssetCookerOutput(
            new RuntimeAssetCatalogArtifact(
                request.Guid,
                request.PackageId,
                request.AssetType,
                resolvedVariant,
                outputRelativePath,
                sizeInBytes,
                sha256,
                formatVersion),
            fullSourcePath,
            dependencies);
    }
}

public interface IRuntimeAssetCooker
{
    string ProviderId { get; }

    IReadOnlyCollection<string> AssetTypes { get; }

    RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request);
}

public sealed record RuntimeAssetCookerRegistration(
    string AssetType,
    string ProviderId);

public interface IRuntimeAssetCookerRegistry
{
    void RegisterCooker(IRuntimeAssetCooker cooker);

    bool UnregisterCooker(IRuntimeAssetCooker cooker);

    bool TryGetCooker(string assetType, out IRuntimeAssetCooker cooker);

    IReadOnlyCollection<RuntimeAssetCookerRegistration> GetRegistrations();
}

public sealed class RuntimeAssetCookerRegistry : IRuntimeAssetCookerRegistry
{
    private readonly Dictionary<string, IRuntimeAssetCooker> m_Cookers =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterCooker(IRuntimeAssetCooker cooker)
    {
        ArgumentNullException.ThrowIfNull(cooker);
        ValidateText(cooker.ProviderId, "cooker provider id");
        IReadOnlyCollection<string>? declaredAssetTypes = cooker.AssetTypes;
        if (declaredAssetTypes == null || declaredAssetTypes.Count == 0)
        {
            throw Invalid(
                $"Cooker provider '{cooker.ProviderId}' must declare at least one asset type.");
        }

        string[] assetTypes = declaredAssetTypes.ToArray();
        var uniqueTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string assetType in assetTypes)
        {
            ValidateText(assetType, $"cooker provider '{cooker.ProviderId}' asset type");
            if (!uniqueTypes.Add(assetType))
            {
                throw Invalid(
                    $"Cooker provider '{cooker.ProviderId}' declares duplicate asset type " +
                    $"'{assetType}'.");
            }

            if (m_Cookers.TryGetValue(assetType, out IRuntimeAssetCooker? existing))
            {
                throw Invalid(
                    $"Asset type '{assetType}' already has cooker provider " +
                    $"'{existing.ProviderId}'; provider '{cooker.ProviderId}' cannot also register it.");
            }
        }

        foreach (string assetType in assetTypes)
        {
            m_Cookers.Add(assetType, cooker);
        }
    }

    public bool UnregisterCooker(IRuntimeAssetCooker cooker)
    {
        if (cooker == null) return false;

        string[] assetTypes = m_Cookers
            .Where(pair => ReferenceEquals(pair.Value, cooker))
            .Select(pair => pair.Key)
            .ToArray();
        if (assetTypes.Length == 0) return false;

        foreach (string assetType in assetTypes)
        {
            m_Cookers.Remove(assetType);
        }

        return true;
    }

    public bool TryGetCooker(string assetType, out IRuntimeAssetCooker cooker)
    {
        if (!string.IsNullOrWhiteSpace(assetType))
        {
            return m_Cookers.TryGetValue(assetType, out cooker!);
        }

        cooker = null!;
        return false;
    }

    public IReadOnlyCollection<RuntimeAssetCookerRegistration> GetRegistrations()
    {
        return m_Cookers
            .Select(pair => new RuntimeAssetCookerRegistration(pair.Key, pair.Value.ProviderId))
            .OrderBy(registration => registration.AssetType, StringComparer.Ordinal)
            .ThenBy(registration => registration.ProviderId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateText(string value, string context)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw Invalid($"The {context} must be non-empty canonical text.");
        }
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException($"[RuntimeAssetCookerRegistry] {message}");
    }
}

public sealed record RuntimeAssetCookedFile(
    RuntimeAssetCatalogArtifact Artifact,
    string SourcePath);

public sealed class RuntimeAssetCookResult
{
    internal RuntimeAssetCookResult(
        RuntimeAssetCatalog catalog,
        RuntimeAssetCookedFile[] files)
    {
        Catalog = catalog;
        Files = new ReadOnlyCollection<RuntimeAssetCookedFile>(files);
    }

    public RuntimeAssetCatalog Catalog { get; }

    public IReadOnlyList<RuntimeAssetCookedFile> Files { get; }
}

/// <summary>
/// Deterministically invokes package-owned cookers and converts their source-oriented dependency
/// requests into a closed deployable catalog. Source format parsing remains inside providers.
/// </summary>
public static class RuntimeAssetCookCoordinator
{
    private const int MaxRootCount = 1024;
    private const int MaxCookRequestCount = 1_000_000;

    public static RuntimeAssetCookResult Cook(
        RuntimeAssetCookContext context,
        IEnumerable<RuntimeAssetCookRootRequest> roots,
        IRuntimeAssetCookerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(registry);
        ValidateContext(context);

        RuntimeAssetCookRootRequest[] rootArray = roots.ToArray();
        if (rootArray.Length > MaxRootCount)
        {
            throw Invalid($"Root count {rootArray.Length} exceeds the limit {MaxRootCount}.");
        }

        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootBindings = new List<RootBinding>(rootArray.Length);
        var pending = new SortedSet<CookRequestKey>(CookRequestKeyComparer.Instance);
        var requestChains = new Dictionary<CookRequestKey, string>();
        foreach (RuntimeAssetCookRootRequest root in rootArray
                     .OrderBy(root => root.Name, StringComparer.Ordinal)
                     .ThenBy(root => root.Guid)
                     .ThenBy(root => root.PackageId, StringComparer.Ordinal)
                     .ThenBy(root => root.AssetType, StringComparer.Ordinal)
                     .ThenBy(root => root.Variant, StringComparer.Ordinal))
        {
            ValidateText(root.Name, "root name");
            if (!rootNames.Add(root.Name))
            {
                throw Invalid($"Duplicate cook root name '{root.Name}'.");
            }

            var key = CreateKey(
                root.Guid,
                root.PackageId,
                root.AssetType,
                root.Variant,
                $"root '{root.Name}'");
            string chain = $"root '{root.Name}' -> {FormatKey(key)}";
            rootBindings.Add(new RootBinding(root.Name, key));
            Enqueue(key, chain, pending, requestChains);
        }

        var nodesByRequest = new Dictionary<CookRequestKey, CookedNode>();
        var nodesByIdentity = new Dictionary<RuntimeAssetIdentity, CookedNode>();
        while (pending.Count > 0)
        {
            if (nodesByRequest.Count >= MaxCookRequestCount)
            {
                throw Invalid(
                    $"Cook request count exceeds the limit {MaxCookRequestCount}.");
            }

            CookRequestKey key = pending.Min;
            pending.Remove(key);
            string chain = requestChains[key];
            if (nodesByRequest.ContainsKey(key))
            {
                continue;
            }

            if (key.Variant.Length > 0 &&
                nodesByIdentity.TryGetValue(new RuntimeAssetIdentity(key.Guid, key.Variant), out var exactNode))
            {
                ValidateNodeMatchesRequest(exactNode, key, chain);
                nodesByRequest.Add(key, exactNode);
                continue;
            }

            if (!registry.TryGetCooker(key.AssetType, out IRuntimeAssetCooker cooker))
            {
                throw Invalid(
                    $"No package-owned cooker is registered for asset type '{key.AssetType}' at {chain}.");
            }

            RuntimeAssetCookerOutput output;
            try
            {
                output = cooker.Cook(
                    context,
                    new RuntimeAssetCookRequest(
                        key.Guid,
                        key.PackageId,
                        key.AssetType,
                        key.Variant));
            }
            catch (Exception ex)
            {
                throw Invalid(
                    $"Cooker provider '{cooker.ProviderId}' failed at {chain}: {ex.Message}",
                    ex);
            }

            if (output == null)
            {
                throw Invalid(
                    $"Cooker provider '{cooker.ProviderId}' returned no output at {chain}.");
            }

            CookedNode node = ValidateOutput(context, cooker.ProviderId, key, chain, output);
            if (nodesByIdentity.TryGetValue(node.Artifact.Identity, out CookedNode? existingNode))
            {
                if (!AreEquivalent(existingNode, node))
                {
                    throw Invalid(
                        $"Cook requests produced conflicting metadata for artifact " +
                        $"'{node.Artifact.Identity}' at {chain}.");
                }

                node = existingNode;
            }
            else
            {
                nodesByIdentity.Add(node.Artifact.Identity, node);
            }

            nodesByRequest.Add(key, node);
            foreach (RuntimeAssetCookDependencyRequest dependency in node.Dependencies)
            {
                CookRequestKey dependencyKey = CreateKey(
                    dependency.Guid,
                    dependency.PackageId,
                    dependency.AssetType,
                    dependency.Variant,
                    $"dependency of '{node.Artifact.Identity}'");
                string dependencyChain = chain + " -> " + FormatKey(dependencyKey);
                Enqueue(dependencyKey, dependencyChain, pending, requestChains);
            }
        }

        var catalogRoots = new RuntimeAssetCatalogRoot[rootBindings.Count];
        for (int index = 0; index < rootBindings.Count; index++)
        {
            RootBinding binding = rootBindings[index];
            CookedNode node = nodesByRequest[binding.Request];
            catalogRoots[index] = new RuntimeAssetCatalogRoot(
                binding.Name,
                node.Artifact.Guid,
                node.Artifact.PackageId,
                node.Artifact.AssetType,
                node.Artifact.Variant);
        }

        var finalizedArtifacts = new Dictionary<RuntimeAssetIdentity, RuntimeAssetCatalogArtifact>();
        foreach (CookedNode node in nodesByIdentity.Values)
        {
            var dependencies = new RuntimeAssetCatalogDependency[node.Dependencies.Length];
            for (int dependencyIndex = 0; dependencyIndex < node.Dependencies.Length; dependencyIndex++)
            {
                RuntimeAssetCookDependencyRequest dependency = node.Dependencies[dependencyIndex];
                CookRequestKey dependencyKey = CreateKey(
                    dependency.Guid,
                    dependency.PackageId,
                    dependency.AssetType,
                    dependency.Variant,
                    $"dependency of '{node.Artifact.Identity}'");
                CookedNode dependencyNode = nodesByRequest[dependencyKey];
                dependencies[dependencyIndex] = new RuntimeAssetCatalogDependency(
                    dependencyNode.Artifact.Guid,
                    dependencyNode.Artifact.PackageId,
                    dependencyNode.Artifact.AssetType,
                    dependencyNode.Artifact.Variant,
                    dependency.Required);
            }

            finalizedArtifacts.Add(
                node.Artifact.Identity,
                new RuntimeAssetCatalogArtifact(
                    node.Artifact.Guid,
                    node.Artifact.PackageId,
                    node.Artifact.AssetType,
                    node.Artifact.Variant,
                    node.Artifact.OutputRelativePath,
                    node.Artifact.SizeInBytes,
                    node.Artifact.Sha256,
                    node.Artifact.FormatVersion,
                    dependencies));
        }

        RuntimeAssetCatalog catalog = RuntimeAssetClosurePlanner.CreateCatalog(
            context.TargetProfile,
            catalogRoots,
            finalizedArtifacts.Values);
        RuntimeAssetCookedFile[] files = catalog.Artifacts
            .Select(artifact => new RuntimeAssetCookedFile(
                artifact,
                nodesByIdentity[artifact.Identity].SourcePath))
            .ToArray();
        return new RuntimeAssetCookResult(catalog, files);
    }

    private static CookedNode ValidateOutput(
        RuntimeAssetCookContext context,
        string providerId,
        CookRequestKey request,
        string chain,
        RuntimeAssetCookerOutput output)
    {
        RuntimeAssetCatalogArtifact artifact = output.Artifact;
        if (artifact.Dependencies.Count != 0)
        {
            throw Invalid(
                $"Cooker provider '{providerId}' returned pre-resolved catalog dependencies for " +
                $"'{artifact.Identity}' at {chain}. Use cook dependency requests instead.");
        }

        if (artifact.Guid != request.Guid ||
            !string.Equals(artifact.PackageId, request.PackageId, StringComparison.Ordinal) ||
            !string.Equals(artifact.AssetType, request.AssetType, StringComparison.Ordinal) ||
            (request.Variant.Length > 0 &&
             !string.Equals(artifact.Variant, request.Variant, StringComparison.Ordinal)))
        {
            throw Invalid(
                $"Cooker provider '{providerId}' returned artifact '{artifact.Identity}' " +
                $"('{artifact.PackageId}:{artifact.AssetType}') for incompatible request " +
                $"{FormatKey(request)} at {chain}.");
        }

        try
        {
            RuntimeAssetCatalog.Create(
                context.TargetProfile,
                Array.Empty<RuntimeAssetCatalogRoot>(),
                new[] { artifact });
        }
        catch (InvalidDataException ex)
        {
            throw Invalid(
                $"Cooker provider '{providerId}' returned invalid artifact metadata at " +
                $"{chain}: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(output.SourcePath) ||
            !Path.IsPathFullyQualified(output.SourcePath))
        {
            throw Invalid(
                $"Cooker provider '{providerId}' must return a fully qualified source path " +
                $"for '{artifact.Identity}' at {chain}.");
        }

        string sourcePath = Path.GetFullPath(output.SourcePath);
        if (!File.Exists(sourcePath))
        {
            throw Invalid(
                $"Cooker provider '{providerId}' output for '{artifact.Identity}' is missing " +
                $"at '{sourcePath}' ({chain}).");
        }

        using (var stream = new FileStream(
                   sourcePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 128 * 1024,
                   FileOptions.SequentialScan))
        {
            if (stream.Length != artifact.SizeInBytes)
            {
                throw Invalid(
                    $"Cooker provider '{providerId}' output size mismatch for " +
                    $"'{artifact.Identity}' at {chain}: metadata declares " +
                    $"{artifact.SizeInBytes}, file contains {stream.Length} bytes.");
            }

            string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"Cooker provider '{providerId}' output SHA-256 mismatch for " +
                    $"'{artifact.Identity}' at {chain}: metadata declares {artifact.Sha256}, " +
                    $"file contains {actualHash}.");
            }
        }

        RuntimeAssetCookDependencyRequest[] dependencies = output.Dependencies.ToArray();
        var dependencyKeys = new HashSet<CookRequestKey>();
        for (int index = 0; index < dependencies.Length; index++)
        {
            RuntimeAssetCookDependencyRequest dependency = dependencies[index];
            CookRequestKey dependencyKey = CreateKey(
                dependency.Guid,
                dependency.PackageId,
                dependency.AssetType,
                dependency.Variant,
                $"dependency {index} of '{artifact.Identity}'");
            if (!dependencyKeys.Add(dependencyKey))
            {
                throw Invalid(
                    $"Cooker provider '{providerId}' returned duplicate dependency " +
                    $"{FormatKey(dependencyKey)} for '{artifact.Identity}' at {chain}.");
            }
        }

        Array.Sort(dependencies, CompareDependencies);
        return new CookedNode(artifact, sourcePath, dependencies);
    }

    private static void ValidateNodeMatchesRequest(
        CookedNode node,
        CookRequestKey request,
        string chain)
    {
        if (!string.Equals(node.Artifact.PackageId, request.PackageId, StringComparison.Ordinal) ||
            !string.Equals(node.Artifact.AssetType, request.AssetType, StringComparison.Ordinal))
        {
            throw Invalid(
                $"Existing artifact '{node.Artifact.Identity}' has package/type " +
                $"'{node.Artifact.PackageId}:{node.Artifact.AssetType}', incompatible with " +
                $"request {FormatKey(request)} at {chain}.");
        }
    }

    private static bool AreEquivalent(CookedNode left, CookedNode right)
    {
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return left.Artifact.Guid == right.Artifact.Guid &&
               string.Equals(left.Artifact.PackageId, right.Artifact.PackageId, StringComparison.Ordinal) &&
               string.Equals(left.Artifact.AssetType, right.Artifact.AssetType, StringComparison.Ordinal) &&
               string.Equals(left.Artifact.Variant, right.Artifact.Variant, StringComparison.Ordinal) &&
               string.Equals(
                   left.Artifact.OutputRelativePath,
                   right.Artifact.OutputRelativePath,
                   StringComparison.Ordinal) &&
               left.Artifact.SizeInBytes == right.Artifact.SizeInBytes &&
               string.Equals(left.Artifact.Sha256, right.Artifact.Sha256, StringComparison.Ordinal) &&
               left.Artifact.FormatVersion == right.Artifact.FormatVersion &&
               string.Equals(left.SourcePath, right.SourcePath, pathComparison) &&
               left.Dependencies.SequenceEqual(right.Dependencies);
    }

    private static void Enqueue(
        CookRequestKey key,
        string chain,
        ISet<CookRequestKey> pending,
        IDictionary<CookRequestKey, string> requestChains)
    {
        if (requestChains.TryGetValue(key, out string? existingChain))
        {
            if (StringComparer.Ordinal.Compare(chain, existingChain) < 0)
            {
                requestChains[key] = chain;
            }

            return;
        }

        pending.Add(key);
        requestChains.Add(key, chain);
    }

    private static CookRequestKey CreateKey(
        Guid guid,
        string packageId,
        string assetType,
        string? variant,
        string context)
    {
        if (guid == Guid.Empty)
        {
            throw Invalid($"The {context} has an empty GUID.");
        }

        ValidateText(packageId, $"{context} package id");
        ValidateText(assetType, $"{context} asset type");
        string normalizedVariant = variant ?? string.Empty;
        if (normalizedVariant.Length > 0)
        {
            ValidateText(normalizedVariant, $"{context} variant");
        }

        return new CookRequestKey(guid, packageId, assetType, normalizedVariant);
    }

    private static void ValidateContext(RuntimeAssetCookContext context)
    {
        ValidateText(context.TargetProfile, "target profile");
        ValidateText(context.Configuration, "configuration");
        ValidateText(context.RuntimeIdentifier, "runtime identifier");
        ValidateAbsoluteDirectory(context.WorkspaceRoot, "workspace root", mustExist: true);
        ValidateAbsoluteDirectory(context.StagingRoot, "staging root", mustExist: false);
        Directory.CreateDirectory(Path.GetFullPath(context.StagingRoot));
    }

    private static void ValidateAbsoluteDirectory(
        string path,
        string context,
        bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw Invalid($"The {context} must be a fully qualified directory path.");
        }

        if (mustExist && !Directory.Exists(Path.GetFullPath(path)))
        {
            throw Invalid($"The {context} '{path}' does not exist.");
        }
    }

    private static void ValidateText(string value, string context)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw Invalid($"The {context} must be non-empty canonical text.");
        }
    }

    private static int CompareDependencies(
        RuntimeAssetCookDependencyRequest left,
        RuntimeAssetCookDependencyRequest right)
    {
        int result = left.Guid.CompareTo(right.Guid);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.AssetType, right.AssetType);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.Variant, right.Variant);
        return result != 0 ? result : left.Required.CompareTo(right.Required);
    }

    private static string FormatKey(CookRequestKey key)
    {
        string variant = key.Variant.Length == 0 ? "<default>" : key.Variant;
        return $"'{key.PackageId}:{key.AssetType}:{key.Guid:D}:{variant}'";
    }

    private static InvalidDataException Invalid(
        string message,
        Exception? innerException = null)
    {
        return new InvalidDataException($"[RuntimeAssetCook] {message}", innerException);
    }

    private readonly record struct CookRequestKey(
        Guid Guid,
        string PackageId,
        string AssetType,
        string Variant);

    private sealed class CookRequestKeyComparer : IComparer<CookRequestKey>
    {
        public static CookRequestKeyComparer Instance { get; } = new();

        public int Compare(CookRequestKey left, CookRequestKey right)
        {
            int result = left.Guid.CompareTo(right.Guid);
            if (result != 0)
            {
                return result;
            }

            result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
            if (result != 0)
            {
                return result;
            }

            result = StringComparer.Ordinal.Compare(left.AssetType, right.AssetType);
            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(left.Variant, right.Variant);
        }
    }

    private sealed record RootBinding(
        string Name,
        CookRequestKey Request);

    private sealed record CookedNode(
        RuntimeAssetCatalogArtifact Artifact,
        string SourcePath,
        RuntimeAssetCookDependencyRequest[] Dependencies);
}
