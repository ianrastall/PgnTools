using System.Text.RegularExpressions;

namespace PgnTools.Services;

internal static class EnginePackageLayout
{
    private static readonly HashSet<string> RuntimeAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nnue",
        ".nn",
        ".exp",
        ".experience",
        ".book",
        ".bin",
        ".polyglot",
        ".ctg",
        ".cto",
        ".ctb",
        ".abk",
        ".obk",
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".ico",
        ".svg"
    };

    public static string BuildVersionDirectory(
        string workspaceFolder,
        string engineName,
        string versionSegment)
    {
        var normalizedWorkspace = Path.GetFullPath(workspaceFolder.Trim());
        var normalizedEngine = NormalizeSegment(engineName);
        var normalizedVersion = NormalizeSegment(versionSegment);

        var prefix = normalizedEngine.Length >= 2
            ? normalizedEngine[..2]
            : normalizedEngine.PadRight(2, '_');

        return Path.Combine(
            normalizedWorkspace,
            prefix,
            normalizedEngine,
            normalizedVersion);
    }

    public static void PrepareVersionDirectory(string versionDirectory)
    {
        if (Directory.Exists(versionDirectory))
        {
            Directory.Delete(versionDirectory, recursive: true);
        }

        Directory.CreateDirectory(versionDirectory);
    }

    public static string CopyPrimaryBinary(
        string builtBinaryPath,
        string versionDirectory,
        IProgress<string>? output)
    {
        var outputBinaryPath = Path.Combine(versionDirectory, Path.GetFileName(builtBinaryPath));
        File.Copy(builtBinaryPath, outputBinaryPath, overwrite: true);
        output?.Report($"Packaged executable: {outputBinaryPath}");
        return outputBinaryPath;
    }

    public static void CopyRuntimeAssets(
        string searchRoot,
        string versionDirectory,
        string primaryBinaryPath,
        IProgress<string>? output)
    {
        if (!Directory.Exists(searchRoot))
        {
            return;
        }

        var sourceFiles = Directory
            .EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
            .Where(path => RuntimeAssetExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceFiles.Count == 0)
        {
            return;
        }

        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceFile in sourceFiles)
        {
            if (string.Equals(sourceFile, primaryBinaryPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPath = BuildUniqueTargetPath(
                versionDirectory,
                Path.GetFileName(sourceFile),
                seenTargets);

            File.Copy(sourceFile, targetPath, overwrite: true);
            output?.Report($"Packaged runtime asset: {targetPath}");
        }
    }

    public static void MoveRepositoryToSourceFolder(
        string repositoryPath,
        string versionDirectory,
        IProgress<string>? output)
    {
        if (!Directory.Exists(repositoryPath))
        {
            return;
        }

        var sourceDestination = Path.Combine(versionDirectory, "src");
        if (Directory.Exists(sourceDestination))
        {
            Directory.Delete(sourceDestination, recursive: true);
        }

        Directory.Move(repositoryPath, sourceDestination);
        output?.Report($"Packaged source tree: {sourceDestination}");
    }

    private static string NormalizeSegment(string value)
    {
        var lowered = (value ?? string.Empty).Trim().ToLowerInvariant();
        var sanitized = Regex.Replace(lowered, @"[^a-z0-9._-]+", "-");
        sanitized = sanitized.Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string BuildUniqueTargetPath(
        string versionDirectory,
        string fileName,
        HashSet<string> seenTargets)
    {
        var candidate = Path.Combine(versionDirectory, fileName);
        if (seenTargets.Add(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        var suffix = 2;
        while (true)
        {
            var suffixed = $"{stem}_{suffix}{extension}";
            candidate = Path.Combine(versionDirectory, suffixed);

            if (seenTargets.Add(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }
}
