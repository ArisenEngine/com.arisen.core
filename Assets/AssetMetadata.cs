using System;
using ArisenEngine.Core.Serialization;
using YamlDotNet.Serialization;

namespace ArisenEngine.Core.Assets;

/// <summary>
/// Serializable metadata associated with project assets to track stable references across files.
/// Typically serialized to a .meta file alongside the main file.
/// </summary>
public class AssetMetadata : ISerializationCallbackReceiver
{
    /// <summary>
    /// A globally unique identifier used by the AssetDatabase to safely reference this asset.
    /// </summary>
    public Guid Guid { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The detected file extension or generic asset type at the time of import.
    /// </summary>
    public string AssetType { get; set; } = string.Empty;

    /// <summary>
    /// Optional importer identifier. Empty keeps legacy metadata valid.
    /// </summary>
    public string Importer { get; set; } = string.Empty;

    /// <summary>
    /// Optional provenance for generated/imported child assets derived from another source asset.
    /// </summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public GeneratedAssetMetadata? Generated { get; set; }

    public void OnAfterDeserialize()
    {
    }

    public void OnBeforeSerialize()
    {
    }
}

public sealed class GeneratedAssetMetadata
{
    public Guid SourceGuid { get; set; }

    public string SourcePackageId { get; set; } = string.Empty;

    public string ChildKind { get; set; } = string.Empty;

    public string ChildKey { get; set; } = string.Empty;

    public string GeneratedByImporter { get; set; } = string.Empty;
}
