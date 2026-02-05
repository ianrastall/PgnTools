using PgnTools.Helpers;

namespace PgnTools.Services;

public interface ILichessDownloaderService
{
    Task DownloadUserGamesAsync(
        string username,
        string outputFile,
        int? max,
        IProgress<LichessDownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public class LichessDownloaderService : ILichessDownloaderService
{
    private const int BufferSize = 65536;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(750);
    private static readonly HttpClient HttpClient = CreateClient();

    public async Task DownloadUserGamesAsync(
        string username,
        string outputFile,
        int? max,
        IProgress<LichessDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(outputFile))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFile));
        }

        if (max.HasValue && max.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), "Max games must be positive.");
        }

        var outputFullPath = Path.GetFullPath(outputFile);
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

        if (Path.GetDirectoryName(outputFullPath) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var escapedUsername = Uri.EscapeDataString(username.Trim());
        var uriBuilder = new UriBuilder($"https://lichess.org/api/games/user/{escapedUsername}");
        if (max.HasValue)
        {
            uriBuilder.Query = $"max={max.Value}";
        }
        var url = uriBuilder.Uri;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/x-chess-pgn");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var lastReportUtc = DateTime.MinValue;
            var stopwatch = progress != null ? System.Diagnostics.Stopwatch.StartNew() : null;

            progress?.Report(new LichessDownloadProgress(
                0,
                totalBytes,
                stopwatch?.Elapsed ?? TimeSpan.Zero));

            await using (var responseStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var outputStream = new FileStream(
                               tempOutputPath,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.None,
                               BufferSize,
                               FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                var buffer = new byte[BufferSize];
                long bytesRead = 0;
                int read;

                while ((read = await responseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await outputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytesRead += read;

                    if (progress != null && ShouldReportProgress(bytesRead, ref lastReportUtc))
                    {
                        progress.Report(new LichessDownloadProgress(
                            bytesRead,
                            totalBytes,
                            stopwatch?.Elapsed ?? TimeSpan.Zero));
                    }
                }

                await outputStream.FlushAsync(ct).ConfigureAwait(false);

                progress?.Report(new LichessDownloadProgress(
                    bytesRead,
                    totalBytes,
                    stopwatch?.Elapsed ?? TimeSpan.Zero));
            }

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
        }
        catch
        {
            if (File.Exists(tempOutputPath))
            {
                try
                {
                    File.Delete(tempOutputPath);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static bool ShouldReportProgress(long bytesRead, ref DateTime lastReportUtc)
    {
        if (bytesRead <= 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - lastReportUtc < ProgressTimeInterval)
        {
            return false;
        }

        lastReportUtc = now;
        return true;
    }
}

public sealed record LichessDownloadProgress(long BytesRead, long? TotalBytes, TimeSpan Elapsed);
