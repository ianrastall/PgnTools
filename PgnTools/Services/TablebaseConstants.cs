using System.Collections.Frozen;
using System.Diagnostics;
using Windows.ApplicationModel;

namespace PgnTools.Services;

public enum TablebaseCategory
{
    Syzygy345,
    Syzygy6,
    Syzygy7
}

public record TablebaseFile(string Url, string FileName, TablebaseCategory Category, long EstimatedSizeBytes);

public static class TablebaseConstants
{
    public const long Size345 = 1_500_000_000L; // ~1.5 GB
    public const long Size6 = 160_000_000_000L; // ~160 GB
    public const long Size7 = 18_000_000_000_000L; // ~18 TB

    private const string DownloadListRelativePath = "Assets/Tablebases/download.txt";
    private static readonly Lazy<FrozenDictionary<TablebaseCategory, string[]>> FileListsLazy = new(LoadFileLists);

    public static FrozenDictionary<TablebaseCategory, string[]> FileLists => FileListsLazy.Value;

    public static string GetCategoryFolderName(TablebaseCategory category) => category switch
    {
        TablebaseCategory.Syzygy345 => "3-4-5",
        TablebaseCategory.Syzygy6 => "6",
        TablebaseCategory.Syzygy7 => "7",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown tablebase category.")
    };

    public static long GetEstimatedSizeBytes(TablebaseCategory category) => category switch
    {
        TablebaseCategory.Syzygy345 => Size345,
        TablebaseCategory.Syzygy6 => Size6,
        TablebaseCategory.Syzygy7 => Size7,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown tablebase category.")
    };

    public static TablebaseCategory GetCategoryFromUrl(string url) => url switch
    {
        _ when url.Contains("/3-4-5-", StringComparison.OrdinalIgnoreCase) => TablebaseCategory.Syzygy345,
        _ when url.Contains("/6-", StringComparison.OrdinalIgnoreCase) => TablebaseCategory.Syzygy6,
        _ when url.Contains("/7/", StringComparison.OrdinalIgnoreCase) => TablebaseCategory.Syzygy7,
        _ => throw new ArgumentOutOfRangeException(nameof(url), "Unknown tablebase category")
    };

    public static string GetDownloadListPath()
    {
        var relativePath = DownloadListRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var basePath = AppContext.BaseDirectory;
        var candidate = Path.Combine(basePath, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        try
        {
            var installedPath = Package.Current.InstalledLocation.Path;
            var packagedCandidate = Path.Combine(installedPath, relativePath);
            if (File.Exists(packagedCandidate))
            {
                return packagedCandidate;
            }
        }
        catch
        {
        }

        return candidate;
    }

    private static FrozenDictionary<TablebaseCategory, string[]> LoadFileLists()
    {
        var map = new Dictionary<TablebaseCategory, List<string>>
        {
            [TablebaseCategory.Syzygy345] = new List<string>(),
            [TablebaseCategory.Syzygy6] = new List<string>(),
            [TablebaseCategory.Syzygy7] = new List<string>()
        };

        var path = GetDownloadListPath();
        if (!File.Exists(path))
        {
            return map.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            try
            {
                var category = GetCategoryFromUrl(line);
                map[category].Add(line);
            }
            catch (ArgumentOutOfRangeException)
            {
                Debug.WriteLine($"Skipping unrecognized tablebase URL: {line}");
            }
        }

        return map.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }
}
