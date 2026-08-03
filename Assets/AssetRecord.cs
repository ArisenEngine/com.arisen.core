namespace ArisenEngine.Core.Assets;

public sealed record AssetRecord(
    Guid Guid,
    string AssetType,
    string SourcePath,
    string MetaPath,
    string PackageId);

public readonly record struct AssetDescriptor(
    Guid Guid,
    string AssetType,
    string PackageId);

public enum AssetDatabaseMode
{
    Uninitialized,
    Workspace,
    ReadOnlyRuntime
}

public enum AssetSourceAccessMode
{
    Disabled,
    EditorAuthoring,
    Diagnostic,
    RuntimeAssetCook
}

public sealed record CookedAssetRecord(
    Guid Guid,
    string AssetType,
    string Variant,
    string Path,
    long SizeInBytes,
    DateTime LastWriteTimeUtc);

internal interface ICookedArtifactWriteOwner
{
    CookedAssetRecord CommitCookedArtifactWrite(
        CookedArtifactWrite write,
        string assetType);

    void DiscardCookedArtifactWrite(CookedArtifactWrite write);
}

/// <summary>
/// Owns one unpublished cooked artifact until it is atomically committed to the registry.
/// </summary>
public sealed class CookedArtifactWrite : IDisposable
{
    private readonly object m_Gate = new();
    private ICookedArtifactWriteOwner? m_Owner;

    internal CookedArtifactWrite(
        ICookedArtifactWriteOwner owner,
        Guid transactionId,
        Guid guid,
        string variant,
        string extension,
        string cookedRoot,
        string outputPath)
    {
        m_Owner = owner;
        TransactionId = transactionId;
        Guid = guid;
        Variant = variant;
        Extension = extension;
        CookedRoot = cookedRoot;
        OutputPath = outputPath;
    }

    public Guid Guid { get; }

    public string Variant { get; }

    public string OutputPath { get; }

    internal Guid TransactionId { get; }

    internal string Extension { get; }

    internal string CookedRoot { get; }

    public CookedAssetRecord Commit(string assetType)
    {
        lock (m_Gate)
        {
            ICookedArtifactWriteOwner owner = m_Owner ??
                throw new ObjectDisposedException(
                    nameof(CookedArtifactWrite),
                    "The cooked artifact write has already been committed or discarded.");
            CookedAssetRecord artifact = owner.CommitCookedArtifactWrite(this, assetType);
            m_Owner = null;
            return artifact;
        }
    }

    public void Dispose()
    {
        lock (m_Gate)
        {
            if (m_Owner == null)
            {
                return;
            }

            m_Owner.DiscardCookedArtifactWrite(this);
            m_Owner = null;
        }
    }
}

public readonly record struct CookedAssetIdentity(
    Guid Guid,
    string Variant);

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
    AssetDatabaseMode Mode { get; }

    bool IsReadOnlyRuntime { get; }

    AssetSourceAccessMode SourceAccessMode { get; }

    bool CanReadSourceAssets { get; }

    string CookedRoot { get; }

    IReadOnlyCollection<AssetRecord> Assets { get; }

    event Action<AssetChangeEvent>? AssetChanged;

    bool TryGetAsset(Guid guid, out AssetRecord asset);

    bool TryGetAssetDescriptor(Guid guid, out AssetDescriptor asset);

    bool TryGetCookedArtifact(Guid guid, string variant, out CookedAssetRecord artifact);

    CookedArtifactWrite BeginCookedArtifactWrite(
        Guid guid,
        string variant,
        string extension);

    bool TryLoadCookedAsset(Guid guid, string variant, string expectedAssetType, out CookedAssetHandle handle);

    bool TryGetCookedAssetBytes(CookedAssetHandle handle, out ReadOnlyMemory<byte> bytes);

    ReadOnlyMemory<byte> GetCookedAssetBytes(CookedAssetHandle handle);

    void Release(CookedAssetHandle handle);

    void ReleaseAllLoadedCookedAssets();

    int InvalidateCookedAssets(Guid guid, string? variant = null);

    int RemoveCookedArtifacts(IReadOnlyCollection<CookedAssetIdentity> identities);

    void NotifyAssetChanged(AssetChangeEvent change);

    IReadOnlyList<LoadedCookedAssetDiagnostic> GetLoadedCookedAssetDiagnostics();
}

/// <summary>
/// Editor/setup-only source indexing boundary. Runtime asset consumers should use
/// <see cref="IAssetDatabase"/> and must not rescan source directories.
/// </summary>
public interface IAssetSourceIndex
{
    void RefreshSourceDirectory(string directoryPath, string packageId);
}
