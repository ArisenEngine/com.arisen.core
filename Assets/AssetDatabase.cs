using System;
using System.Collections.Generic;
using System.IO;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Core.Assets;

/// <summary>
/// A centralized registry that indexes discovered assets in the project directory,
/// matching files to their serialized .meta files and GUIDs.
/// </summary>
public class AssetDatabase
{
    private static AssetDatabase? s_Instance;
    public static AssetDatabase Instance => s_Instance ??= new AssetDatabase();

    private readonly Dictionary<Guid, string> m_AssetRegistry = new();
    private readonly Dictionary<string, Guid> m_PathRegistry = new();

    /// <summary>
    /// Scans the given project directory to index all assets and automatically provisions missing .meta files.
    /// </summary>
    public void Initialize(string projectContentPath)
    {
        m_AssetRegistry.Clear();
        m_PathRegistry.Clear();

        if (!Directory.Exists(projectContentPath))
            return;

        RefreshDirectory(projectContentPath);
    }

    /// <summary>
    /// Refreshes the indexing of a directory recursively, creating .meta files for any asset without one.
    /// </summary>
    public void RefreshDirectory(string directoryPath)
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
                meta = new AssetMetadata
                {
                    Guid = Guid.NewGuid(),
                    AssetType = Path.GetExtension(filePath)
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

            m_AssetRegistry[meta.Guid] = filePath;
            m_PathRegistry[filePath] = meta.Guid;
        }
    }

    /// <summary>
    /// Looks up the absolute file path for a registered asset reference.
    /// </summary>
    public string? GetAssetPath(Guid guid)
    {
        return m_AssetRegistry.TryGetValue(guid, out var path) ? path : null;
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
}
