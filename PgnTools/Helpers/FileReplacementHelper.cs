// PGNTOOLS-TABLEBASES-BEGIN
using System.IO;

namespace PgnTools.Helpers;

public static class FileReplacementHelper
{
    private const int MaxReplaceAttempts = 6;

    public static string CreateTempFilePath(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination file path is required.", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.GetTempPath();
        }

        var fileName = Path.GetFileName(destinationPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "output";
        }

        var tempName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        return Path.Combine(directory, tempName);
    }

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

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxReplaceAttempts; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(tempFilePath, destinationPath, null);
                }
                else
                {
                    File.Move(tempFilePath, destinationPath);
                }

                return;
            }
            catch (FileNotFoundException)
            {
                // Destination does not exist yet, so fallback to a move.
                File.Move(tempFilePath, destinationPath);
                return;
            }
            catch (IOException ex) when (attempt < MaxReplaceAttempts)
            {
                lastError = ex;
                Thread.Sleep(attempt * 100);
            }
            catch (UnauthorizedAccessException ex) when (attempt < MaxReplaceAttempts)
            {
                lastError = ex;
                Thread.Sleep(attempt * 100);
            }
        }

        throw new IOException(
            $"Failed to replace '{destinationPath}' after {MaxReplaceAttempts} attempts. The file may be locked by another process.",
            lastError);
    }

    public static async Task ReplaceFileAsync(string tempFilePath, string destinationPath, CancellationToken ct = default)
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

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxReplaceAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(tempFilePath, destinationPath, null);
                }
                else
                {
                    File.Move(tempFilePath, destinationPath);
                }

                return;
            }
            catch (FileNotFoundException)
            {
                File.Move(tempFilePath, destinationPath);
                return;
            }
            catch (IOException ex) when (attempt < MaxReplaceAttempts)
            {
                lastError = ex;
                await Task.Delay(attempt * 100, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex) when (attempt < MaxReplaceAttempts)
            {
                lastError = ex;
                await Task.Delay(attempt * 100, ct).ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Failed to replace '{destinationPath}' after {MaxReplaceAttempts} attempts. The file may be locked by another process.",
            lastError);
    }
}
// PGNTOOLS-TABLEBASES-END
