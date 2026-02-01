using System.IO;

namespace PgnTools.Helpers;

public static class FileReplacementHelper
{
    public static void ReplaceFile(string tempFilePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(tempFilePath))
        {
            throw new ArgumentException("Temp file path is required.", nameof(tempFilePath));
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination file path is required.", nameof(destinationPath));
        }

        if (!File.Exists(tempFilePath))
        {
            throw new FileNotFoundException("Temp file not found.", tempFilePath);
        }

        if (Path.GetDirectoryName(destinationPath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            File.Replace(tempFilePath, destinationPath, null);
        }
        catch (FileNotFoundException)
        {
            File.Move(tempFilePath, destinationPath);
        }
    }
}
