// PGNTOOLS-TABLEBASES-BEGIN
using System.Buffers;
using System.Diagnostics;
using System.Net.Http;

namespace PgnTools.Services;

public enum TablebaseProgressStage
{
    Starting,
    AlreadyPresent,
    SkippedLocked,
    Downloading,
    Completed,
    Failed
}

public record TablebaseProgress(
    TablebaseCategory Category,
    string CurrentFileName,
    int FilesCompleted,
    int TotalFiles,
    long BytesRead,
    long? TotalBytes,
    double SpeedMbPerSecond,
    int FilesSkipped = 0,
    TablebaseProgressStage Stage = TablebaseProgressStage.Downloading);

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

        var estimatedBytes = TablebaseConstants.GetEstimatedSizeBytes(category);
        var existingBytes = EstimateExistingBytes(targetPath, urls);
        var requiredBytes = Math.Max(0, estimatedBytes - existingBytes);
        if (requiredBytes > 0)
        {
            await Task.Run(() => EnsureDiskSpace(targetPath, requiredBytes), ct).ConfigureAwait(false);
        }

        var completed = 0;
        var skipped = 0;
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = GetFileNameFromUrl(url);
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
                0,
                skipped,
                TablebaseProgressStage.Starting));

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
                    0,
                    skipped,
                    TablebaseProgressStage.AlreadyPresent));
                continue;
            }

            if (File.Exists(filePath) && IsFileInUse(filePath))
            {
                skipped++;
                progress?.Report(new TablebaseProgress(
                    category,
                    fileName,
                    completed,
                    urls.Length,
                    0,
                    null,
                    0,
                    skipped,
                    TablebaseProgressStage.SkippedLocked));
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
                    0,
                    skipped,
                    TablebaseProgressStage.Downloading));

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
                                    speed,
                                    skipped,
                                    TablebaseProgressStage.Downloading));

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
                            computedSpeed,
                            skipped,
                            TablebaseProgressStage.Downloading));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                ValidateDownloadedFile(tempPath, fileName, finalBytesRead, finalTotalBytes);

                try
                {
                    await FileReplacementHelper.ReplaceFileAsync(tempPath, filePath, ct).ConfigureAwait(false);
                }
                catch (IOException) when (IsFileInUse(filePath) || IsFileInUse(tempPath))
                {
                    TryDeleteFile(tempPath);
                    skipped++;
                    progress?.Report(new TablebaseProgress(
                        category,
                        fileName,
                        completed,
                        urls.Length,
                        finalBytesRead,
                        finalTotalBytes,
                        finalSpeed,
                        skipped,
                        TablebaseProgressStage.SkippedLocked));
                    continue;
                }
                catch (UnauthorizedAccessException) when (IsFileInUse(filePath) || IsFileInUse(tempPath))
                {
                    TryDeleteFile(tempPath);
                    skipped++;
                    progress?.Report(new TablebaseProgress(
                        category,
                        fileName,
                        completed,
                        urls.Length,
                        finalBytesRead,
                        finalTotalBytes,
                        finalSpeed,
                        skipped,
                        TablebaseProgressStage.SkippedLocked));
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
                finalSpeed,
                skipped,
                TablebaseProgressStage.Completed));
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
            var expected = await TryGetRemoteContentLengthAsync(url, ct).ConfigureAwait(false);
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
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
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

    private static void EnsureDiskSpace(string path, long requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        if (!TryGetAvailableFreeSpace(path, out var availableBytes))
        {
            Debug.WriteLine($"Unable to determine free space for '{path}'. Skipping disk space check.");
            return;
        }

        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"Insufficient disk space on {path}. Required: {FormatBytes(requiredBytes)}, " +
                $"Available: {FormatBytes(availableBytes)}.");
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

    private static long EstimateExistingBytes(string targetPath, string[] urls)
    {
        if (urls.Length == 0)
        {
            return 0;
        }

        long existingBytes = 0;
        foreach (var url in urls)
        {
            var fileName = GetFileNameFromUrl(url);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var filePath = Path.Combine(targetPath, fileName);
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists)
            {
                existingBytes += fileInfo.Length;
            }
        }

        return existingBytes;
    }

    private static string GetFileNameFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL is required.", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid tablebase URL: {url}");
        }

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException($"URL does not contain a file name: {url}");
        }

        return fileName;
    }

    private async Task<long?> TryGetRemoteContentLengthAsync(string url, CancellationToken ct)
    {
        using (var headRequest = new HttpRequestMessage(HttpMethod.Head, url))
        using (var headResponse = await httpClient.SendAsync(headRequest, ct).ConfigureAwait(false))
        {
            if (headResponse.IsSuccessStatusCode)
            {
                var length = headResponse.Content.Headers.ContentLength;
                if (length.HasValue && length.Value > 0)
                {
                    return length.Value;
                }
            }
        }

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var rangeResponse = await httpClient.SendAsync(
            rangeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        if (!rangeResponse.IsSuccessStatusCode)
        {
            return null;
        }

        var contentRange = rangeResponse.Content.Headers.ContentRange;
        if (contentRange?.Length is long totalLength && totalLength > 0)
        {
            return totalLength;
        }

        var lengthFallback = rangeResponse.Content.Headers.ContentLength;
        return lengthFallback.HasValue && lengthFallback.Value > 0 ? lengthFallback.Value : null;
    }

    private static bool TryGetAvailableFreeSpace(string path, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            availableBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void ValidateDownloadedFile(string tempPath, string fileName, long bytesRead, long? expectedBytes)
    {
        var fileInfo = new FileInfo(tempPath);
        if (!fileInfo.Exists)
        {
            throw new IOException($"Download failed for '{fileName}'. Temp file missing.");
        }

        if (bytesRead > 0 && fileInfo.Length != bytesRead)
        {
            throw new IOException(
                $"Download incomplete for '{fileName}'. Bytes read: {bytesRead:N0}, temp length: {fileInfo.Length:N0}.");
        }

        if (expectedBytes.HasValue && expectedBytes.Value > 0 && fileInfo.Length != expectedBytes.Value)
        {
            throw new IOException(
                $"Download incomplete for '{fileName}'. Expected {expectedBytes.Value:N0} bytes, got {fileInfo.Length:N0}.");
        }
    }
}
// PGNTOOLS-TABLEBASES-END
