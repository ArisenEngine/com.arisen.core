using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Core.Assets;

/// <summary>
/// A centralized registry that indexes discovered assets in the project directory,
/// matching files to their serialized .meta files and GUIDs.
/// </summary>
public class AssetDatabase : IAssetDatabase, IAssetSourceIndex, ICookedArtifactWriteOwner
{
    private static AssetDatabase? s_Instance;
    public static AssetDatabase Instance => s_Instance ??= new AssetDatabase();

    private readonly Dictionary<Guid, AssetRecord> m_AssetRegistry = new();
    private readonly Dictionary<string, Guid> m_PathRegistry = new();
    private readonly object m_SourceRegistryGate = new();
    private readonly object m_CookedRegistryWriterGate = new();
    private CookedRegistrySnapshot m_CookedRegistrySnapshot = CookedRegistrySnapshot.Empty;
    private readonly Dictionary<string, int> m_LoadedCookedAssetSlotsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoadedCookedAssetSlot> m_LoadedCookedAssetSlots = new();
    private readonly Stack<int> m_FreeLoadedCookedAssetSlots = new();
    private readonly object m_LoadedCookedAssetGate = new();
    private string m_CookedManifestPath = string.Empty;
    private RuntimeAssetCatalog? m_RuntimeCatalog;

    internal Action<CookedManifestCommit>? BeforeCookedManifestReplace { get; set; }

    internal long CookedRegistryGeneration => ReadCookedRegistrySnapshot().Generation;

    public AssetDatabaseMode Mode { get; private set; } = AssetDatabaseMode.Uninitialized;

    public bool IsReadOnlyRuntime => Mode == AssetDatabaseMode.ReadOnlyRuntime;

    public AssetSourceAccessMode SourceAccessMode { get; private set; } =
        AssetSourceAccessMode.Disabled;

    public bool CanReadSourceAssets => SourceAccessMode != AssetSourceAccessMode.Disabled;

    public string CookedRoot { get; private set; } = string.Empty;

    public IReadOnlyCollection<AssetRecord> Assets
    {
        get
        {
            lock (m_SourceRegistryGate) return m_AssetRegistry.Values.ToArray();
        }
    }

    public event Action<AssetChangeEvent>? AssetChanged;

    /// <summary>
    /// Scans the given project directory to index all assets and automatically provisions missing .meta files.
    /// </summary>
    public void Initialize(
        string projectContentPath,
        AssetSourceAccessMode sourceAccessMode)
    {
        lock (m_SourceRegistryGate)
        {
            m_AssetRegistry.Clear();
            m_PathRegistry.Clear();
        }

        lock (m_CookedRegistryWriterGate)
        {
            Mode = AssetDatabaseMode.Workspace;
            SourceAccessMode = sourceAccessMode;
            m_RuntimeCatalog = null;

            if (!Directory.Exists(projectContentPath))
            {
                CookedRoot = string.Empty;
                m_CookedManifestPath = string.Empty;
                ReplaceCookedRegistryWithoutManifest(
                    new Dictionary<string, CookedAssetRecord>(StringComparer.OrdinalIgnoreCase),
                    persistedGeneration: 0);
                return;
            }

            CookedRoot = Path.Combine(projectContentPath, ".arisen", "Cache", "CookedAssets");
            Directory.CreateDirectory(CookedRoot);
            m_CookedManifestPath = Path.Combine(CookedRoot, "AssetManifest.json");
            CookedManifestLoadResult loadedManifest = LoadCookedManifest(m_CookedManifestPath);
            ReplaceCookedRegistryWithoutManifest(
                loadedManifest.Artifacts,
                loadedManifest.Generation);
        }

        RefreshDirectory(projectContentPath);
    }

    public void InitializeWorkspace(
        string workspaceRoot,
        IEnumerable<(string PackageId, string PackageRoot)> packages,
        AssetSourceAccessMode sourceAccessMode)
    {
        ArgumentNullException.ThrowIfNull(packages);
        lock (m_SourceRegistryGate)
        {
            m_AssetRegistry.Clear();
            m_PathRegistry.Clear();
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            workspaceRoot = Directory.GetCurrentDirectory();
        }

        lock (m_CookedRegistryWriterGate)
        {
            Mode = AssetDatabaseMode.Workspace;
            SourceAccessMode = sourceAccessMode;
            m_RuntimeCatalog = null;
            CookedRoot = Path.Combine(workspaceRoot, ".arisen", "Cache", "CookedAssets");
            Directory.CreateDirectory(CookedRoot);
            m_CookedManifestPath = Path.Combine(CookedRoot, "AssetManifest.json");
            CookedManifestLoadResult loadedManifest = LoadCookedManifest(m_CookedManifestPath);
            ReplaceCookedRegistryWithoutManifest(
                loadedManifest.Artifacts,
                loadedManifest.Generation);
        }

        var workspaceAssets = Path.Combine(workspaceRoot, "Assets");
        if (Directory.Exists(workspaceAssets))
        {
            RefreshDirectory(workspaceAssets, "workspace");
        }

        foreach (var (packageId, packageRoot) in packages)
        {
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(packageId))
            {
                throw new InvalidOperationException(
                    $"Asset indexing requires a package id for root '{packageRoot}'.");
            }

            var assetsRoot = Path.Combine(packageRoot, "Assets");
            if (!Directory.Exists(assetsRoot))
            {
                continue;
            }

            RefreshDirectory(assetsRoot, packageId.Trim());
        }

        int indexedAssetCount;
        lock (m_SourceRegistryGate)
        {
            indexedAssetCount = m_AssetRegistry.Count;
        }

        Logger.Info($"[AssetDatabase] Indexed {indexedAssetCount} asset(s). CookedRoot: {CookedRoot}");
    }

    public void InitializeRuntimeCatalog(string outputRoot, string expectedProfile)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException(
                "[AssetDatabase] Runtime output root cannot be empty.",
                nameof(outputRoot));
        }

        if (string.IsNullOrWhiteSpace(expectedProfile))
        {
            throw new ArgumentException(
                "[AssetDatabase] Expected runtime profile cannot be empty.",
                nameof(expectedProfile));
        }

        string fullOutputRoot = Path.GetFullPath(outputRoot);
        string catalogPath = Path.Combine(fullOutputRoot, RuntimeAssetCatalog.DefaultFileName);
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                $"[AssetDatabase] Runtime asset catalog was not found at '{catalogPath}'.",
                catalogPath);
        }

        RuntimeAssetCatalog catalog = RuntimeAssetCatalog.Parse(File.ReadAllBytes(catalogPath));
        if (!string.Equals(
                catalog.TargetProfile,
                expectedProfile.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"[AssetDatabase] Runtime asset catalog targets profile " +
                $"'{catalog.TargetProfile}', expected '{expectedProfile.Trim()}'.");
        }

        string contentRoot = Path.Combine(
            fullOutputRoot,
            RuntimeAssetDeployment.ContentDirectoryName);
        catalog.ValidateDeployment(contentRoot);

        var mountedArtifacts = new Dictionary<string, CookedAssetRecord>(
            catalog.Artifacts.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeAssetCatalogArtifact artifact in catalog.Artifacts)
        {
            string path = catalog.ResolveArtifactPath(
                contentRoot,
                artifact.Guid,
                artifact.Variant);
            mountedArtifacts.Add(
                MakeCookedKey(artifact.Guid, artifact.Variant),
                new CookedAssetRecord(
                    artifact.Guid,
                    artifact.AssetType,
                    artifact.Variant,
                    path,
                    artifact.SizeInBytes,
                    File.GetLastWriteTimeUtc(path)));
        }

        lock (m_SourceRegistryGate)
        {
            m_AssetRegistry.Clear();
            m_PathRegistry.Clear();
        }

        lock (m_CookedRegistryWriterGate)
        {
            CookedRoot = Path.GetFullPath(contentRoot);
            m_CookedManifestPath = string.Empty;
            m_RuntimeCatalog = catalog;
            Mode = AssetDatabaseMode.ReadOnlyRuntime;
            SourceAccessMode = AssetSourceAccessMode.Disabled;
            ReplaceCookedRegistryWithoutManifest(mountedArtifacts, persistedGeneration: 0);
        }
        Logger.Info(
            $"[AssetDatabase] Mounted read-only runtime catalog for profile " +
            $"'{catalog.TargetProfile}' with {catalog.Artifacts.Count} artifact(s) from '{catalogPath}'.");
    }

    /// <summary>
    /// Refreshes the indexing of a directory recursively, creating .meta files for any asset without one.
    /// </summary>
    public void RefreshDirectory(string directoryPath)
    {
        EnsureMutable("refresh source assets");
        RefreshDirectory(directoryPath, "workspace");
    }

    public void RefreshDirectory(string directoryPath, string packageId)
    {
        EnsureMutable("refresh source assets");
        lock (m_SourceRegistryGate)
        {
            RefreshDirectoryCore(directoryPath, packageId);
        }
    }

    private void RefreshDirectoryCore(string directoryPath, string packageId)
    {
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories))
        {
            if (IsIgnoredPath(filePath))
                continue;

            string metaPath = filePath + ".meta";
            AssetMetadata meta;

            if (File.Exists(metaPath))
            {
                try 
                {
                    meta = SerializationUtil.Deserialize<AssetMetadata>(metaPath, serializeIfNotExist: false);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to parse metadata for {filePath}: {ex.Message}");
                    continue;
                }
            }
            else
            {
                var assetType = InferAssetType(filePath);
                meta = new AssetMetadata
                {
                    Guid = Guid.NewGuid(),
                    AssetType = assetType,
                    Importer = InferImporter(filePath, assetType)
                };
                
                try 
                {
                    SerializationUtil.Serialize(meta, metaPath);
                    Logger.Info($"Generated new .meta file for discovered asset: {filePath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to formulate metadata file for {filePath}: {ex.Message}");
                    continue;
                }
            }

            var metadataChanged = false;
            if (string.IsNullOrWhiteSpace(meta.AssetType))
            {
                meta.AssetType = InferAssetType(filePath);
                metadataChanged = true;
            }

            if (string.IsNullOrWhiteSpace(meta.Importer))
            {
                meta.Importer = InferImporter(filePath, meta.AssetType);
                metadataChanged = true;
            }

            if (metadataChanged)
            {
                try
                {
                    SerializationUtil.Serialize(meta, metaPath);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to update metadata file for {filePath}: {ex.Message}");
                    continue;
                }
            }

            var fullPath = Path.GetFullPath(filePath);
            var fullMetaPath = Path.GetFullPath(metaPath);
            if (m_AssetRegistry.TryGetValue(meta.Guid, out var existing)
                && !string.Equals(existing.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Duplicate asset GUID '{meta.Guid}' found at '{fullPath}' and '{existing.SourcePath}'.");
            }

            m_AssetRegistry[meta.Guid] = new AssetRecord(
                meta.Guid,
                meta.AssetType,
                fullPath,
                fullMetaPath,
                packageId);
            m_PathRegistry[fullPath] = meta.Guid;
        }
    }

    public void RefreshSourceDirectory(string directoryPath, string packageId)
    {
        EnsureMutable("refresh the source asset index");
        if (!CanReadSourceAssets)
        {
            throw new InvalidOperationException(
                "[AssetDatabase] Source indexing is unavailable when source access is disabled.");
        }

        if (string.IsNullOrWhiteSpace(directoryPath) ||
            string.IsNullOrWhiteSpace(packageId) ||
            !string.Equals(packageId, packageId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "[AssetDatabase] Source refresh requires a directory and canonical package id.");
        }

        string directory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!IsUnderAssetsRoot(directory) || ContainsPathSegment(directory, ".arisen"))
        {
            throw new InvalidOperationException(
                $"[AssetDatabase] Source refresh must stay under an Assets root: '{directory}'.");
        }

        lock (m_SourceRegistryGate)
        {
            AssetRecord[] replaced = GetSourceAssetsUnder(directory, packageId);
            RemoveSourceAssets(replaced);

            try
            {
                if (Directory.Exists(directory))
                {
                    RefreshDirectoryCore(directory, packageId);
                }
            }
            catch
            {
                RemoveSourceAssets(GetSourceAssetsUnder(directory, packageId));
                foreach (AssetRecord asset in replaced)
                {
                    m_AssetRegistry[asset.Guid] = asset;
                    m_PathRegistry[Path.GetFullPath(asset.SourcePath)] = asset.Guid;
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Looks up the absolute file path for a registered asset reference.
    /// </summary>
    public string? GetAssetPath(Guid guid)
    {
        lock (m_SourceRegistryGate)
        {
            return CanReadSourceAssets && m_AssetRegistry.TryGetValue(guid, out var asset)
                ? asset.SourcePath
                : null;
        }
    }

    public bool TryGetAsset(Guid guid, out AssetRecord asset)
    {
        if (CanReadSourceAssets)
        {
            lock (m_SourceRegistryGate)
            {
                return m_AssetRegistry.TryGetValue(guid, out asset!);
            }
        }

        asset = null!;
        return false;
    }

    public bool TryGetAssetDescriptor(Guid guid, out AssetDescriptor asset)
    {
        lock (m_SourceRegistryGate)
        {
            if (m_AssetRegistry.TryGetValue(guid, out AssetRecord? sourceAsset))
            {
                asset = new AssetDescriptor(
                    sourceAsset.Guid,
                    sourceAsset.AssetType,
                    sourceAsset.PackageId);
                return true;
            }
        }

        if (m_RuntimeCatalog != null && m_RuntimeCatalog.TryGetAsset(guid, out asset))
        {
            return true;
        }

        asset = default;
        return false;
    }

    public bool TryGetCookedArtifact(Guid guid, string variant, out CookedAssetRecord artifact)
    {
        CookedRegistrySnapshot snapshot = ReadCookedRegistrySnapshot();
        return snapshot.Artifacts.TryGetValue(MakeCookedKey(guid, variant), out artifact!);
    }

    public CookedArtifactWrite BeginCookedArtifactWrite(
        Guid guid,
        string variant,
        string extension)
    {
        ValidateCookedIdentity(new CookedAssetIdentity(guid, variant));
        string normalizedExtension = NormalizeCookedExtension(extension);
        Guid transactionId = Guid.NewGuid();
        string cookedRoot;
        string outputPath;
        lock (m_CookedRegistryWriterGate)
        {
            EnsureMutable("begin a cooked artifact write");
            if (string.IsNullOrWhiteSpace(CookedRoot))
            {
                throw new InvalidOperationException("[AssetDatabase] CookedRoot is not initialized.");
            }

            cookedRoot = Path.GetFullPath(CookedRoot);
            string transactionRoot = Path.Combine(
                cookedRoot,
                ".staging",
                transactionId.ToString("N"));
            Directory.CreateDirectory(transactionRoot);
            outputPath = Path.Combine(transactionRoot, "artifact" + normalizedExtension);
        }

        return new CookedArtifactWrite(
            this,
            transactionId,
            guid,
            variant.Trim(),
            normalizedExtension,
            cookedRoot,
            outputPath);
    }

    CookedAssetRecord ICookedArtifactWriteOwner.CommitCookedArtifactWrite(
        CookedArtifactWrite write,
        string assetType)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (string.IsNullOrWhiteSpace(assetType))
        {
            throw new ArgumentException(
                "[AssetDatabase] Cooked artifact type cannot be empty.",
                nameof(assetType));
        }

        string key = MakeCookedKey(write.Guid, write.Variant);
        CookedAssetRecord artifact;
        CookedArtifactRemoval? supersededArtifactRemoval = null;
        bool superseded;
        bool reused;
        long generation;
        lock (m_CookedRegistryWriterGate)
        {
            EnsureMutable("commit a cooked artifact write");
            ValidateCookedArtifactWrite(write);
            if (!File.Exists(write.OutputPath))
            {
                throw new FileNotFoundException(
                    "[AssetDatabase] Cooked artifact write produced no staged file.",
                    write.OutputPath);
            }

            CookedRegistrySnapshot previous = ReadCookedRegistrySnapshot();
            generation = NextCookedRegistryGeneration(previous.Generation);
            if (previous.Artifacts.TryGetValue(key, out CookedAssetRecord? currentArtifact) &&
                currentArtifact != null &&
                string.Equals(
                    currentArtifact.AssetType,
                    assetType.Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                FilesAreByteIdentical(currentArtifact.Path, write.OutputPath))
            {
                File.Delete(write.OutputPath);
                artifact = currentArtifact;
                superseded = false;
                reused = true;
                generation = previous.Generation;
            }
            else
            {
                reused = false;
                string finalDirectory = Path.Combine(write.CookedRoot, write.Guid.ToString("N"));
                Directory.CreateDirectory(finalDirectory);
                string finalPath = Path.Combine(
                    finalDirectory,
                    $"{SanitizeFileName(write.Variant)}.g{generation:D20}." +
                    $"{write.TransactionId:N}{write.Extension}");
                File.Move(write.OutputPath, finalPath);

                try
                {
                    var output = new FileInfo(finalPath);
                    if (!output.Exists || output.Length == 0)
                    {
                        throw new InvalidOperationException(
                            $"[AssetDatabase] Staged cooked artifact '{write.OutputPath}' is empty.");
                    }

                    artifact = new CookedAssetRecord(
                        write.Guid,
                        assetType.Trim(),
                        write.Variant,
                        output.FullName,
                        output.Length,
                        output.LastWriteTimeUtc);
                    if (previous.Artifacts.TryGetValue(key, out CookedAssetRecord? previousArtifact) &&
                        previousArtifact != null &&
                        !string.Equals(previousArtifact.Path, artifact.Path, StringComparison.OrdinalIgnoreCase) &&
                        !previous.Artifacts.Any(pair =>
                            !string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(
                                pair.Value.Path,
                                previousArtifact.Path,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            string previousPath = ValidateCookedCachePath(
                                write.CookedRoot,
                                previousArtifact);
                            supersededArtifactRemoval = new CookedArtifactRemoval(
                                key,
                                previousArtifact,
                                previousPath,
                                Path.GetRelativePath(write.CookedRoot, previousPath));
                        }
                        catch (InvalidOperationException ex)
                        {
                            Logger.Warning(
                                $"[AssetDatabase] Superseded cooked artifact " +
                                $"'{previousArtifact.Path}' was not cache-owned and will not be quarantined: " +
                                ex.Message);
                        }
                    }

                    (superseded, _) = PublishCookedArtifactCore(previous, artifact);
                }
                catch (Exception commitError)
                {
                    try
                    {
                        File.Move(finalPath, write.OutputPath);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException(
                            $"[AssetDatabase] Cooked artifact publication for '{write.Guid}' " +
                            $"variant '{write.Variant}' failed and its staged file could not be restored.",
                            new AggregateException(commitError, rollbackError));
                    }

                    throw;
                }

                if (supersededArtifactRemoval != null)
                {
                    QuarantineCommittedCookedArtifacts(
                        write.CookedRoot,
                        [supersededArtifactRemoval]);
                }
            }
        }

        TryDeleteEmptyDirectory(
            Path.GetDirectoryName(write.OutputPath),
            write.CookedRoot);
        TryDeleteEmptyDirectory(
            Path.Combine(write.CookedRoot, ".staging"),
            write.CookedRoot);
        Logger.Info(reused
            ? $"[AssetDatabase] Reused byte-identical cooked asset {artifact.Guid} | " +
              $"Variant: {artifact.Variant} | RegistryGeneration: {generation}"
            : $"[AssetDatabase] Published cooked asset {artifact.Guid} | " +
              $"Variant: {artifact.Variant} | RegistryGeneration: {generation}");
        if (superseded)
        {
            Logger.Info(
                $"[AssetDatabase] Superseded loaded cooked asset {artifact.Guid} | " +
                $"Variant: {artifact.Variant} | RegistryGeneration: {generation}");
        }

        return artifact;
    }

    void ICookedArtifactWriteOwner.DiscardCookedArtifactWrite(CookedArtifactWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        try
        {
            ValidateCookedArtifactWriteIdentity(write);
            if (File.Exists(write.OutputPath))
            {
                File.Delete(write.OutputPath);
            }

            TryDeleteEmptyDirectory(
                Path.GetDirectoryName(write.OutputPath),
                write.CookedRoot);
            TryDeleteEmptyDirectory(
                Path.Combine(write.CookedRoot, ".staging"),
                write.CookedRoot);
        }
        catch (Exception ex)
        {
            Logger.Warning(
                $"[AssetDatabase] Failed to discard cooked artifact staging " +
                $"'{write.OutputPath}': {ex.Message}");
        }
    }

    internal void RegisterExistingCookedArtifact(CookedAssetRecord artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        bool superseded;
        long generation;
        lock (m_CookedRegistryWriterGate)
        {
            EnsureMutable("register an existing cooked artifact");
            CookedRegistrySnapshot previous = ReadCookedRegistrySnapshot();
            (superseded, generation) = PublishCookedArtifactCore(previous, artifact);
        }

        if (superseded)
        {
            Logger.Info(
                $"[AssetDatabase] Superseded loaded cooked asset {artifact.Guid} | " +
                $"Variant: {artifact.Variant} | RegistryGeneration: {generation}");
        }
    }

    private (bool Superseded, long Generation) PublishCookedArtifactCore(
        CookedRegistrySnapshot previous,
        CookedAssetRecord artifact)
    {
        string key = MakeCookedKey(artifact.Guid, artifact.Variant);
        Dictionary<string, CookedAssetRecord> candidateArtifacts = CloneCookedArtifacts(previous);
        candidateArtifacts[key] = artifact;
        CookedRegistrySnapshot candidate = CreateCookedRegistrySnapshot(
            NextCookedRegistryGeneration(previous.Generation),
            candidateArtifacts);

        lock (m_LoadedCookedAssetGate)
        {
            EnsureExpectedCookedRegistrySnapshot(previous);
            WriteCookedManifest(previous, candidate);
            bool superseded = SupersedeLoadedCookedAssetCore(key);
            Volatile.Write(ref m_CookedRegistrySnapshot, candidate);
            return (superseded, candidate.Generation);
        }
    }

    public bool TryLoadCookedAsset(Guid guid, string variant, string expectedAssetType, out CookedAssetHandle handle)
    {
        handle = CookedAssetHandle.Invalid;

        if (!TryGetAssetDescriptor(guid, out AssetDescriptor asset))
        {
            Logger.Warning(
                $"[AssetDatabase] Cannot load cooked asset '{guid}': asset identity is not indexed or cataloged.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedAssetType)
            && !string.Equals(asset.AssetType, expectedAssetType, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warning(
                $"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': expected '{expectedAssetType}', found '{asset.AssetType}'.");
            return false;
        }

        string key = MakeCookedKey(guid, variant);
        while (true)
        {
            CookedRegistrySnapshot snapshot = ReadCookedRegistrySnapshot();
            if (!snapshot.Artifacts.TryGetValue(key, out CookedAssetRecord? artifact))
            {
                Logger.Warning(
                    $"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': " +
                    "cooked artifact is not registered.");
                return false;
            }

            lock (m_LoadedCookedAssetGate)
            {
                if (!ReferenceEquals(snapshot, ReadCookedRegistrySnapshot()))
                {
                    continue;
                }

                if (m_LoadedCookedAssetSlotsByKey.TryGetValue(key, out int existingIndex))
                {
                    LoadedCookedAssetSlot existingSlot = m_LoadedCookedAssetSlots[existingIndex];
                    existingSlot.RefCount++;
                    handle = new CookedAssetHandle(
                        existingIndex,
                        existingSlot.Generation,
                        existingSlot.Guid,
                        existingSlot.Variant);
                    return true;
                }
            }

            if (!File.Exists(artifact.Path))
            {
                if (!ReferenceEquals(snapshot, ReadCookedRegistrySnapshot()))
                {
                    continue;
                }

                Logger.Warning(
                    $"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': " +
                    $"missing file '{artifact.Path}'.");
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(artifact.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!ReferenceEquals(snapshot, ReadCookedRegistrySnapshot()))
                {
                    continue;
                }

                Logger.Warning(
                    $"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}' " +
                    $"from '{artifact.Path}': {ex.Message}");
                return false;
            }

            if (bytes.Length == 0)
            {
                if (!ReferenceEquals(snapshot, ReadCookedRegistrySnapshot()))
                {
                    continue;
                }

                Logger.Warning(
                    $"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': file is empty.");
                return false;
            }

            lock (m_LoadedCookedAssetGate)
            {
                if (!ReferenceEquals(snapshot, ReadCookedRegistrySnapshot()))
                {
                    continue;
                }

                if (m_LoadedCookedAssetSlotsByKey.TryGetValue(key, out int racedIndex))
                {
                    LoadedCookedAssetSlot racedSlot = m_LoadedCookedAssetSlots[racedIndex];
                    racedSlot.RefCount++;
                    handle = new CookedAssetHandle(
                        racedIndex,
                        racedSlot.Generation,
                        racedSlot.Guid,
                        racedSlot.Variant);
                    return true;
                }

                int slotIndex = AllocateLoadedCookedAssetSlot();
                LoadedCookedAssetSlot slot = m_LoadedCookedAssetSlots[slotIndex];
                slot.IsOccupied = true;
                slot.Key = key;
                slot.Guid = guid;
                slot.AssetType = asset.AssetType;
                slot.Variant = variant;
                slot.Path = Path.GetFullPath(artifact.Path);
                slot.Data = bytes;
                slot.RefCount = 1;
                slot.LastWriteTimeUtc = artifact.LastWriteTimeUtc;
                m_LoadedCookedAssetSlotsByKey[key] = slotIndex;
                handle = new CookedAssetHandle(slotIndex, slot.Generation, slot.Guid, slot.Variant);
            }

            Logger.Info(
                $"[AssetDatabase] Loaded cooked asset {guid} | Variant: {variant} | " +
                $"Size: {bytes.Length} bytes | Handle: {handle.Index}:{handle.Generation} | " +
                $"RegistryGeneration: {snapshot.Generation}");
            return true;
        }
    }

    public bool TryGetCookedAssetBytes(CookedAssetHandle handle, out ReadOnlyMemory<byte> bytes)
    {
        lock (m_LoadedCookedAssetGate)
        {
            bytes = ReadOnlyMemory<byte>.Empty;
            if (!TryGetLoadedCookedAssetSlot(handle, out var slot))
            {
                return false;
            }

            bytes = slot.Data;
            return true;
        }
    }

    public ReadOnlyMemory<byte> GetCookedAssetBytes(CookedAssetHandle handle)
    {
        if (!TryGetCookedAssetBytes(handle, out var bytes))
        {
            throw new InvalidOperationException($"[AssetDatabase] Invalid cooked asset handle {handle.Index}:{handle.Generation}.");
        }

        return bytes;
    }

    public void Release(CookedAssetHandle handle)
    {
        lock (m_LoadedCookedAssetGate)
        {
            if (!TryGetLoadedCookedAssetSlot(handle, out var slot))
            {
                Logger.Warning($"[AssetDatabase] Ignored release for invalid cooked asset handle {handle.Index}:{handle.Generation}.");
                return;
            }

            slot.RefCount--;
            if (slot.RefCount > 0)
            {
                return;
            }

            Logger.Info(
                $"[AssetDatabase] Unloaded cooked asset {slot.Guid} | Variant: {slot.Variant} | Handle: {handle.Index}:{handle.Generation}");

            if (m_LoadedCookedAssetSlotsByKey.TryGetValue(
                    slot.Key,
                    out int currentIndex) &&
                currentIndex == handle.Index)
            {
                m_LoadedCookedAssetSlotsByKey.Remove(slot.Key);
            }
            slot.Reset();
            m_FreeLoadedCookedAssetSlots.Push(handle.Index);
        }
    }

    public void ReleaseAllLoadedCookedAssets()
    {
        lock (m_LoadedCookedAssetGate)
        {
            ReleaseAllLoadedCookedAssetsCore();
        }
    }

    public int InvalidateCookedAssets(Guid guid, string? variant = null)
    {
        if (guid == Guid.Empty)
        {
            return 0;
        }

        int releasedCount;
        int removedCount;
        long generation;
        lock (m_CookedRegistryWriterGate)
        {
            EnsureMutable("invalidate cooked artifacts");
            CookedRegistrySnapshot previous = ReadCookedRegistrySnapshot();
            string[] registryKeys = previous.Artifacts.Keys
                .Where(key => IsCookedKeyMatch(key, guid, variant))
                .ToArray();
            removedCount = registryKeys.Length;

            if (removedCount == 0)
            {
                lock (m_LoadedCookedAssetGate)
                {
                    releasedCount = ReleaseLoadedCookedAssetsCore(guid, variant);
                }

                generation = previous.Generation;
            }
            else
            {
                Dictionary<string, CookedAssetRecord> candidateArtifacts = CloneCookedArtifacts(previous);
                foreach (string key in registryKeys)
                {
                    candidateArtifacts.Remove(key);
                }

                CookedRegistrySnapshot candidate = CreateCookedRegistrySnapshot(
                    NextCookedRegistryGeneration(previous.Generation),
                    candidateArtifacts);
                WriteCookedManifest(previous, candidate);
                lock (m_LoadedCookedAssetGate)
                {
                    EnsureExpectedCookedRegistrySnapshot(previous);
                    releasedCount = ReleaseLoadedCookedAssetsCore(guid, variant);
                    Volatile.Write(ref m_CookedRegistrySnapshot, candidate);
                }

                generation = candidate.Generation;
            }
        }

        if (releasedCount > 0 || removedCount > 0)
        {
            AssetRecord? asset = GetSourceAsset(guid);
            NotifyAssetChanged(new AssetChangeEvent(
                AssetChangeKind.CookedInvalidated,
                guid,
                asset?.AssetType ?? string.Empty,
                asset?.SourcePath ?? string.Empty,
                string.Empty,
                asset?.PackageId ?? string.Empty));

            Logger.Info(
                $"[AssetDatabase] Invalidated cooked asset {guid} | Variant: {variant ?? "<all>"} | " +
                $"LoadedReleased: {releasedCount} | RegistryRemoved: {removedCount} | " +
                $"RegistryGeneration: {generation}");
        }

        return releasedCount;
    }

    public int RemoveCookedArtifacts(IReadOnlyCollection<CookedAssetIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count == 0)
        {
            return 0;
        }

        List<CookedArtifactRemoval> removals;
        string cookedRoot;
        int releasedCount;
        long generation;
        lock (m_CookedRegistryWriterGate)
        {
            EnsureMutable("remove cooked artifacts");
            if (string.IsNullOrWhiteSpace(CookedRoot))
            {
                throw new InvalidOperationException("[AssetDatabase] CookedRoot is not initialized.");
            }

            cookedRoot = Path.GetFullPath(CookedRoot);
            CookedRegistrySnapshot previous = ReadCookedRegistrySnapshot();
            var observedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var observedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            removals = new List<CookedArtifactRemoval>(identities.Count);
            foreach (CookedAssetIdentity identity in identities
                         .OrderBy(identity => identity.Guid)
                         .ThenBy(identity => identity.Variant, StringComparer.Ordinal))
            {
                ValidateCookedIdentity(identity);
                string registryKey = MakeCookedKey(identity.Guid, identity.Variant);
                if (!observedKeys.Add(registryKey) ||
                    !previous.Artifacts.TryGetValue(registryKey, out CookedAssetRecord? artifact))
                {
                    continue;
                }

                string artifactPath = ValidateCookedCachePath(cookedRoot, artifact);
                if (!observedPaths.Add(artifactPath))
                {
                    throw new InvalidOperationException(
                        $"[AssetDatabase] Cooked artifacts selected for removal share path " +
                        $"'{artifactPath}'.");
                }

                removals.Add(new CookedArtifactRemoval(
                    registryKey,
                    artifact,
                    artifactPath,
                    Path.GetRelativePath(cookedRoot, artifactPath)));
            }

            if (removals.Count == 0)
            {
                return 0;
            }

            Dictionary<string, CookedAssetRecord> candidateArtifacts = CloneCookedArtifacts(previous);
            foreach (CookedArtifactRemoval removal in removals)
            {
                candidateArtifacts.Remove(removal.RegistryKey);
            }

            CookedRegistrySnapshot candidate = CreateCookedRegistrySnapshot(
                NextCookedRegistryGeneration(previous.Generation),
                candidateArtifacts);
            WriteCookedManifest(previous, candidate);

            lock (m_LoadedCookedAssetGate)
            {
                EnsureExpectedCookedRegistrySnapshot(previous);
                releasedCount = 0;
                foreach (CookedArtifactRemoval removal in removals)
                {
                    releasedCount += ReleaseLoadedCookedAssetsCore(
                        removal.Artifact.Guid,
                        removal.Artifact.Variant);
                }

                Volatile.Write(ref m_CookedRegistrySnapshot, candidate);
            }

            generation = candidate.Generation;
            QuarantineCommittedCookedArtifacts(cookedRoot, removals);
        }

        List<AssetChangeEvent> changes = removals
            .Select(removal =>
            {
                AssetRecord? asset = GetSourceAsset(removal.Artifact.Guid);
                return new AssetChangeEvent(
                    AssetChangeKind.CookedInvalidated,
                    removal.Artifact.Guid,
                    removal.Artifact.AssetType,
                    asset?.SourcePath ?? string.Empty,
                    string.Empty,
                    asset?.PackageId ?? string.Empty);
            })
            .ToList();
        foreach (AssetChangeEvent change in changes)
        {
            NotifyAssetChanged(change);
        }

        Logger.Info(
            $"[AssetDatabase] Removed {removals.Count} cooked artifact(s) transactionally | " +
            $"LoadedReleased: {releasedCount} | RegistryGeneration: {generation}");
        return removals.Count;
    }

    public void NotifyAssetChanged(AssetChangeEvent change)
    {
        if (change.Guid == Guid.Empty)
        {
            return;
        }

        AssetChanged?.Invoke(change);
    }

    public IReadOnlyList<LoadedCookedAssetDiagnostic> GetLoadedCookedAssetDiagnostics()
    {
        lock (m_LoadedCookedAssetGate)
        {
            var diagnostics = new List<LoadedCookedAssetDiagnostic>(m_LoadedCookedAssetSlotsByKey.Count);
            foreach (var slot in m_LoadedCookedAssetSlots)
            {
                if (!slot.IsOccupied)
                {
                    continue;
                }

                diagnostics.Add(new LoadedCookedAssetDiagnostic(
                    slot.Guid,
                    slot.AssetType,
                    slot.Variant,
                    slot.Path,
                    slot.RefCount,
                    slot.Data.LongLength));
            }

            return diagnostics;
        }
    }

    /// <summary>
    /// <summary>
    /// Checks if a file or any of its parent directories should be ignored by the asset pipeline.
    /// Rules: Starts with '.', is a .meta file, or is explicitly hidden by the OS.
    /// </summary>
    private bool IsIgnoredPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        
        // 1. Skip if the file itself is a .meta
        if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;

        var name = Path.GetFileName(path);
        if (name.StartsWith(".")) return true;

        // 2. Check all parent directories up to the root of the scan
        // In a high-performance engine, we might want to cache this or use a more efficient check.
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.StartsWith(".")) return true;
            
            // Optimization: Stop if we reach a known engine root if needed, but for now simple recursion
            dir = Path.GetDirectoryName(dir);
        }

        // 3. Fallback to OS File attributes (Hidden/System)
        try 
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0) return true;
        }
        catch { /* Ignore path access errors */ }

        return false;
    }

    private static bool IsUnderAssetsRoot(string path)
    {
        DirectoryInfo? directory = new DirectoryInfo(path);
        while (directory != null)
        {
            if (string.Equals(directory.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private AssetRecord[] GetSourceAssetsUnder(string directory, string packageId)
    {
        return m_AssetRegistry.Values
            .Where(asset =>
                string.Equals(asset.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                IsSameOrChildPath(asset.SourcePath, directory))
            .ToArray();
    }

    private AssetRecord? GetSourceAsset(Guid guid)
    {
        lock (m_SourceRegistryGate)
        {
            m_AssetRegistry.TryGetValue(guid, out AssetRecord? asset);
            return asset;
        }
    }

    private void RemoveSourceAssets(IEnumerable<AssetRecord> assets)
    {
        foreach (AssetRecord asset in assets)
        {
            m_AssetRegistry.Remove(asset.Guid);
            m_PathRegistry.Remove(Path.GetFullPath(asset.SourcePath));
        }
    }

    private static bool ContainsPathSegment(string path, string segment)
    {
        return Path.GetFullPath(path)
            .Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameOrChildPath(string path, string parent)
    {
        string normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(
                normalizedParent + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string InferAssetType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".hlsl" => "ShaderSource",
            ".shader" => "ShaderSource",
            ".png" => "Texture2D",
            ".jpg" => "Texture2D",
            ".jpeg" => "Texture2D",
            ".ppm" => "Texture2D",
            ".hdr" => "Texture2D",
            ".arienvironment" => "EnvironmentTexture",
            ".arismaterial" => "Material",
            ".material" => "Material",
            ".arismodel" => "Model",
            ".model" => "Model",
            ".arisenscene" => "Scene",
            ".scene" => "Scene",
            ".arisenworld" => "World",
            ".arisrenderpipeline" => "RenderPipelineSettings",
            ".armesh" => "Mesh",
            ".obj" => "Mesh",
            ".gltf" => "Mesh",
            ".glb" => "Mesh",
            ".bin" => "AssetDependency",
            ".fbx" => "Mesh",
            _ => extension.TrimStart('.')
        };
    }

    private static string InferImporter(string filePath, string assetType)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".hlsl" => "HlslShader",
            ".shader" => "ShaderLab",
            ".ppm" => "PpmTextureImporter",
            ".png" or ".jpg" or ".jpeg" => "ImageTextureImporter",
            ".hdr" => "HdrTextureImporter",
            ".arienvironment" => "ArisenEnvironmentTextureImporter",
            ".arismaterial" or ".material" => "ArisenMaterialImporter",
            ".arismodel" or ".model" => "ArisenModelImporter",
            ".arisenscene" or ".scene" => "ArisenSceneImporter",
            ".arisenworld" => "ArisenWorldImporter",
            ".arisrenderpipeline" => "ArisenRenderPipelineSettingsImporter",
            ".armesh" => "ArisenTextMeshImporter",
            ".obj" => "ObjMeshImporter",
            ".gltf" or ".glb" => "GltfMeshImporter",
            ".bin" => "GltfBufferDependency",
            ".fbx" => "FbxMeshImporter",
            _ when !string.IsNullOrWhiteSpace(assetType) => assetType + "Importer",
            _ => "Default"
        };
    }

    private static string MakeCookedKey(Guid guid, string variant)
    {
        return $"{guid:N}:{variant}";
    }

    private static void ValidateCookedIdentity(CookedAssetIdentity identity)
    {
        if (identity.Guid == Guid.Empty ||
            string.IsNullOrWhiteSpace(identity.Variant) ||
            !string.Equals(identity.Variant, identity.Variant.Trim(), StringComparison.Ordinal) ||
            identity.Variant.Any(char.IsControl))
        {
            throw new ArgumentException(
                "[AssetDatabase] Cooked artifact identity requires a non-empty GUID and canonical variant.",
                nameof(identity));
        }
    }

    private static string ValidateCookedCachePath(
        string cookedRoot,
        CookedAssetRecord artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Path) ||
            !Path.IsPathFullyQualified(artifact.Path))
        {
            throw new InvalidOperationException(
                $"[AssetDatabase] Cooked artifact '{artifact.Guid:D}:{artifact.Variant}' has a " +
                "non-absolute cache path.");
        }

        string artifactPath = Path.GetFullPath(artifact.Path);
        string relativePath = Path.GetRelativePath(cookedRoot, artifactPath);
        if (relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException(
                $"[AssetDatabase] Refusing to remove cooked artifact " +
                $"'{artifact.Guid:D}:{artifact.Variant}' outside CookedRoot '{cookedRoot}'.");
        }

        return artifactPath;
    }

    private static bool IsCookedKeyMatch(string key, Guid guid, string? variant)
    {
        string prefix = $"{guid:N}:";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(variant)
            || string.Equals(key.Substring(prefix.Length), variant, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ':' || chars[i] == '/' || chars[i] == '\\')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static string NormalizeCookedExtension(string extension)
    {
        string normalized = string.IsNullOrWhiteSpace(extension)
            ? ".arisenasset"
            : extension.StartsWith('.') ? extension : "." + extension;
        if (normalized.Length < 2 ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains(Path.DirectorySeparatorChar) ||
            normalized.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"[AssetDatabase] Cooked artifact extension '{extension}' is invalid.",
                nameof(extension));
        }

        return normalized;
    }

    private static bool FilesAreByteIdentical(string existingPath, string candidatePath)
    {
        if (!File.Exists(existingPath) || !File.Exists(candidatePath))
        {
            return false;
        }

        const int BufferSize = 64 * 1024;
        byte[] existingBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] candidateBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var existing = new FileStream(
                existingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);
            using var candidate = new FileStream(
                candidatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);
            if (existing.Length != candidate.Length)
            {
                return false;
            }

            while (true)
            {
                int existingRead = existing.Read(existingBuffer, 0, BufferSize);
                int candidateRead = candidate.Read(candidateBuffer, 0, BufferSize);
                if (existingRead != candidateRead)
                {
                    return false;
                }

                if (existingRead == 0)
                {
                    return true;
                }

                if (!existingBuffer.AsSpan(0, existingRead).SequenceEqual(
                        candidateBuffer.AsSpan(0, candidateRead)))
                {
                    return false;
                }
            }
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(existingBuffer);
            ArrayPool<byte>.Shared.Return(candidateBuffer);
        }
    }

    private void ValidateCookedArtifactWrite(CookedArtifactWrite write)
    {
        ValidateCookedArtifactWriteIdentity(write);
        string currentRoot = string.IsNullOrWhiteSpace(CookedRoot)
            ? string.Empty
            : Path.GetFullPath(CookedRoot);
        if (!string.Equals(currentRoot, write.CookedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "[AssetDatabase] Cooked artifact write belongs to a previous database root.");
        }
    }

    private static void ValidateCookedArtifactWriteIdentity(CookedArtifactWrite write)
    {
        string expectedPath = Path.GetFullPath(Path.Combine(
            write.CookedRoot,
            ".staging",
            write.TransactionId.ToString("N"),
            "artifact" + write.Extension));
        if (!string.Equals(
                expectedPath,
                Path.GetFullPath(write.OutputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "[AssetDatabase] Cooked artifact staging path does not match its transaction identity.");
        }
    }

    private void EnsureMutable(string operation)
    {
        if (IsReadOnlyRuntime)
        {
            throw new InvalidOperationException(
                $"[AssetDatabase] Cannot {operation} while a read-only runtime catalog is mounted. " +
                "Production content must be cooked and deployed before launch.");
        }
    }

    private int AllocateLoadedCookedAssetSlot()
    {
        if (m_FreeLoadedCookedAssetSlots.Count > 0)
        {
            return m_FreeLoadedCookedAssetSlots.Pop();
        }

        var slot = new LoadedCookedAssetSlot { Generation = 1 };
        m_LoadedCookedAssetSlots.Add(slot);
        return m_LoadedCookedAssetSlots.Count - 1;
    }

    private CookedRegistrySnapshot ReadCookedRegistrySnapshot()
    {
        return Volatile.Read(ref m_CookedRegistrySnapshot);
    }

    private static Dictionary<string, CookedAssetRecord> CloneCookedArtifacts(
        CookedRegistrySnapshot snapshot)
    {
        var clone = new Dictionary<string, CookedAssetRecord>(
            snapshot.Artifacts.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, CookedAssetRecord artifact) in snapshot.Artifacts)
        {
            clone.Add(key, artifact);
        }

        return clone;
    }

    private static CookedRegistrySnapshot CreateCookedRegistrySnapshot(
        long generation,
        IReadOnlyDictionary<string, CookedAssetRecord> artifacts)
    {
        FrozenDictionary<string, CookedAssetRecord> frozen = artifacts.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return new CookedRegistrySnapshot(generation, frozen);
    }

    private static long NextCookedRegistryGeneration(long generation)
    {
        return generation == long.MaxValue ? 1 : generation + 1;
    }

    private void EnsureExpectedCookedRegistrySnapshot(CookedRegistrySnapshot expected)
    {
        if (!ReferenceEquals(expected, ReadCookedRegistrySnapshot()))
        {
            throw new InvalidOperationException(
                "[AssetDatabase] Cooked registry changed outside its serialized writer transaction.");
        }
    }

    private void ReplaceCookedRegistryWithoutManifest(
        IReadOnlyDictionary<string, CookedAssetRecord> artifacts,
        long persistedGeneration)
    {
        CookedRegistrySnapshot previous = ReadCookedRegistrySnapshot();
        long generation = System.Math.Max(
            NextCookedRegistryGeneration(previous.Generation),
            System.Math.Max(0, persistedGeneration));
        CookedRegistrySnapshot replacement = CreateCookedRegistrySnapshot(generation, artifacts);
        lock (m_LoadedCookedAssetGate)
        {
            ReleaseAllLoadedCookedAssetsCore();
            Volatile.Write(ref m_CookedRegistrySnapshot, replacement);
        }
    }

    private bool SupersedeLoadedCookedAssetCore(string key)
    {
        if (!m_LoadedCookedAssetSlotsByKey.ContainsKey(key))
        {
            return false;
        }

        m_LoadedCookedAssetSlotsByKey.Remove(key);
        return true;
    }

    private bool TryGetLoadedCookedAssetSlot(CookedAssetHandle handle, out LoadedCookedAssetSlot slot)
    {
        slot = null!;
        if (!handle.IsValid
            || handle.Index < 0
            || handle.Index >= m_LoadedCookedAssetSlots.Count)
        {
            return false;
        }

        var candidate = m_LoadedCookedAssetSlots[handle.Index];
        if (!candidate.IsOccupied
            || candidate.Generation != handle.Generation
            || candidate.Guid != handle.Guid
            || !string.Equals(candidate.Variant, handle.Variant, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        slot = candidate;
        return true;
    }

    private int ReleaseLoadedCookedAssetsCore(Guid guid, string? variant)
    {
        int releasedCount = 0;
        m_FreeLoadedCookedAssetSlots.EnsureCapacity(
            m_FreeLoadedCookedAssetSlots.Count + m_LoadedCookedAssetSlots.Count);

        for (int i = 0; i < m_LoadedCookedAssetSlots.Count; i++)
        {
            LoadedCookedAssetSlot slot = m_LoadedCookedAssetSlots[i];
            if (!slot.IsOccupied || slot.Guid != guid)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(variant) &&
                !string.Equals(slot.Variant, variant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            m_LoadedCookedAssetSlotsByKey.Remove(slot.Key);
            slot.Reset();
            m_FreeLoadedCookedAssetSlots.Push(i);
            releasedCount++;
        }

        return releasedCount;
    }

    private void ReleaseAllLoadedCookedAssetsCore()
    {
        m_FreeLoadedCookedAssetSlots.Clear();
        m_FreeLoadedCookedAssetSlots.EnsureCapacity(m_LoadedCookedAssetSlots.Count);
        for (int i = 0; i < m_LoadedCookedAssetSlots.Count; i++)
        {
            if (m_LoadedCookedAssetSlots[i].IsOccupied)
            {
                m_LoadedCookedAssetSlots[i].Reset();
            }
        }

        m_LoadedCookedAssetSlotsByKey.Clear();
        for (int i = m_LoadedCookedAssetSlots.Count - 1; i >= 0; i--)
        {
            m_FreeLoadedCookedAssetSlots.Push(i);
        }
    }

    private CookedManifestLoadResult LoadCookedManifest(string manifestPath)
    {
        var artifacts = new Dictionary<string, CookedAssetRecord>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return new CookedManifestLoadResult(0, artifacts);
        }

        try
        {
            CookedAssetManifest? manifest = JsonSerializer.Deserialize<CookedAssetManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest?.Artifacts == null)
            {
                return new CookedManifestLoadResult(0, artifacts);
            }

            foreach (CookedAssetRecord artifact in manifest.Artifacts)
            {
                if (artifact.Guid == Guid.Empty || string.IsNullOrWhiteSpace(artifact.Variant))
                {
                    continue;
                }

                artifacts[MakeCookedKey(artifact.Guid, artifact.Variant)] = artifact;
            }

            return new CookedManifestLoadResult(System.Math.Max(0, manifest.Generation), artifacts);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[AssetDatabase] Failed to read cooked manifest '{manifestPath}': {ex.Message}");
            return new CookedManifestLoadResult(0, artifacts);
        }
    }

    private void WriteCookedManifest(
        CookedRegistrySnapshot previous,
        CookedRegistrySnapshot candidate)
    {
        if (string.IsNullOrWhiteSpace(m_CookedManifestPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(m_CookedManifestPath)!);
        var manifest = new CookedAssetManifest
        {
            Generation = candidate.Generation,
            Artifacts = candidate.Artifacts.Values
                .OrderBy(artifact => artifact.Guid)
                .ThenBy(artifact => artifact.Variant, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        string temporaryPath = m_CookedManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true }));
            BeforeCookedManifestReplace?.Invoke(new CookedManifestCommit(
                previous.Generation,
                candidate.Generation,
                m_CookedManifestPath,
                candidate.Artifacts.Count));

            if (File.Exists(m_CookedManifestPath))
            {
                File.Replace(
                    temporaryPath,
                    m_CookedManifestPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, m_CookedManifestPath);
            }
        }
        catch (Exception commitError)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"[AssetDatabase] Cooked manifest generation {candidate.Generation} failed " +
                    "and its temporary file could not be removed.",
                    new AggregateException(commitError, cleanupError));
            }

            throw new InvalidOperationException(
                $"[AssetDatabase] Failed to publish cooked manifest generation " +
                $"{candidate.Generation} at '{m_CookedManifestPath}'.",
                commitError);
        }
    }

    private static void QuarantineCommittedCookedArtifacts(
        string cookedRoot,
        IReadOnlyList<CookedArtifactRemoval> removals)
    {
        string transactionRoot = Path.Combine(
            cookedRoot,
            ".remove",
            Guid.NewGuid().ToString("N"));
        foreach (CookedArtifactRemoval removal in removals)
        {
            if (!File.Exists(removal.OriginalPath))
            {
                continue;
            }

            try
            {
                string quarantinePath = Path.Combine(transactionRoot, removal.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
                File.Move(removal.OriginalPath, quarantinePath);
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    $"[AssetDatabase] Registry removal committed, but cache artifact " +
                    $"'{removal.OriginalPath}' could not be quarantined: {ex.Message}");
            }
        }

        TryDeleteDirectory(transactionRoot);
        TryDeleteEmptyDirectory(Path.GetDirectoryName(transactionRoot), cookedRoot);
        foreach (CookedArtifactRemoval removal in removals)
        {
            TryDeleteEmptyDirectory(Path.GetDirectoryName(removal.OriginalPath), cookedRoot);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[AssetDatabase] Failed to delete cache transaction directory '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteEmptyDirectory(string? path, string cookedRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, cookedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string relativePath = Path.GetRelativePath(cookedRoot, fullPath);
        if (relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relativePath))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                Directory.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[AssetDatabase] Failed to prune empty cache directory '{fullPath}': {ex.Message}");
        }
    }

    private sealed class CookedAssetManifest
    {
        public long Generation { get; set; }

        public List<CookedAssetRecord> Artifacts { get; set; } = new();
    }

    private sealed record CookedManifestLoadResult(
        long Generation,
        IReadOnlyDictionary<string, CookedAssetRecord> Artifacts);

    private sealed record CookedRegistrySnapshot(
        long Generation,
        FrozenDictionary<string, CookedAssetRecord> Artifacts)
    {
        public static CookedRegistrySnapshot Empty { get; } = new(
            0,
            FrozenDictionary<string, CookedAssetRecord>.Empty);
    }

    private sealed record CookedArtifactRemoval(
        string RegistryKey,
        CookedAssetRecord Artifact,
        string OriginalPath,
        string RelativePath);

    private sealed class LoadedCookedAssetSlot
    {
        public int Generation { get; set; }
        public bool IsOccupied { get; set; }
        public string Key { get; set; } = string.Empty;
        public Guid Guid { get; set; }
        public string AssetType { get; set; } = string.Empty;
        public string Variant { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int RefCount { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }

        public void Reset()
        {
            Generation = Generation == int.MaxValue ? 1 : Generation + 1;
            IsOccupied = false;
            Key = string.Empty;
            Guid = Guid.Empty;
            AssetType = string.Empty;
            Variant = string.Empty;
            Path = string.Empty;
            Data = Array.Empty<byte>();
            RefCount = 0;
            LastWriteTimeUtc = default;
        }
    }
}

internal readonly record struct CookedManifestCommit(
    long PreviousGeneration,
    long CandidateGeneration,
    string ManifestPath,
    int ArtifactCount);
