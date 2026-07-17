using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Core.Assets;

/// <summary>
/// A centralized registry that indexes discovered assets in the project directory,
/// matching files to their serialized .meta files and GUIDs.
/// </summary>
public class AssetDatabase : IAssetDatabase
{
    private static AssetDatabase? s_Instance;
    public static AssetDatabase Instance => s_Instance ??= new AssetDatabase();

    private readonly Dictionary<Guid, AssetRecord> m_AssetRegistry = new();
    private readonly Dictionary<string, Guid> m_PathRegistry = new();
    private readonly Dictionary<string, CookedAssetRecord> m_CookedRegistry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> m_LoadedCookedAssetSlotsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoadedCookedAssetSlot> m_LoadedCookedAssetSlots = new();
    private readonly Stack<int> m_FreeLoadedCookedAssetSlots = new();
    private string m_CookedManifestPath = string.Empty;

    public string CookedRoot { get; private set; } = string.Empty;

    public IReadOnlyCollection<AssetRecord> Assets => m_AssetRegistry.Values;

    public event Action<AssetChangeEvent>? AssetChanged;

    /// <summary>
    /// Scans the given project directory to index all assets and automatically provisions missing .meta files.
    /// </summary>
    public void Initialize(string projectContentPath)
    {
        m_AssetRegistry.Clear();
        m_PathRegistry.Clear();
        ReleaseAllLoadedCookedAssets();

        if (!Directory.Exists(projectContentPath))
            return;

        CookedRoot = Path.Combine(projectContentPath, ".arisen", "Cache", "CookedAssets");
        Directory.CreateDirectory(CookedRoot);
        m_CookedManifestPath = Path.Combine(CookedRoot, "AssetManifest.json");
        LoadCookedManifest();

        RefreshDirectory(projectContentPath);
    }

    public void InitializeWorkspace(string workspaceRoot, IEnumerable<(string PackageId, string PackageRoot)> packages)
    {
        m_AssetRegistry.Clear();
        m_PathRegistry.Clear();
        m_CookedRegistry.Clear();
        ReleaseAllLoadedCookedAssets();

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            workspaceRoot = Directory.GetCurrentDirectory();
        }

        CookedRoot = Path.Combine(workspaceRoot, ".arisen", "Cache", "CookedAssets");
        Directory.CreateDirectory(CookedRoot);
        m_CookedManifestPath = Path.Combine(CookedRoot, "AssetManifest.json");
        LoadCookedManifest();

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

            var assetsRoot = Path.Combine(packageRoot, "Assets");
            if (!Directory.Exists(assetsRoot))
            {
                continue;
            }

            RefreshDirectory(assetsRoot, string.IsNullOrWhiteSpace(packageId) ? Path.GetFileName(packageRoot) : packageId);
        }

        Logger.Info($"[AssetDatabase] Indexed {m_AssetRegistry.Count} asset(s). CookedRoot: {CookedRoot}");
    }

    /// <summary>
    /// Refreshes the indexing of a directory recursively, creating .meta files for any asset without one.
    /// </summary>
    public void RefreshDirectory(string directoryPath)
    {
        RefreshDirectory(directoryPath, "workspace");
    }

    public void RefreshDirectory(string directoryPath, string packageId)
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

    /// <summary>
    /// Looks up the absolute file path for a registered asset reference.
    /// </summary>
    public string? GetAssetPath(Guid guid)
    {
        return m_AssetRegistry.TryGetValue(guid, out var asset) ? asset.SourcePath : null;
    }

    public bool TryGetAsset(Guid guid, out AssetRecord asset)
    {
        return m_AssetRegistry.TryGetValue(guid, out asset!);
    }

    public bool TryGetCookedArtifact(Guid guid, string variant, out CookedAssetRecord artifact)
    {
        return m_CookedRegistry.TryGetValue(MakeCookedKey(guid, variant), out artifact!);
    }

    public string GetCookedArtifactPath(Guid guid, string variant, string extension)
    {
        if (string.IsNullOrWhiteSpace(CookedRoot))
        {
            throw new InvalidOperationException("[AssetDatabase] CookedRoot is not initialized.");
        }

        var safeVariant = SanitizeFileName(variant);
        var safeExtension = string.IsNullOrWhiteSpace(extension)
            ? ".arisenasset"
            : extension.StartsWith('.') ? extension : "." + extension;

        var directory = Path.Combine(CookedRoot, guid.ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, safeVariant + safeExtension);
    }

    public void RegisterCookedArtifact(CookedAssetRecord artifact)
    {
        m_CookedRegistry[MakeCookedKey(artifact.Guid, artifact.Variant)] = artifact;
        SaveCookedManifest();
    }

    public bool TryLoadCookedAsset(Guid guid, string variant, string expectedAssetType, out CookedAssetHandle handle)
    {
        handle = CookedAssetHandle.Invalid;

        if (!TryGetAsset(guid, out var asset))
        {
            Logger.Warning($"[AssetDatabase] Cannot load cooked asset '{guid}': source asset is not indexed.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedAssetType)
            && !string.Equals(asset.AssetType, expectedAssetType, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warning(
                $"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': expected '{expectedAssetType}', found '{asset.AssetType}'.");
            return false;
        }

        if (!TryGetCookedArtifact(guid, variant, out var artifact))
        {
            Logger.Warning($"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': cooked artifact is not registered.");
            return false;
        }

        if (!File.Exists(artifact.Path))
        {
            Logger.Warning($"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': missing file '{artifact.Path}'.");
            return false;
        }

        var key = MakeCookedKey(guid, variant);
        if (m_LoadedCookedAssetSlotsByKey.TryGetValue(key, out var existingIndex))
        {
            var existingSlot = m_LoadedCookedAssetSlots[existingIndex];
            existingSlot.RefCount++;
            handle = new CookedAssetHandle(existingIndex, existingSlot.Generation, existingSlot.Guid, existingSlot.Variant);
            return true;
        }

        var bytes = File.ReadAllBytes(artifact.Path);
        if (bytes.Length == 0)
        {
            Logger.Warning($"[AssetDatabase] Cannot load cooked asset '{guid}' variant '{variant}': file is empty.");
            return false;
        }

        var slotIndex = AllocateLoadedCookedAssetSlot();
        var slot = m_LoadedCookedAssetSlots[slotIndex];
        slot.IsOccupied = true;
        slot.Key = key;
        slot.Guid = guid;
        slot.AssetType = asset.AssetType;
        slot.Variant = variant;
        slot.Path = Path.GetFullPath(artifact.Path);
        slot.Data = bytes;
        slot.RefCount = 1;
        slot.LastWriteTimeUtc = File.GetLastWriteTimeUtc(artifact.Path);
        m_LoadedCookedAssetSlotsByKey[key] = slotIndex;

        handle = new CookedAssetHandle(slotIndex, slot.Generation, slot.Guid, slot.Variant);
        Logger.Info(
            $"[AssetDatabase] Loaded cooked asset {guid} | Variant: {variant} | Size: {bytes.Length} bytes | Handle: {handle.Index}:{handle.Generation}");
        return true;
    }

    public bool TryGetCookedAssetBytes(CookedAssetHandle handle, out ReadOnlyMemory<byte> bytes)
    {
        bytes = ReadOnlyMemory<byte>.Empty;
        if (!TryGetLoadedCookedAssetSlot(handle, out var slot))
        {
            return false;
        }

        bytes = slot.Data;
        return true;
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

        m_LoadedCookedAssetSlotsByKey.Remove(slot.Key);
        slot.Reset();
        m_FreeLoadedCookedAssetSlots.Push(handle.Index);
    }

    public void ReleaseAllLoadedCookedAssets()
    {
        for (int i = 0; i < m_LoadedCookedAssetSlots.Count; i++)
        {
            if (m_LoadedCookedAssetSlots[i].IsOccupied)
            {
                m_LoadedCookedAssetSlots[i].Reset();
            }
        }

        m_LoadedCookedAssetSlotsByKey.Clear();
        m_FreeLoadedCookedAssetSlots.Clear();
        for (int i = m_LoadedCookedAssetSlots.Count - 1; i >= 0; i--)
        {
            m_FreeLoadedCookedAssetSlots.Push(i);
        }
    }

    public int InvalidateCookedAssets(Guid guid, string? variant = null)
    {
        if (guid == Guid.Empty)
        {
            return 0;
        }

        int releasedCount = ReleaseLoadedCookedAssets(guid, variant);

        var registryKeys = m_CookedRegistry.Keys
            .Where(key => IsCookedKeyMatch(key, guid, variant))
            .ToArray();

        foreach (string key in registryKeys)
        {
            m_CookedRegistry.Remove(key);
        }

        if (registryKeys.Length > 0)
        {
            SaveCookedManifest();
        }

        if (releasedCount > 0 || registryKeys.Length > 0)
        {
            m_AssetRegistry.TryGetValue(guid, out var asset);
            NotifyAssetChanged(new AssetChangeEvent(
                AssetChangeKind.CookedInvalidated,
                guid,
                asset?.AssetType ?? string.Empty,
                asset?.SourcePath ?? string.Empty,
                string.Empty,
                asset?.PackageId ?? string.Empty));

            Logger.Info(
                $"[AssetDatabase] Invalidated cooked asset {guid} | Variant: {variant ?? "<all>"} | LoadedReleased: {releasedCount} | RegistryRemoved: {registryKeys.Length}");
        }

        return releasedCount;
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

    private int ReleaseLoadedCookedAssets(Guid guid, string? variant)
    {
        int releasedCount = 0;

        for (int i = 0; i < m_LoadedCookedAssetSlots.Count; i++)
        {
            var slot = m_LoadedCookedAssetSlots[i];
            if (!slot.IsOccupied || slot.Guid != guid)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(variant)
                && !string.Equals(slot.Variant, variant, StringComparison.OrdinalIgnoreCase))
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

    private void LoadCookedManifest()
    {
        if (string.IsNullOrWhiteSpace(m_CookedManifestPath) || !File.Exists(m_CookedManifestPath))
        {
            return;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<CookedAssetManifest>(
                File.ReadAllText(m_CookedManifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest?.Artifacts == null)
            {
                return;
            }

            foreach (var artifact in manifest.Artifacts)
            {
                if (artifact.Guid == Guid.Empty || string.IsNullOrWhiteSpace(artifact.Variant))
                {
                    continue;
                }

                m_CookedRegistry[MakeCookedKey(artifact.Guid, artifact.Variant)] = artifact;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[AssetDatabase] Failed to read cooked manifest '{m_CookedManifestPath}': {ex.Message}");
        }
    }

    private void SaveCookedManifest()
    {
        if (string.IsNullOrWhiteSpace(m_CookedManifestPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m_CookedManifestPath)!);
            var manifest = new CookedAssetManifest
            {
                Artifacts = m_CookedRegistry.Values
                    .OrderBy(x => x.Guid)
                    .ThenBy(x => x.Variant, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            File.WriteAllText(
                m_CookedManifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[AssetDatabase] Failed to write cooked manifest '{m_CookedManifestPath}': {ex.Message}");
        }
    }

    private sealed class CookedAssetManifest
    {
        public List<CookedAssetRecord> Artifacts { get; set; } = new();
    }

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
