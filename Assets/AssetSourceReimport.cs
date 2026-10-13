using System;
using System.Collections.Generic;

namespace ArisenEngine.Core.Assets;

/// <summary>
/// Reimports every generated child of the source assets it owns. Generated identity is derived
/// from the source GUID, the owning package id, the child kind, and the child key, so an asset
/// whose owning package changes has to be reimported for its children to be re-keyed. The
/// importer writes both the children and the references to them, which is why this is a
/// producer-side operation and not a text rewrite.
/// </summary>
public interface IAssetSourceReimporter
{
    /// <summary>Importer id recorded in <see cref="AssetMetadata.Importer"/> for its outputs.</summary>
    string ImporterId { get; }

    /// <summary>
    /// Asset type of the sources this importer owns, matched against
    /// <see cref="AssetRecord.AssetType"/>.
    /// </summary>
    string SourceAssetType { get; }

    /// <summary>
    /// Reimports every source in <paramref name="sources"/> and returns the number of source assets
    /// that were reimported. Implementations must be deterministic: reimporting an unchanged source
    /// has to reproduce the bytes already on disk.
    /// </summary>
    int ReimportAll(IAssetDatabase database, IReadOnlyList<AssetRecord> sources);
}

/// <summary>
/// Importers that can reimport a source asset in place. Packages register their own, so a
/// build-stage command can re-key content without loading editor UI.
/// </summary>
public interface IAssetSourceReimporterRegistry
{
    void RegisterReimporter(IAssetSourceReimporter reimporter);

    IReadOnlyList<IAssetSourceReimporter> Reimporters { get; }
}

public sealed class AssetSourceReimporterRegistry : IAssetSourceReimporterRegistry
{
    private readonly List<IAssetSourceReimporter> m_Reimporters = new();

    public IReadOnlyList<IAssetSourceReimporter> Reimporters => m_Reimporters;

    public void RegisterReimporter(IAssetSourceReimporter reimporter)
    {
        ArgumentNullException.ThrowIfNull(reimporter);
        if (string.IsNullOrWhiteSpace(reimporter.ImporterId))
        {
            throw new InvalidOperationException(
                "A source reimporter must declare a non-empty importer id.");
        }

        if (string.IsNullOrWhiteSpace(reimporter.SourceAssetType))
        {
            throw new InvalidOperationException(
                $"Source reimporter '{reimporter.ImporterId}' must declare a non-empty source asset type.");
        }

        for (int index = 0; index < m_Reimporters.Count; index++)
        {
            IAssetSourceReimporter existing = m_Reimporters[index];
            if (string.Equals(existing.ImporterId, reimporter.ImporterId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Source reimporter '{reimporter.ImporterId}' is already registered by " +
                    $"'{existing.GetType().FullName}'.");
            }

            if (string.Equals(
                    existing.SourceAssetType,
                    reimporter.SourceAssetType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Source asset type '{reimporter.SourceAssetType}' is already owned by reimporter " +
                    $"'{existing.ImporterId}'.");
            }
        }

        m_Reimporters.Add(reimporter);
    }
}
