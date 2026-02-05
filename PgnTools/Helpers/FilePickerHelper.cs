using System.Collections.Generic;
using System.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PgnTools.Helpers;

/// <summary>
/// Helper class for file/folder pickers in WinUI 3 Unpackaged apps.
/// Handles the IInitializeWithWindow pattern required for unpackaged apps.
/// </summary>
public static class FilePickerHelper
{
    /// <summary>
    /// Opens a file picker to select a single file.
    /// </summary>
    /// <param name="windowHandle">The HWND of the main window.</param>
    /// <param name="settingsIdentifier">Identifier to persist picker location and view settings.</param>
    /// <param name="fileTypeFilters">File type filters (e.g., ".pgn", ".txt").</param>
    /// <returns>The selected StorageFile, or null if cancelled.</returns>
    public static async Task<StorageFile?> PickSingleFileAsync(
        nint windowHandle,
        string? settingsIdentifier,
        params string[] fileTypeFilters)
    {
        ValidateWindowHandle(windowHandle);
        var picker = new FileOpenPicker();

        // Initialize with window handle for unpackaged apps
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        if (!string.IsNullOrWhiteSpace(settingsIdentifier))
        {
            picker.SettingsIdentifier = settingsIdentifier;
        }

        var addedFilter = false;
        foreach (var filter in NormalizeFilters(fileTypeFilters))
        {
            picker.FileTypeFilter.Add(filter);
            addedFilter = true;
        }

        if (!addedFilter)
        {
            picker.FileTypeFilter.Add("*");
        }

        return await picker.PickSingleFileAsync();
    }

    /// <summary>
    /// Opens a file picker to select multiple files.
    /// </summary>
    /// <param name="windowHandle">The HWND of the main window.</param>
    /// <param name="settingsIdentifier">Identifier to persist picker location and view settings.</param>
    /// <param name="fileTypeFilters">File type filters (e.g., ".pgn", ".txt").</param>
    /// <returns>The selected StorageFiles, or empty if cancelled.</returns>
    public static async Task<IReadOnlyList<StorageFile>> PickMultipleFilesAsync(
        nint windowHandle,
        string? settingsIdentifier,
        params string[] fileTypeFilters)
    {
        ValidateWindowHandle(windowHandle);
        var picker = new FileOpenPicker();

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        if (!string.IsNullOrWhiteSpace(settingsIdentifier))
        {
            picker.SettingsIdentifier = settingsIdentifier;
        }

        var addedFilter = false;
        foreach (var filter in NormalizeFilters(fileTypeFilters))
        {
            picker.FileTypeFilter.Add(filter);
            addedFilter = true;
        }

        if (!addedFilter)
        {
            picker.FileTypeFilter.Add("*");
        }

        return await picker.PickMultipleFilesAsync();
    }

    /// <summary>
    /// Opens a file picker to save a file.
    /// </summary>
    /// <param name="windowHandle">The HWND of the main window.</param>
    /// <param name="suggestedFileName">Suggested file name.</param>
    /// <param name="fileTypeChoices">Dictionary of file type descriptions to extensions.</param>
    /// <param name="settingsIdentifier">Identifier to persist picker location and view settings.</param>
    /// <returns>The selected StorageFile, or null if cancelled.</returns>
    public static async Task<StorageFile?> PickSaveFileAsync(
        nint windowHandle,
        string suggestedFileName,
        Dictionary<string, IList<string>> fileTypeChoices,
        string? settingsIdentifier = null)
    {
        ValidateWindowHandle(windowHandle);
        var picker = new FileSavePicker();

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = suggestedFileName;
        if (!string.IsNullOrWhiteSpace(settingsIdentifier))
        {
            picker.SettingsIdentifier = settingsIdentifier;
        }

        var normalizedChoices = new List<(string Label, List<string> Extensions)>();
        foreach (var choice in fileTypeChoices)
        {
            var normalizedExtensions = NormalizeFilters(choice.Value).ToList();
            if (normalizedExtensions.Count == 0)
            {
                continue;
            }

            normalizedChoices.Add((choice.Key, normalizedExtensions));
            picker.FileTypeChoices.Add(choice.Key, normalizedExtensions);
        }

        if (string.IsNullOrWhiteSpace(picker.DefaultFileExtension))
        {
            foreach (var choice in normalizedChoices)
            {
                var extension = choice.Extensions[0];
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    picker.DefaultFileExtension = extension.StartsWith(".", StringComparison.Ordinal)
                        ? extension
                        : $".{extension}";
                    break;
                }
            }
        }

        return await picker.PickSaveFileAsync();
    }

    /// <summary>
    /// Opens a folder picker to select a folder.
    /// </summary>
    /// <param name="windowHandle">The HWND of the main window.</param>
    /// <param name="settingsIdentifier">Identifier to persist picker location and view settings.</param>
    /// <returns>The selected StorageFolder, or null if cancelled.</returns>
    public static async Task<StorageFolder?> PickFolderAsync(nint windowHandle, string? settingsIdentifier = null)
    {
        ValidateWindowHandle(windowHandle);
        var picker = new FolderPicker();

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");
        if (!string.IsNullOrWhiteSpace(settingsIdentifier))
        {
            picker.SettingsIdentifier = settingsIdentifier;
        }

        return await picker.PickSingleFolderAsync();
    }

    private static void ValidateWindowHandle(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("Window handle (HWND) cannot be zero.", nameof(windowHandle));
        }
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
