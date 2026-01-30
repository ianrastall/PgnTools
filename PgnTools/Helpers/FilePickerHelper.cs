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
        var picker = new FileOpenPicker();

        // Initialize with window handle for unpackaged apps
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        if (!string.IsNullOrWhiteSpace(settingsIdentifier))
        {
            picker.SettingsIdentifier = settingsIdentifier;
        }

        foreach (var filter in fileTypeFilters)
        {
            picker.FileTypeFilter.Add(filter);
        }

        if (fileTypeFilters.Length == 0)
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
        var picker = new FileOpenPicker();

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        if (!string.IsNullOrWhiteSpace(settingsIdentifier))
        {
            picker.SettingsIdentifier = settingsIdentifier;
        }

        foreach (var filter in fileTypeFilters)
        {
            picker.FileTypeFilter.Add(filter);
        }

        if (fileTypeFilters.Length == 0)
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
        var picker = new FileSavePicker();

        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = suggestedFileName;
        if (!string.IsNullOrWhiteSpace(settingsIdentifier))
        {
            picker.SettingsIdentifier = settingsIdentifier;
        }

        foreach (var choice in fileTypeChoices)
        {
            picker.FileTypeChoices.Add(choice.Key, choice.Value);
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
}
