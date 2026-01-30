using System.IO;
using Windows.Storage;

namespace PgnTools.Helpers;

/// <summary>
/// Helpers for validating file and folder access selected via pickers.
/// </summary>
public static class FileValidationHelper
{
    public static async Task<(bool Success, string? ErrorMessage)> ValidateReadableFileAsync(StorageFile file)
    {
        try
        {
            using var stream = await file.OpenReadAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<(bool Success, string? ErrorMessage)> ValidateWritableFolderAsync(string folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return (false, "Folder path is empty.");
            }

            Directory.CreateDirectory(folderPath);

            var testFile = Path.Combine(folderPath, $".pgn_tools_write_{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(testFile, "test");
            File.Delete(testFile);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
