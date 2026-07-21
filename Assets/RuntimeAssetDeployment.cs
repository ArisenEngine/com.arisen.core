using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace ArisenEngine.Core.Assets;

public sealed record RuntimeAssetDeploymentResult(
    string OutputRoot,
    string ContentRoot,
    string CatalogPath,
    int ArtifactCount,
    int ReusedArtifactCount,
    int CopiedArtifactCount);

/// <summary>
/// Publishes one complete cooked closure into an owned runtime content directory. Package source
/// formats remain opaque here; deployment only consumes verified catalog metadata and cooked files.
/// </summary>
public static class RuntimeAssetDeployment
{
    public const string ContentDirectoryName = "Content";

    public static RuntimeAssetDeploymentResult Deploy(
        RuntimeAssetCookResult cookResult,
        string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(cookResult);
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("A non-empty runtime output root is required.", nameof(outputRoot));
        }

        string fullOutputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        Directory.CreateDirectory(fullOutputRoot);

        string contentRoot = Path.Combine(fullOutputRoot, ContentDirectoryName);
        string catalogPath = Path.Combine(fullOutputRoot, RuntimeAssetCatalog.DefaultFileName);
        ValidateExistingDeployment(contentRoot, catalogPath);

        IReadOnlyDictionary<RuntimeAssetIdentity, string> sourcePaths =
            ValidateCookResult(cookResult);
        ValidateCookedSources(cookResult.Catalog, sourcePaths);
        RuntimeAssetCatalog? previousCatalog = TryLoadPreviousCatalog(
            cookResult.Catalog,
            contentRoot,
            catalogPath);
        string transactionId = Guid.NewGuid().ToString("N");
        string stagingRoot = Path.Combine(
            fullOutputRoot,
            $".arisen-runtime-assets-stage-{transactionId}");
        string backupRoot = Path.Combine(
            fullOutputRoot,
            $".arisen-runtime-assets-backup-{transactionId}");
        string stagingContentRoot = Path.Combine(stagingRoot, ContentDirectoryName);
        string stagingCatalogPath = Path.Combine(
            stagingRoot,
            RuntimeAssetCatalog.DefaultFileName);
        int reusedArtifactCount = 0;
        int copiedArtifactCount = 0;
        bool deploymentCommitted = false;

        try
        {
            Directory.CreateDirectory(stagingContentRoot);
            foreach (RuntimeAssetCatalogArtifact artifact in cookResult.Catalog.Artifacts)
            {
                string destinationPath = cookResult.Catalog.ResolveArtifactPath(
                    stagingContentRoot,
                    artifact.Guid,
                    artifact.Variant);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (TryResolveReusableArtifact(
                        previousCatalog,
                        contentRoot,
                        artifact,
                        out string previousPath) &&
                    TryCreateHardLink(destinationPath, previousPath))
                {
                    reusedArtifactCount++;
                }
                else
                {
                    File.Copy(sourcePaths[artifact.Identity], destinationPath, overwrite: false);
                    copiedArtifactCount++;
                }
            }

            cookResult.Catalog.ValidateDeployment(stagingContentRoot);
            File.WriteAllBytes(stagingCatalogPath, cookResult.Catalog.Serialize());

            CommitDeployment(
                stagingContentRoot,
                stagingCatalogPath,
                contentRoot,
                catalogPath,
                backupRoot);
            deploymentCommitted = true;

            return new RuntimeAssetDeploymentResult(
                fullOutputRoot,
                contentRoot,
                catalogPath,
                cookResult.Catalog.Artifacts.Count,
                reusedArtifactCount,
                copiedArtifactCount);
        }
        finally
        {
            DeleteDirectoryBestEffort(stagingRoot);
            if (deploymentCommitted)
            {
                DeleteDirectoryBestEffort(backupRoot);
            }
        }
    }

    private static IReadOnlyDictionary<RuntimeAssetIdentity, string> ValidateCookResult(
        RuntimeAssetCookResult cookResult)
    {
        if (cookResult.Catalog == null)
        {
            throw Invalid("The cook result does not contain a runtime asset catalog.");
        }

        if (cookResult.Files == null)
        {
            throw Invalid("The cook result does not contain a cooked-file mapping.");
        }

        if (cookResult.Files.Count != cookResult.Catalog.Artifacts.Count)
        {
            throw Invalid(
                $"Cooked-file mapping count {cookResult.Files.Count} does not match catalog " +
                $"artifact count {cookResult.Catalog.Artifacts.Count}.");
        }

        var sourcePaths = new Dictionary<RuntimeAssetIdentity, string>();
        foreach (RuntimeAssetCookedFile? file in cookResult.Files)
        {
            if (file == null || file.Artifact == null)
            {
                throw Invalid("The cooked-file mapping contains a null entry or artifact.");
            }

            RuntimeAssetIdentity identity = file.Artifact.Identity;
            if (!sourcePaths.TryAdd(identity, string.Empty))
            {
                throw Invalid($"The cooked-file mapping contains duplicate artifact '{identity}'.");
            }

            if (!cookResult.Catalog.TryGetArtifact(
                    file.Artifact.Guid,
                    file.Artifact.Variant,
                    out RuntimeAssetCatalogArtifact catalogArtifact))
            {
                throw Invalid(
                    $"Cooked-file mapping artifact '{identity}' is not declared by the catalog.");
            }

            if (!ArtifactsMatch(catalogArtifact, file.Artifact))
            {
                throw Invalid(
                    $"Cooked-file mapping metadata for artifact '{identity}' does not exactly " +
                    "match the catalog.");
            }

            if (string.IsNullOrWhiteSpace(file.SourcePath) ||
                !Path.IsPathFullyQualified(file.SourcePath))
            {
                throw Invalid(
                    $"Cooked-file mapping for artifact '{identity}' must use a fully qualified " +
                    "source path.");
            }

            string sourcePath = Path.GetFullPath(file.SourcePath);
            if (!File.Exists(sourcePath))
            {
                throw Invalid(
                    $"Cooked source for artifact '{identity}' is missing at '{sourcePath}'.");
            }

            sourcePaths[identity] = sourcePath;
        }

        return sourcePaths;
    }

    private static void ValidateCookedSources(
        RuntimeAssetCatalog catalog,
        IReadOnlyDictionary<RuntimeAssetIdentity, string> sourcePaths)
    {
        foreach (RuntimeAssetCatalogArtifact artifact in catalog.Artifacts)
        {
            string sourcePath = sourcePaths[artifact.Identity];
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != artifact.SizeInBytes)
            {
                throw Invalid(
                    $"Cooked source for artifact '{artifact.Identity}' size mismatch at " +
                    $"'{sourcePath}': expected {artifact.SizeInBytes}, found {stream.Length}.");
            }

            string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"Cooked source for artifact '{artifact.Identity}' SHA-256 mismatch at " +
                    $"'{sourcePath}': expected {artifact.Sha256}, found {actualHash}.");
            }
        }
    }

    private static RuntimeAssetCatalog? TryLoadPreviousCatalog(
        RuntimeAssetCatalog catalog,
        string contentRoot,
        string catalogPath)
    {
        if (!Directory.Exists(contentRoot) || !File.Exists(catalogPath))
        {
            return null;
        }

        try
        {
            RuntimeAssetCatalog previousCatalog = RuntimeAssetCatalog.Parse(
                File.ReadAllBytes(catalogPath));
            return string.Equals(
                previousCatalog.TargetProfile,
                catalog.TargetProfile,
                StringComparison.Ordinal)
                ? previousCatalog
                : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool TryResolveReusableArtifact(
        RuntimeAssetCatalog? previousCatalog,
        string contentRoot,
        RuntimeAssetCatalogArtifact artifact,
        out string previousPath)
    {
        previousPath = string.Empty;
        if (previousCatalog == null ||
            !previousCatalog.TryGetArtifact(
                artifact.Guid,
                artifact.Variant,
                out RuntimeAssetCatalogArtifact previousArtifact) ||
            previousArtifact.FormatVersion != artifact.FormatVersion ||
            previousArtifact.SizeInBytes != artifact.SizeInBytes ||
            !string.Equals(previousArtifact.Sha256, artifact.Sha256, StringComparison.Ordinal))
        {
            return false;
        }

        previousPath = previousCatalog.ResolveArtifactPath(
            contentRoot,
            previousArtifact.Guid,
            previousArtifact.Variant);
        return FileMatchesArtifact(previousPath, previousArtifact);
    }

    private static bool FileMatchesArtifact(
        string path,
        RuntimeAssetCatalogArtifact artifact)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != artifact.SizeInBytes)
            {
                return false;
            }

            string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCreateHardLink(string destinationPath, string existingPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return NativeMethods.CreateHardLink(
                    destinationPath,
                    existingPath,
                    IntPtr.Zero);
            }

            if (OperatingSystem.IsLinux() ||
                OperatingSystem.IsMacOS() ||
                OperatingSystem.IsFreeBSD())
            {
                return NativeMethods.Link(existingPath, destinationPath) == 0;
            }

            return false;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateHardLinkW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(
            string newFileName,
            string existingFileName,
            IntPtr securityAttributes);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        internal static extern int Link(string existingPath, string newPath);
    }

    private static bool ArtifactsMatch(
        RuntimeAssetCatalogArtifact expected,
        RuntimeAssetCatalogArtifact actual)
    {
        if (expected.Guid != actual.Guid ||
            !string.Equals(expected.PackageId, actual.PackageId, StringComparison.Ordinal) ||
            !string.Equals(expected.AssetType, actual.AssetType, StringComparison.Ordinal) ||
            !string.Equals(expected.Variant, actual.Variant, StringComparison.Ordinal) ||
            !string.Equals(
                expected.OutputRelativePath,
                actual.OutputRelativePath,
                StringComparison.Ordinal) ||
            expected.SizeInBytes != actual.SizeInBytes ||
            !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal) ||
            expected.FormatVersion != actual.FormatVersion ||
            expected.Dependencies.Count != actual.Dependencies.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Dependencies.Count; index++)
        {
            if (expected.Dependencies[index] != actual.Dependencies[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateExistingDeployment(string contentRoot, string catalogPath)
    {
        if (Path.Exists(contentRoot))
        {
            FileAttributes attributes = File.GetAttributes(contentRoot);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw Invalid(
                    $"Runtime content path '{contentRoot}' exists but is not a directory.");
            }

            EnsureTreeContainsNoReparsePoints(contentRoot);
        }

        if (Path.Exists(catalogPath))
        {
            FileAttributes attributes = File.GetAttributes(catalogPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw Invalid(
                    $"Runtime catalog path '{catalogPath}' exists but is not a file.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(
                    $"Runtime catalog path '{catalogPath}' is a symbolic link or reparse point.");
            }
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(string contentRoot)
    {
        var pending = new Stack<string>();
        pending.Push(contentRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            FileAttributes directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(
                    $"Existing runtime content contains a symbolic link or reparse point at " +
                    $"'{directory}'.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw Invalid(
                        $"Existing runtime content contains a symbolic link or reparse point at " +
                        $"'{entry}'.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void CommitDeployment(
        string stagingContentRoot,
        string stagingCatalogPath,
        string contentRoot,
        string catalogPath,
        string backupRoot)
    {
        string backupContentRoot = Path.Combine(backupRoot, ContentDirectoryName);
        string backupCatalogPath = Path.Combine(
            backupRoot,
            RuntimeAssetCatalog.DefaultFileName);
        bool backedUpContent = false;
        bool backedUpCatalog = false;
        bool installedContent = false;
        bool installedCatalog = false;

        Directory.CreateDirectory(backupRoot);
        try
        {
            if (Directory.Exists(contentRoot))
            {
                Directory.Move(contentRoot, backupContentRoot);
                backedUpContent = true;
            }

            if (File.Exists(catalogPath))
            {
                File.Move(catalogPath, backupCatalogPath);
                backedUpCatalog = true;
            }

            Directory.Move(stagingContentRoot, contentRoot);
            installedContent = true;
            File.Move(stagingCatalogPath, catalogPath);
            installedCatalog = true;
        }
        catch (Exception commitException)
        {
            try
            {
                if (installedCatalog && File.Exists(catalogPath))
                {
                    File.Delete(catalogPath);
                }

                if (installedContent && Directory.Exists(contentRoot))
                {
                    Directory.Delete(contentRoot, recursive: true);
                }

                if (backedUpContent && Directory.Exists(backupContentRoot))
                {
                    Directory.Move(backupContentRoot, contentRoot);
                }

                if (backedUpCatalog && File.Exists(backupCatalogPath))
                {
                    File.Move(backupCatalogPath, catalogPath);
                }
            }
            catch (Exception rollbackException)
            {
                throw Invalid(
                    "Runtime deployment commit failed and the previous deployment could not be " +
                    "fully restored.",
                    new AggregateException(commitException, rollbackException));
            }

            throw Invalid(
                "Runtime deployment commit failed; the previous deployment was restored.",
                commitException);
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A completed deployment remains valid even if a temporary backup is externally held.
        }
    }

    private static InvalidDataException Invalid(
        string message,
        Exception? innerException = null)
    {
        return new InvalidDataException($"[RuntimeAssetDeployment] {message}", innerException);
    }
}
