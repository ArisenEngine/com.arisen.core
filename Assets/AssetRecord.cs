namespace ArisenEngine.Core.Assets;

public sealed record AssetRecord(
    Guid Guid,
    string AssetType,
    string SourcePath,
    string MetaPath,
    string PackageId);

public sealed record CookedAssetRecord(
    Guid Guid,
    string AssetType,
    string Variant,
    string Path,
    long SizeInBytes,
    DateTime LastWriteTimeUtc);

public readonly record struct CookedAssetHandle(
    int Index,
    int Generation,
    Guid Guid,
    string Variant)
{
    public static CookedAssetHandle Invalid { get; } = new(-1, 0, Guid.Empty, string.Empty);

    public bool IsValid => Index >= 0 && Generation > 0 && Guid != Guid.Empty;
}

public sealed record LoadedCookedAssetDiagnostic(
    Guid Guid,
    string AssetType,
    string Variant,
    string Path,
    int RefCount,
    long SizeInBytes);

public enum AssetChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
    CookedInvalidated
}

public readonly record struct AssetChangeEvent(
    AssetChangeKind Kind,
    Guid Guid,
    string AssetType,
    string SourcePath,
    string PreviousSourcePath,
    string PackageId);

public interface IAssetDatabase
{
    string CookedRoot { get; }

    IReadOnlyCollection<AssetRecord> Assets { get; }

    event Action<AssetChangeEvent>? AssetChanged;

    bool TryGetAsset(Guid guid, out AssetRecord asset);

    bool TryGetCookedArtifact(Guid guid, string variant, out CookedAssetRecord artifact);

    string GetCookedArtifactPath(Guid guid, string variant, string extension);

    void RegisterCookedArtifact(CookedAssetRecord artifact);

    bool TryLoadCookedAsset(Guid guid, string variant, string expectedAssetType, out CookedAssetHandle handle);

    bool TryGetCookedAssetBytes(CookedAssetHandle handle, out ReadOnlyMemory<byte> bytes);

    ReadOnlyMemory<byte> GetCookedAssetBytes(CookedAssetHandle handle);

    void Release(CookedAssetHandle handle);

    void ReleaseAllLoadedCookedAssets();

    int InvalidateCookedAssets(Guid guid, string? variant = null);

    void NotifyAssetChanged(AssetChangeEvent change);

    IReadOnlyList<LoadedCookedAssetDiagnostic> GetLoadedCookedAssetDiagnostics();
}
