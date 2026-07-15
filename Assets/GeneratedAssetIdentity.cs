using System.Security.Cryptography;
using System.Text;

namespace ArisenEngine.Core.Assets;

public static class GeneratedAssetIdentity
{
    private const string IdentityVersion = "arisen.generated-asset-child.v1";

    public static Guid CreateChildGuid(
        Guid sourceGuid,
        string packageId,
        string childKind,
        string childKey)
    {
        if (sourceGuid == Guid.Empty)
        {
            throw new ArgumentException("Generated child assets require a stable source asset GUID.", nameof(sourceGuid));
        }

        var normalizedPackageId = NormalizeRequired(packageId, nameof(packageId), toLowerInvariant: true);
        var normalizedChildKind = NormalizeRequired(childKind, nameof(childKind), toLowerInvariant: true);
        var normalizedChildKey = NormalizeRequired(childKey, nameof(childKey), toLowerInvariant: false);
        var identity = string.Join(
            '\n',
            IdentityVersion,
            sourceGuid.ToString("N"),
            normalizedPackageId,
            normalizedChildKind,
            normalizedChildKey);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(identity), hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    public static AssetMetadata CreateChildMetadata(
        Guid sourceGuid,
        string packageId,
        string childKind,
        string childKey,
        string assetType,
        string importer)
    {
        var normalizedAssetType = NormalizeRequired(assetType, nameof(assetType), toLowerInvariant: false);
        var normalizedImporter = NormalizeRequired(importer, nameof(importer), toLowerInvariant: false);

        return new AssetMetadata
        {
            Guid = CreateChildGuid(sourceGuid, packageId, childKind, childKey),
            AssetType = normalizedAssetType,
            Importer = normalizedImporter,
            Generated = new GeneratedAssetMetadata
            {
                SourceGuid = sourceGuid,
                SourcePackageId = NormalizeRequired(packageId, nameof(packageId), toLowerInvariant: true),
                ChildKind = NormalizeRequired(childKind, nameof(childKind), toLowerInvariant: true),
                ChildKey = NormalizeRequired(childKey, nameof(childKey), toLowerInvariant: false),
                GeneratedByImporter = normalizedImporter
            }
        };
    }

    private static string NormalizeRequired(string value, string parameterName, bool toLowerInvariant)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        var normalized = value.Trim().Replace('\\', '/');
        return toLowerInvariant ? normalized.ToLowerInvariant() : normalized;
    }
}
