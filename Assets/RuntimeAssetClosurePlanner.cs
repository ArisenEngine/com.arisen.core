namespace ArisenEngine.Core.Assets;

/// <summary>
/// Derives the deterministic artifact set reachable from named runtime roots. Package-owned
/// cookers provide the candidate metadata; this planner does not inspect source asset formats.
/// </summary>
public static class RuntimeAssetClosurePlanner
{
    private const int MaxRootCount = 1024;
    private const int MaxCandidateArtifactCount = 1_000_000;

    public static RuntimeAssetCatalog CreateCatalog(
        string targetProfile,
        IEnumerable<RuntimeAssetCatalogRoot> roots,
        IEnumerable<RuntimeAssetCatalogArtifact> candidateArtifacts)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(candidateArtifacts);

        RuntimeAssetCatalogRoot[] rootArray = roots.ToArray();
        RuntimeAssetCatalogArtifact[] candidateArray = candidateArtifacts.ToArray();
        if (rootArray.Length > MaxRootCount)
        {
            throw Invalid($"Root count {rootArray.Length} exceeds the limit {MaxRootCount}.");
        }

        if (candidateArray.Length > MaxCandidateArtifactCount)
        {
            throw Invalid(
                $"Candidate artifact count {candidateArray.Length} exceeds the limit " +
                $"{MaxCandidateArtifactCount}.");
        }

        var candidates = new Dictionary<RuntimeAssetIdentity, RuntimeAssetCatalogArtifact>(
            candidateArray.Length);
        for (int index = 0; index < candidateArray.Length; index++)
        {
            RuntimeAssetCatalogArtifact artifact = candidateArray[index]
                ?? throw Invalid($"Candidate artifact {index} is null.");
            if (!candidates.TryAdd(artifact.Identity, artifact))
            {
                throw Invalid($"Duplicate candidate artifact identity '{artifact.Identity}'.");
            }
        }

        RuntimeAssetCatalogRoot[] orderedRoots = rootArray
            .OrderBy(root => root.Name, StringComparer.Ordinal)
            .ThenBy(root => root.Guid)
            .ThenBy(root => root.Variant, StringComparer.Ordinal)
            .ToArray();
        var states = new Dictionary<RuntimeAssetIdentity, VisitState>();
        var selected = new HashSet<RuntimeAssetIdentity>();
        foreach (RuntimeAssetCatalogRoot root in orderedRoots)
        {
            if (!candidates.TryGetValue(root.Identity, out RuntimeAssetCatalogArtifact? rootArtifact))
            {
                throw Invalid(
                    $"Root '{root.Name}' references missing candidate artifact '{root.Identity}'.");
            }

            ValidateReference(
                root.Identity,
                root.PackageId,
                root.AssetType,
                rootArtifact,
                $"Root '{root.Name}'");
            TraverseRoot(root.Name, rootArtifact, candidates, states, selected);
        }

        RuntimeAssetCatalogArtifact[] closure = candidateArray
            .Where(artifact => selected.Contains(artifact.Identity))
            .ToArray();
        return RuntimeAssetCatalog.Create(targetProfile, rootArray, closure);
    }

    private static void TraverseRoot(
        string rootName,
        RuntimeAssetCatalogArtifact rootArtifact,
        IReadOnlyDictionary<RuntimeAssetIdentity, RuntimeAssetCatalogArtifact> candidates,
        IDictionary<RuntimeAssetIdentity, VisitState> states,
        ISet<RuntimeAssetIdentity> selected)
    {
        if (states.ContainsKey(rootArtifact.Identity))
        {
            return;
        }

        var path = new List<RuntimeAssetIdentity> { rootArtifact.Identity };
        var stack = new List<TraversalFrame>
        {
            new(rootArtifact, OrderDependencies(rootArtifact.Dependencies))
        };
        states[rootArtifact.Identity] = VisitState.Visiting;

        while (stack.Count > 0)
        {
            int frameIndex = stack.Count - 1;
            TraversalFrame frame = stack[frameIndex];
            if (frame.NextDependencyIndex >= frame.Dependencies.Length)
            {
                states[frame.Artifact.Identity] = VisitState.Visited;
                selected.Add(frame.Artifact.Identity);
                stack.RemoveAt(frameIndex);
                path.RemoveAt(path.Count - 1);
                continue;
            }

            RuntimeAssetCatalogDependency dependency =
                frame.Dependencies[frame.NextDependencyIndex++];
            stack[frameIndex] = frame;
            string chain = FormatChain(rootName, path, dependency.Identity);
            if (!candidates.TryGetValue(
                    dependency.Identity,
                    out RuntimeAssetCatalogArtifact? dependencyArtifact))
            {
                string intent = dependency.Required ? "required" : "optional";
                throw Invalid(
                    $"Startup closure is incomplete at {chain}: {intent} dependency " +
                    $"'{dependency.Identity}' is missing from cooked artifact metadata.");
            }

            ValidateReference(
                dependency.Identity,
                dependency.PackageId,
                dependency.AssetType,
                dependencyArtifact,
                $"Startup closure at {chain}");

            if (states.TryGetValue(dependency.Identity, out VisitState dependencyState))
            {
                if (dependencyState == VisitState.Visiting)
                {
                    // Reachability is well-defined for cycles; cooker ordering and residency
                    // policy may impose stricter rules at their own boundaries.
                }

                continue;
            }

            states[dependency.Identity] = VisitState.Visiting;
            path.Add(dependency.Identity);
            stack.Add(new TraversalFrame(
                dependencyArtifact,
                OrderDependencies(dependencyArtifact.Dependencies)));
        }
    }

    private static RuntimeAssetCatalogDependency[] OrderDependencies(
        IReadOnlyList<RuntimeAssetCatalogDependency> dependencies)
    {
        return dependencies
            .OrderBy(dependency => dependency.Guid)
            .ThenBy(dependency => dependency.Variant, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.PackageId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.AssetType, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Required)
            .ToArray();
    }

    private static void ValidateReference(
        RuntimeAssetIdentity identity,
        string packageId,
        string assetType,
        RuntimeAssetCatalogArtifact artifact,
        string context)
    {
        if (!string.Equals(packageId, artifact.PackageId, StringComparison.Ordinal) ||
            !string.Equals(assetType, artifact.AssetType, StringComparison.Ordinal))
        {
            throw Invalid(
                $"{context} declares '{packageId}:{assetType}' for '{identity}', but cooked " +
                $"metadata declares '{artifact.PackageId}:{artifact.AssetType}'.");
        }
    }

    private static string FormatChain(
        string rootName,
        IEnumerable<RuntimeAssetIdentity> path,
        RuntimeAssetIdentity terminal)
    {
        return $"root '{rootName}' -> " +
               string.Join(" -> ", path.Append(terminal).Select(identity => $"'{identity}'"));
    }

    private static InvalidDataException Invalid(string message)
    {
        return new InvalidDataException($"[RuntimeAssetClosure] {message}");
    }

    private enum VisitState : byte
    {
        Visiting,
        Visited
    }

    private struct TraversalFrame
    {
        public TraversalFrame(
            RuntimeAssetCatalogArtifact artifact,
            RuntimeAssetCatalogDependency[] dependencies)
        {
            Artifact = artifact;
            Dependencies = dependencies;
            NextDependencyIndex = 0;
        }

        public RuntimeAssetCatalogArtifact Artifact { get; }

        public RuntimeAssetCatalogDependency[] Dependencies { get; }

        public int NextDependencyIndex { get; set; }
    }
}
