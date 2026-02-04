using System.Buffers;
using System.Diagnostics;
using System.Net.Http;

namespace PgnTools.Services;

public record TablebaseProgress(
    TablebaseCategory Category,
    string CurrentFileName,
    int FilesCompleted,
    int TotalFiles,
    long BytesRead,
    long? TotalBytes,
    double SpeedMbPerSecond);

public interface ITablebaseDownloaderService
{
    Task DownloadCategoryAsync(
        TablebaseCategory category,
        string rootOutputPath,
        IProgress<TablebaseProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class TablebaseDownloaderService(HttpClient httpClient) : ITablebaseDownloaderService
{
    private const int BufferSize = 81920;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public async Task DownloadCategoryAsync(
        TablebaseCategory category,
        string rootOutputPath,
        IProgress<TablebaseProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootOutputPath))
        {
            throw new ArgumentException("Output folder is required.", nameof(rootOutputPath));
        }

        var targetDirName = TablebaseConstants.GetCategoryFolderName(category);
        var targetPath = Path.Combine(rootOutputPath, targetDirName);
        Directory.CreateDirectory(targetPath);

        if (!TablebaseConstants.FileLists.TryGetValue(category, out var urls) || urls.Length == 0)
        {
            throw new InvalidOperationException(
                "No tablebase URLs found. Ensure Assets/Tablebases/download.txt is present.");
        }

        EnsureDiskSpace(targetPath, TablebaseConstants.GetEstimatedSizeBytes(category));

        var completed = 0;
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(url);
            var filePath = Path.Combine(targetPath, fileName);
            var finalBytesRead = 0L;
            long? finalTotalBytes = null;
            var finalSpeed = 0d;

            progress?.Report(new TablebaseProgress(
                category,
                fileName,
                completed,
                urls.Length,
                0,
                null,
                0));

            if (File.Exists(filePath) && IsFileInUse(filePath))
            {
                completed++;
                progress?.Report(new TablebaseProgress(
                    category,
                    fileName,
                    completed,
                    urls.Length,
                    0,
                    null,
                    0));
                continue;
            }

            if (await IsExistingFileCompleteAsync(filePath, url, ct).ConfigureAwait(false))
            {
                completed++;
                progress?.Report(new TablebaseProgress(
                    category,
                    fileName,
                    completed,
                    urls.Length,
                    0,
                    null,
                    0));
                continue;
            }

            var tempPath = FileReplacementHelper.CreateTempFilePath(filePath);
            try
            {
                using var response = await httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                finalTotalBytes = totalBytes;

                progress?.Report(new TablebaseProgress(
                    category,
                    fileName,
                    completed,
                    urls.Length,
                    0,
                    totalBytes,
                    0));

                {
                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fileStream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        var fileStopwatch = Stopwatch.StartNew();
                        var lastReport = Stopwatch.StartNew();
                        long bytesRead = 0;

                        while (true)
                        {
                            var read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                            if (read <= 0)
                            {
                                break;
                            }

                            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            bytesRead += read;

                            if (lastReport.Elapsed >= ProgressInterval)
                            {
                                var speed = bytesRead > 0 && fileStopwatch.Elapsed.TotalSeconds > 0
                                    ? bytesRead / fileStopwatch.Elapsed.TotalSeconds / 1024d / 1024d
                                    : 0;

                                progress?.Report(new TablebaseProgress(
                                    category,
                                    fileName,
                                    completed,
                                    urls.Length,
                                    bytesRead,
                                    totalBytes,
                                    speed));

                                lastReport.Restart();
                            }
                        }

                        var computedSpeed = bytesRead > 0 && fileStopwatch.Elapsed.TotalSeconds > 0
                            ? bytesRead / fileStopwatch.Elapsed.TotalSeconds / 1024d / 1024d
                            : 0;
                        finalBytesRead = bytesRead;
                        finalSpeed = computedSpeed;

                        progress?.Report(new TablebaseProgress(
                            category,
                            fileName,
                            completed,
                            urls.Length,
                            bytesRead,
                            totalBytes,
                            computedSpeed));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                if (!TryReplaceFile(tempPath, filePath))
                {
                    TryDeleteFile(tempPath);
                    completed++;
                    progress?.Report(new TablebaseProgress(
                        category,
                        fileName,
                        completed,
                        urls.Length,
                        finalBytesRead,
                        finalTotalBytes,
                        finalSpeed));
                    continue;
                }
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            completed++;
            progress?.Report(new TablebaseProgress(
                category,
                fileName,
                completed,
                urls.Length,
                finalBytesRead,
                finalTotalBytes,
                finalSpeed));
        }
    }

    private async Task<bool> IsExistingFileCompleteAsync(string filePath, string url, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await httpClient.SendAsync(headRequest, ct).ConfigureAwait(false);
            if (!headResponse.IsSuccessStatusCode)
            {
                return false;
            }

            var expected = headResponse.Content.Headers.ContentLength;
            if (!expected.HasValue || expected.Value <= 0)
            {
                return false;
            }

            var actual = new FileInfo(filePath).Length;
            return actual == expected.Value;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileInUse(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private static bool TryReplaceFile(string tempPath, string destinationPath)
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                FileReplacementHelper.ReplaceFile(tempPath, destinationPath);
                return true;
            }
            catch (IOException) when (IsFileInUse(destinationPath) || IsFileInUse(tempPath))
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (IsFileInUse(destinationPath) || IsFileInUse(tempPath))
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }

        return false;
    }

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Invalid path.", nameof(path));
        }

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"Insufficient disk space on {root}. Required: {FormatBytes(requiredBytes)}, " +
                $"Available: {FormatBytes(drive.AvailableFreeSpace)}.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double scale = 1024;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= scale && unitIndex < units.Length - 1)
        {
            value /= scale;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
