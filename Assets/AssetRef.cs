namespace ArisenEngine.Core.Assets;

public readonly record struct AssetRef<TAsset>(
    Guid Guid,
    string AssetType = "",
    string PackageId = "")
{
    public bool IsValid => Guid != Guid.Empty;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(AssetType)
            ? Guid.ToString("D")
            : $"{AssetType}:{Guid:D}";
    }

    public static implicit operator Guid(AssetRef<TAsset> assetRef)
    {
        return assetRef.Guid;
    }
}

public sealed class ShaderSourceAsset
{
    private ShaderSourceAsset() { }
}

public sealed class Texture2DSourceAsset
{
    private Texture2DSourceAsset() { }
}

public sealed class MeshSourceAsset
{
    private MeshSourceAsset() { }
}

public sealed class ModelSourceAsset
{
    private ModelSourceAsset() { }
}

public sealed class MaterialSourceAsset
{
    private MaterialSourceAsset() { }
}

public sealed class SceneSourceAsset
{
    private SceneSourceAsset() { }
}

public static class AssetDatabaseExtensions
{
    public static bool TryGetAsset<TAsset>(
        this IAssetDatabase assetDatabase,
        AssetRef<TAsset> assetRef,
        out AssetRecord asset)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetRef.IsValid ||
            !assetDatabase.TryGetAsset(assetRef.Guid, out asset))
        {
            asset = null!;
            return false;
        }

        return string.IsNullOrWhiteSpace(assetRef.AssetType) ||
               string.Equals(asset.AssetType, assetRef.AssetType, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryLoadCookedAsset<TAsset>(
        this IAssetDatabase assetDatabase,
        AssetRef<TAsset> assetRef,
        string variant,
        out CookedAssetHandle handle)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetRef.IsValid || string.IsNullOrWhiteSpace(assetRef.AssetType))
        {
            handle = CookedAssetHandle.Invalid;
            return false;
        }

        return assetDatabase.TryLoadCookedAsset(assetRef.Guid, variant, assetRef.AssetType, out handle);
    }
}
