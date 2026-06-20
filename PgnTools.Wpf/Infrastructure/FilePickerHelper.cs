using Windows.Storage;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace PgnTools.Helpers;

public static class FilePickerHelper
{
    public static Task<StorageFile?> PickSingleFileAsync(
        nint windowHandle,
        string? settingsIdentifier,
        params string[] fileTypeFilters)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = false,
            Filter = BuildOpenFilter(fileTypeFilters)
        };

        return Task.FromResult(dialog.ShowDialog() == true
            ? new StorageFile(dialog.FileName)
            : null);
    }

    public static Task<IReadOnlyList<StorageFile>> PickMultipleFilesAsync(
        nint windowHandle,
        string? settingsIdentifier,
        params string[] fileTypeFilters)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = BuildOpenFilter(fileTypeFilters)
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.FromResult<IReadOnlyList<StorageFile>>([]);
        }

        IReadOnlyList<StorageFile> files = dialog.FileNames.Select(static path => new StorageFile(path)).ToList();
        return Task.FromResult(files);
    }

    public static Task<StorageFile?> PickSaveFileAsync(
        nint windowHandle,
        string suggestedFileName,
        Dictionary<string, IList<string>> fileTypeChoices,
        string? settingsIdentifier = null)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = BuildSaveFilter(fileTypeChoices),
            AddExtension = true,
            OverwritePrompt = false
        };

        return Task.FromResult(dialog.ShowDialog() == true
            ? new StorageFile(dialog.FileName)
            : null);
    }

    public static Task<StorageFolder?> PickFolderAsync(nint windowHandle, string? settingsIdentifier = null)
    {
        using var dialog = new FolderBrowserDialog
        {
            ShowNewFolderButton = true
        };

        return Task.FromResult(dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? new StorageFolder(dialog.SelectedPath)
            : null);
    }

    private static string BuildOpenFilter(IEnumerable<string> fileTypeFilters)
    {
        var normalized = NormalizeFilters(fileTypeFilters).ToList();
        if (normalized.Count == 0 || normalized.Contains("*"))
        {
            return "All Files (*.*)|*.*";
        }

        var joined = string.Join(";", normalized.Select(static filter => $"*{filter}"));
        return $"Supported Files ({joined})|{joined}|All Files (*.*)|*.*";
    }

    private static string BuildSaveFilter(Dictionary<string, IList<string>> fileTypeChoices)
    {
        var parts = new List<string>();
        foreach (var choice in fileTypeChoices)
        {
            var normalized = NormalizeFilters(choice.Value).ToList();
            if (normalized.Count == 0)
            {
                continue;
            }

            var joined = string.Join(";", normalized.Select(static filter => $"*{filter}"));
            parts.Add($"{choice.Key} ({joined})|{joined}");
        }

        return parts.Count == 0 ? "All Files (*.*)|*.*" : string.Join("|", parts);
    }

    private static IEnumerable<string> NormalizeFilters(IEnumerable<string> filters)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                continue;
            }

            var trimmed = filter.Trim();
            var normalized = trimmed == "*"
                ? "*"
                : trimmed.StartsWith(".", StringComparison.Ordinal)
                    ? trimmed
                    : $".{trimmed}";

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }
}
