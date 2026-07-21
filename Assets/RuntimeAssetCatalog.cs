using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArisenEngine.Core.Assets;

public readonly record struct RuntimeAssetIdentity(Guid Guid, string Variant)
{
    public override string ToString()
    {
        return $"{Guid:D}:{Variant}";
    }
}

public readonly record struct RuntimeAssetCatalogRoot(
    string Name,
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant)
{
    public RuntimeAssetIdentity Identity => new(Guid, Variant);
}

public readonly record struct RuntimeAssetCatalogDependency(
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant,
    bool Required)
{
    public RuntimeAssetIdentity Identity => new(Guid, Variant);
}

public sealed class RuntimeAssetCatalogArtifact
{
    public RuntimeAssetCatalogArtifact(
        Guid guid,
        string packageId,
        string assetType,
        string variant,
        string outputRelativePath,
        long sizeInBytes,
        string sha256,
        int formatVersion,
        IEnumerable<RuntimeAssetCatalogDependency>? dependencies = null)
    {
        Guid = guid;
        PackageId = packageId;
        AssetType = assetType;
        Variant = variant;
        OutputRelativePath = outputRelativePath;
        SizeInBytes = sizeInBytes;
        Sha256 = sha256;
        FormatVersion = formatVersion;
        Dependencies = new ReadOnlyCollection<RuntimeAssetCatalogDependency>(
            dependencies?.ToArray() ?? Array.Empty<RuntimeAssetCatalogDependency>());
    }

    public Guid Guid { get; }

    public string PackageId { get; }

    public string AssetType { get; }

    public string Variant { get; }

    public string OutputRelativePath { get; }

    public long SizeInBytes { get; }

    public string Sha256 { get; }

    public int FormatVersion { get; }

    public IReadOnlyList<RuntimeAssetCatalogDependency> Dependencies { get; }

    public RuntimeAssetIdentity Identity => new(Guid, Variant);
}

/// <summary>
/// Versioned deployable lookup data for cooked runtime assets. It intentionally contains no
/// source/cache paths, timestamps, or mutable workspace state.
/// </summary>
public sealed class RuntimeAssetCatalog
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultFileName = "runtime-assets.json";

    private const int MaxCatalogBytes = 64 * 1024 * 1024;
    private const int MaxRootCount = 1024;
    private const int MaxArtifactCount = 1_000_000;
    private const int MaxDependenciesPerArtifact = 1_000_000;
    private const int MaxIdentityLength = 1024;
    private const int MaxPathLength = 4096;

    private readonly IReadOnlyDictionary<RuntimeAssetIdentity, RuntimeAssetCatalogArtifact> m_ArtifactsByIdentity;
    private readonly IReadOnlyDictionary<Guid, AssetDescriptor> m_AssetsByGuid;

    private RuntimeAssetCatalog(
        string targetProfile,
        RuntimeAssetCatalogRoot[] roots,
        RuntimeAssetCatalogArtifact[] artifacts)
    {
        TargetProfile = targetProfile;
        Roots = new ReadOnlyCollection<RuntimeAssetCatalogRoot>(roots);
        Artifacts = new ReadOnlyCollection<RuntimeAssetCatalogArtifact>(artifacts);
        m_ArtifactsByIdentity = new ReadOnlyDictionary<RuntimeAssetIdentity, RuntimeAssetCatalogArtifact>(
            artifacts.ToDictionary(artifact => artifact.Identity));
        m_AssetsByGuid = new ReadOnlyDictionary<Guid, AssetDescriptor>(
            artifacts
                .GroupBy(artifact => artifact.Guid)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        RuntimeAssetCatalogArtifact artifact = group.First();
                        return new AssetDescriptor(
                            artifact.Guid,
                            artifact.AssetType,
                            artifact.PackageId);
                    }));
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public string TargetProfile { get; }

    public IReadOnlyList<RuntimeAssetCatalogRoot> Roots { get; }

    public IReadOnlyList<RuntimeAssetCatalogArtifact> Artifacts { get; }

    public static RuntimeAssetCatalog Create(
        string targetProfile,
        IEnumerable<RuntimeAssetCatalogRoot> roots,
        IEnumerable<RuntimeAssetCatalogArtifact> artifacts)
    {
        ValidateText(targetProfile, "target profile", MaxIdentityLength);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(artifacts);

        RuntimeAssetCatalogRoot[] rootArray = roots.ToArray();
        RuntimeAssetCatalogArtifact[] artifactArray = artifacts.ToArray();
        if (rootArray.Length > MaxRootCount)
        {
            throw Invalid($"Root count {rootArray.Length} exceeds the limit {MaxRootCount}.");
        }

        if (artifactArray.Length > MaxArtifactCount)
        {
            throw Invalid($"Artifact count {artifactArray.Length} exceeds the limit {MaxArtifactCount}.");
        }

        var artifactIdentities = new HashSet<RuntimeAssetIdentity>();
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetDescriptors = new Dictionary<Guid, AssetDescriptor>();
        var canonicalArtifacts = new RuntimeAssetCatalogArtifact[artifactArray.Length];
        for (int artifactIndex = 0; artifactIndex < artifactArray.Length; artifactIndex++)
        {
            RuntimeAssetCatalogArtifact artifact = artifactArray[artifactIndex]
                ?? throw Invalid($"Artifact {artifactIndex} is null.");
            ValidateIdentity(artifact.Guid, artifact.Variant, $"artifact {artifactIndex}");
            ValidateText(artifact.PackageId, $"artifact {artifact.Identity} package id", MaxIdentityLength);
            ValidateText(artifact.AssetType, $"artifact {artifact.Identity} asset type", MaxIdentityLength);
            ValidateOutputRelativePath(artifact.OutputRelativePath, artifact.Identity);
            ValidateSha256(artifact.Sha256, artifact.Identity);
            if (artifact.SizeInBytes < 0)
            {
                throw Invalid($"Artifact '{artifact.Identity}' has a negative byte size.");
            }

            if (artifact.FormatVersion <= 0)
            {
                throw Invalid($"Artifact '{artifact.Identity}' format version must be positive.");
            }

            if (!artifactIdentities.Add(artifact.Identity))
            {
                throw Invalid($"Duplicate artifact identity '{artifact.Identity}'.");
            }

            var descriptor = new AssetDescriptor(
                artifact.Guid,
                artifact.AssetType,
                artifact.PackageId);
            if (assetDescriptors.TryGetValue(artifact.Guid, out AssetDescriptor existingDescriptor))
            {
                if (!string.Equals(
                        existingDescriptor.AssetType,
                        descriptor.AssetType,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        existingDescriptor.PackageId,
                        descriptor.PackageId,
                        StringComparison.Ordinal))
                {
                    throw Invalid(
                        $"Artifacts for GUID '{artifact.Guid:D}' disagree on package or asset type: " +
                        $"'{existingDescriptor.PackageId}:{existingDescriptor.AssetType}' and " +
                        $"'{descriptor.PackageId}:{descriptor.AssetType}'.");
                }
            }
            else
            {
                assetDescriptors.Add(artifact.Guid, descriptor);
            }

            if (!outputPaths.Add(artifact.OutputRelativePath))
            {
                throw Invalid(
                    $"Duplicate output-relative artifact path '{artifact.OutputRelativePath}'. " +
                    "Paths must also be unique under case-insensitive filesystems.");
            }

            if (artifact.Dependencies.Count > MaxDependenciesPerArtifact)
            {
                throw Invalid(
                    $"Artifact '{artifact.Identity}' dependency count {artifact.Dependencies.Count} " +
                    $"exceeds the limit {MaxDependenciesPerArtifact}.");
            }

            var dependencyIdentities = new HashSet<RuntimeAssetIdentity>();
            var canonicalDependencies = artifact.Dependencies.ToArray();
            for (int dependencyIndex = 0; dependencyIndex < canonicalDependencies.Length; dependencyIndex++)
            {
                RuntimeAssetCatalogDependency dependency = canonicalDependencies[dependencyIndex];
                ValidateIdentity(
                    dependency.Guid,
                    dependency.Variant,
                    $"artifact {artifact.Identity} dependency {dependencyIndex}");
                ValidateText(
                    dependency.PackageId,
                    $"artifact {artifact.Identity} dependency {dependency.Identity} package id",
                    MaxIdentityLength);
                ValidateText(
                    dependency.AssetType,
                    $"artifact {artifact.Identity} dependency {dependency.Identity} asset type",
                    MaxIdentityLength);
                if (!dependencyIdentities.Add(dependency.Identity))
                {
                    throw Invalid(
                        $"Artifact '{artifact.Identity}' contains duplicate dependency '{dependency.Identity}'.");
                }
            }

            Array.Sort(canonicalDependencies, CompareDependencies);
            canonicalArtifacts[artifactIndex] = new RuntimeAssetCatalogArtifact(
                artifact.Guid,
                artifact.PackageId,
                artifact.AssetType,
                artifact.Variant,
                artifact.OutputRelativePath,
                artifact.SizeInBytes,
                artifact.Sha256,
                artifact.FormatVersion,
                canonicalDependencies);
        }

        Array.Sort(canonicalArtifacts, CompareArtifacts);
        var canonicalArtifactMap = canonicalArtifacts.ToDictionary(artifact => artifact.Identity);

        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int rootIndex = 0; rootIndex < rootArray.Length; rootIndex++)
        {
            RuntimeAssetCatalogRoot root = rootArray[rootIndex];
            ValidateText(root.Name, $"root {rootIndex} name", MaxIdentityLength);
            ValidateIdentity(root.Guid, root.Variant, $"root '{root.Name}'");
            ValidateText(root.PackageId, $"root '{root.Name}' package id", MaxIdentityLength);
            ValidateText(root.AssetType, $"root '{root.Name}' asset type", MaxIdentityLength);
            if (!rootNames.Add(root.Name))
            {
                throw Invalid($"Duplicate runtime root name '{root.Name}'.");
            }

            ValidateReference(
                root.Identity,
                root.PackageId,
                root.AssetType,
                $"Root '{root.Name}'",
                canonicalArtifactMap);
        }

        foreach (RuntimeAssetCatalogArtifact artifact in canonicalArtifacts)
        {
            foreach (RuntimeAssetCatalogDependency dependency in artifact.Dependencies)
            {
                ValidateReference(
                    dependency.Identity,
                    dependency.PackageId,
                    dependency.AssetType,
                    $"Artifact '{artifact.Identity}' dependency",
                    canonicalArtifactMap);
            }
        }

        Array.Sort(rootArray, CompareRoots);
        return new RuntimeAssetCatalog(targetProfile, rootArray, canonicalArtifacts);
    }

    public bool TryGetArtifact(
        Guid guid,
        string variant,
        out RuntimeAssetCatalogArtifact artifact)
    {
        if (guid != Guid.Empty && !string.IsNullOrWhiteSpace(variant))
        {
            return m_ArtifactsByIdentity.TryGetValue(new RuntimeAssetIdentity(guid, variant), out artifact!);
        }

        artifact = null!;
        return false;
    }

    public bool TryGetAsset(Guid guid, out AssetDescriptor asset)
    {
        if (guid != Guid.Empty)
        {
            return m_AssetsByGuid.TryGetValue(guid, out asset);
        }

        asset = default;
        return false;
    }

    public string ResolveArtifactPath(string contentRoot, Guid guid, string variant)
    {
        if (!TryGetArtifact(guid, variant, out RuntimeAssetCatalogArtifact artifact))
        {
            throw new KeyNotFoundException(
                $"Runtime asset '{guid:D}:{variant}' does not exist in the catalog.");
        }

        return ResolveUnderContentRoot(contentRoot, artifact.OutputRelativePath);
    }

    public void ValidateDeployment(string contentRoot)
    {
        string fullContentRoot = GetFullContentRoot(contentRoot);
        if (!Directory.Exists(fullContentRoot))
        {
            throw Invalid($"Deployed content root '{fullContentRoot}' does not exist.");
        }

        EnsureNotReparsePoint(fullContentRoot, "content root");
        foreach (RuntimeAssetCatalogArtifact artifact in Artifacts)
        {
            string artifactPath = ResolveUnderContentRoot(fullContentRoot, artifact.OutputRelativePath);
            if (!File.Exists(artifactPath))
            {
                throw Invalid(
                    $"Deployed artifact '{artifact.Identity}' is missing at '{artifact.OutputRelativePath}'.");
            }

            EnsurePathContainsNoReparsePoints(fullContentRoot, artifactPath, artifact.Identity);
            using var stream = new FileStream(
                artifactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != artifact.SizeInBytes)
            {
                throw Invalid(
                    $"Deployed artifact '{artifact.Identity}' size mismatch at " +
                    $"'{artifact.OutputRelativePath}': expected {artifact.SizeInBytes}, found {stream.Length}.");
            }

            string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"Deployed artifact '{artifact.Identity}' SHA-256 mismatch at " +
                    $"'{artifact.OutputRelativePath}': expected {artifact.Sha256}, found {actualHash}.");
            }
        }
    }

    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("targetProfile", TargetProfile);

            writer.WriteStartArray("roots");
            foreach (RuntimeAssetCatalogRoot root in Roots)
            {
                writer.WriteStartObject();
                writer.WriteString("name", root.Name);
                writer.WriteString("guid", root.Guid.ToString("D"));
                writer.WriteString("packageId", root.PackageId);
                writer.WriteString("assetType", root.AssetType);
                writer.WriteString("variant", root.Variant);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("artifacts");
            foreach (RuntimeAssetCatalogArtifact artifact in Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("guid", artifact.Guid.ToString("D"));
                writer.WriteString("packageId", artifact.PackageId);
                writer.WriteString("assetType", artifact.AssetType);
                writer.WriteString("variant", artifact.Variant);
                writer.WriteString("path", artifact.OutputRelativePath);
                writer.WriteNumber("sizeInBytes", artifact.SizeInBytes);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteNumber("formatVersion", artifact.FormatVersion);
                writer.WriteStartArray("dependencies");
                foreach (RuntimeAssetCatalogDependency dependency in artifact.Dependencies)
                {
                    writer.WriteStartObject();
                    writer.WriteString("guid", dependency.Guid.ToString("D"));
                    writer.WriteString("packageId", dependency.PackageId);
                    writer.WriteString("assetType", dependency.AssetType);
                    writer.WriteString("variant", dependency.Variant);
                    writer.WriteBoolean("required", dependency.Required);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static RuntimeAssetCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw Invalid("Catalog JSON is empty.");
        }

        if (utf8Json.Length > MaxCatalogBytes)
        {
            throw Invalid($"Catalog size {utf8Json.Length} exceeds the limit {MaxCatalogBytes} bytes.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonElement rootElement = document.RootElement;
            ValidateProperties(
                rootElement,
                "catalog",
                "schemaVersion",
                "targetProfile",
                "roots",
                "artifacts");

            int schemaVersion = ReadInt32(rootElement, "schemaVersion", "catalog");
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw Invalid(
                    $"Unsupported runtime asset catalog schema version {schemaVersion}; " +
                    $"expected {CurrentSchemaVersion}.");
            }

            string targetProfile = ReadString(rootElement, "targetProfile", "catalog", MaxIdentityLength);
            JsonElement rootsElement = ReadArray(rootElement, "roots", "catalog", MaxRootCount);
            var roots = new List<RuntimeAssetCatalogRoot>(rootsElement.GetArrayLength());
            int rootIndex = 0;
            foreach (JsonElement root in rootsElement.EnumerateArray())
            {
                string context = $"root {rootIndex}";
                ValidateProperties(root, context, "name", "guid", "packageId", "assetType", "variant");
                roots.Add(new RuntimeAssetCatalogRoot(
                    ReadString(root, "name", context, MaxIdentityLength),
                    ReadGuid(root, "guid", context),
                    ReadString(root, "packageId", context, MaxIdentityLength),
                    ReadString(root, "assetType", context, MaxIdentityLength),
                    ReadString(root, "variant", context, MaxIdentityLength)));
                rootIndex++;
            }

            JsonElement artifactsElement = ReadArray(
                rootElement,
                "artifacts",
                "catalog",
                MaxArtifactCount);
            var artifacts = new List<RuntimeAssetCatalogArtifact>(artifactsElement.GetArrayLength());
            int artifactIndex = 0;
            foreach (JsonElement artifact in artifactsElement.EnumerateArray())
            {
                string context = $"artifact {artifactIndex}";
                ValidateProperties(
                    artifact,
                    context,
                    "guid",
                    "packageId",
                    "assetType",
                    "variant",
                    "path",
                    "sizeInBytes",
                    "sha256",
                    "formatVersion",
                    "dependencies");

                JsonElement dependenciesElement = ReadArray(
                    artifact,
                    "dependencies",
                    context,
                    MaxDependenciesPerArtifact);
                var dependencies = new List<RuntimeAssetCatalogDependency>(
                    dependenciesElement.GetArrayLength());
                int dependencyIndex = 0;
                foreach (JsonElement dependency in dependenciesElement.EnumerateArray())
                {
                    string dependencyContext = $"{context} dependency {dependencyIndex}";
                    ValidateProperties(
                        dependency,
                        dependencyContext,
                        "guid",
                        "packageId",
                        "assetType",
                        "variant",
                        "required");
                    dependencies.Add(new RuntimeAssetCatalogDependency(
                        ReadGuid(dependency, "guid", dependencyContext),
                        ReadString(dependency, "packageId", dependencyContext, MaxIdentityLength),
                        ReadString(dependency, "assetType", dependencyContext, MaxIdentityLength),
                        ReadString(dependency, "variant", dependencyContext, MaxIdentityLength),
                        ReadBoolean(dependency, "required", dependencyContext)));
                    dependencyIndex++;
                }

                artifacts.Add(new RuntimeAssetCatalogArtifact(
                    ReadGuid(artifact, "guid", context),
                    ReadString(artifact, "packageId", context, MaxIdentityLength),
                    ReadString(artifact, "assetType", context, MaxIdentityLength),
                    ReadString(artifact, "variant", context, MaxIdentityLength),
                    ReadString(artifact, "path", context, MaxPathLength),
                    ReadInt64(artifact, "sizeInBytes", context),
                    ReadString(artifact, "sha256", context, 64),
                    ReadInt32(artifact, "formatVersion", context),
                    dependencies));
                artifactIndex++;
            }

            return Create(targetProfile, roots, artifacts);
        }
        catch (JsonException ex)
        {
            throw Invalid($"Catalog JSON is invalid: {ex.Message}", ex);
        }
    }

    private static void ValidateReference(
        RuntimeAssetIdentity identity,
        string packageId,
        string assetType,
        string context,
        IReadOnlyDictionary<RuntimeAssetIdentity, RuntimeAssetCatalogArtifact> artifacts)
    {
        if (!artifacts.TryGetValue(identity, out RuntimeAssetCatalogArtifact? artifact))
        {
            throw Invalid($"{context} references missing artifact '{identity}'.");
        }

        if (!string.Equals(packageId, artifact.PackageId, StringComparison.Ordinal) ||
            !string.Equals(assetType, artifact.AssetType, StringComparison.Ordinal))
        {
            throw Invalid(
                $"{context} identity '{identity}' declares '{packageId}:{assetType}', but the " +
                $"catalog artifact declares '{artifact.PackageId}:{artifact.AssetType}'.");
        }
    }

    private static void ValidateIdentity(Guid guid, string variant, string context)
    {
        if (guid == Guid.Empty)
        {
            throw Invalid($"{context} has an empty GUID.");
        }

        ValidateText(variant, $"{context} variant", MaxIdentityLength);
    }

    private static void ValidateText(string value, string context, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"The {context} must not be empty.");
        }

        if (value.Length > maxLength)
        {
            throw Invalid($"The {context} length {value.Length} exceeds the limit {maxLength}.");
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw Invalid($"The {context} must not contain leading or trailing whitespace.");
        }

        if (value.Any(char.IsControl))
        {
            throw Invalid($"The {context} must not contain control characters.");
        }
    }

    private static void ValidateOutputRelativePath(string path, RuntimeAssetIdentity identity)
    {
        ValidateText(path, $"artifact {identity} output-relative path", MaxPathLength);
        if (path[0] == '/' || path.Contains('\\') || path.Contains(':') || Path.IsPathRooted(path))
        {
            throw Invalid(
                $"Artifact '{identity}' path '{path}' must be relative and use forward slashes.");
        }

        string[] segments = path.Split('/', StringSplitOptions.None);
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw Invalid(
                    $"Artifact '{identity}' path '{path}' contains an empty or traversal segment.");
            }

            if (!string.Equals(segment, segment.TrimEnd(' ', '.'), StringComparison.Ordinal))
            {
                throw Invalid(
                    $"Artifact '{identity}' path '{path}' contains a segment ending in a space or dot.");
            }
        }
    }

    private static void ValidateSha256(string sha256, RuntimeAssetIdentity identity)
    {
        if (sha256 == null || sha256.Length != 64 || sha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw Invalid(
                $"Artifact '{identity}' SHA-256 must contain exactly 64 lowercase hexadecimal characters.");
        }
    }

    private static string ResolveUnderContentRoot(string contentRoot, string outputRelativePath)
    {
        string fullContentRoot = GetFullContentRoot(contentRoot);
        string platformRelativePath = outputRelativePath.Replace('/', Path.DirectorySeparatorChar);
        string resolvedPath = Path.GetFullPath(Path.Combine(fullContentRoot, platformRelativePath));
        string rootPrefix = Path.EndsInDirectorySeparator(fullContentRoot)
            ? fullContentRoot
            : fullContentRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolvedPath.StartsWith(rootPrefix, comparison))
        {
            throw Invalid(
                $"Artifact path '{outputRelativePath}' resolves outside content root '{fullContentRoot}'.");
        }

        return resolvedPath;
    }

    private static string GetFullContentRoot(string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            throw new ArgumentException("A non-empty content root is required.", nameof(contentRoot));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
    }

    private static void EnsurePathContainsNoReparsePoints(
        string contentRoot,
        string artifactPath,
        RuntimeAssetIdentity identity)
    {
        string relativePath = Path.GetRelativePath(contentRoot, artifactPath);
        string currentPath = contentRoot;
        foreach (string segment in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            EnsureNotReparsePoint(currentPath, $"artifact '{identity}' path");
        }
    }

    private static void EnsureNotReparsePoint(string path, string context)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid($"The {context} contains a symbolic link or reparse point at '{path}'.");
        }
    }

    private static int CompareRoots(RuntimeAssetCatalogRoot left, RuntimeAssetCatalogRoot right)
    {
        int result = StringComparer.Ordinal.Compare(left.Name, right.Name);
        if (result != 0)
        {
            return result;
        }

        result = left.Guid.CompareTo(right.Guid);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.Variant, right.Variant);
    }

    private static int CompareArtifacts(
        RuntimeAssetCatalogArtifact left,
        RuntimeAssetCatalogArtifact right)
    {
        int result = left.Guid.CompareTo(right.Guid);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.Variant, right.Variant);
    }

    private static int CompareDependencies(
        RuntimeAssetCatalogDependency left,
        RuntimeAssetCatalogDependency right)
    {
        int result = left.Guid.CompareTo(right.Guid);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.Variant, right.Variant);
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
        return result != 0 ? result : left.Required.CompareTo(right.Required);
    }

    private static void ValidateProperties(
        JsonElement element,
        string context,
        params string[] allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"The {context} must be a JSON object.");
        }

        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Invalid($"The {context} contains duplicate property '{property.Name}'.");
            }

            if (!allowed.Contains(property.Name))
            {
                throw Invalid($"The {context} contains unsupported property '{property.Name}'.");
            }
        }
    }

    private static JsonElement ReadArray(
        JsonElement element,
        string propertyName,
        string context,
        int maxCount)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"The {context} property '{propertyName}' must be an array.");
        }

        if (value.GetArrayLength() > maxCount)
        {
            throw Invalid(
                $"The {context} property '{propertyName}' count {value.GetArrayLength()} " +
                $"exceeds the limit {maxCount}.");
        }

        return value;
    }

    private static string ReadString(
        JsonElement element,
        string propertyName,
        string context,
        int maxLength)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"The {context} property '{propertyName}' must be a string.");
        }

        string result = value.GetString()!;
        if (result.Length > maxLength)
        {
            throw Invalid(
                $"The {context} property '{propertyName}' length {result.Length} " +
                $"exceeds the limit {maxLength}.");
        }

        return result;
    }

    private static Guid ReadGuid(JsonElement element, string propertyName, string context)
    {
        string value = ReadString(element, propertyName, context, 36);
        if (!Guid.TryParseExact(value, "D", out Guid guid))
        {
            throw Invalid(
                $"The {context} property '{propertyName}' must be a canonical GUID string.");
        }

        return guid;
    }

    private static int ReadInt32(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
        {
            throw Invalid($"The {context} property '{propertyName}' must be a 32-bit integer.");
        }

        return result;
    }

    private static long ReadInt64(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result))
        {
            throw Invalid($"The {context} property '{propertyName}' must be a 64-bit integer.");
        }

        return result;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid($"The {context} property '{propertyName}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static InvalidDataException Invalid(string message, Exception? innerException = null)
    {
        return new InvalidDataException($"[RuntimeAssetCatalog] {message}", innerException);
    }
}
