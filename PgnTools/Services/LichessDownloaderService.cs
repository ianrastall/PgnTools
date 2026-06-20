using System.Text;
using PgnTools.Helpers;
using PgnTools.Models;

namespace PgnTools.Services;

/// <summary>
/// Optional per-game filters applied to a Lichess user-games download.
/// Mirrors <see cref="ChesscomUserGameFilters"/> so both downloaders behave the same.
/// </summary>
public sealed record LichessUserGameFilters(
    bool OnlyUserWins = false,
    bool OnlyCheckmates = false,
    bool ExcludeBullet = false,
    bool ExcludeNonStandard = false)
{
    public static LichessUserGameFilters None { get; } = new();

    public bool IsActive =>
        OnlyUserWins ||
        OnlyCheckmates ||
        ExcludeBullet ||
        ExcludeNonStandard;
}

public interface ILichessDownloaderService
{
    Task DownloadUserGamesAsync(
        string username,
        string outputFile,
        int? max,
        LichessUserGameFilters filters,
        IProgress<LichessDownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public class LichessDownloaderService : ILichessDownloaderService
{
    private const int BufferSize = 65536;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(750);
    private static readonly HttpClient HttpClient = CreateClient();
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public LichessDownloaderService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task DownloadUserGamesAsync(
        string username,
        string outputFile,
        int? max,
        LichessUserGameFilters filters,
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

        filters ??= LichessUserGameFilters.None;

        var outputFullPath = Path.GetFullPath(outputFile);
        var downloadTempPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

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
                               downloadTempPath,
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

            if (filters.IsActive)
            {
                var filteredTempPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);
                try
                {
                    await FilterGamesAsync(downloadTempPath, filteredTempPath, username, filters, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    TryDelete(filteredTempPath);
                    throw;
                }

                TryDelete(downloadTempPath);
                FileReplacementHelper.ReplaceFile(filteredTempPath, outputFullPath);
            }
            else
            {
                FileReplacementHelper.ReplaceFile(downloadTempPath, outputFullPath);
            }
        }
        catch
        {
            TryDelete(downloadTempPath);
            throw;
        }
    }

    /// <summary>
    /// Streams the downloaded PGN game-by-game and writes only the games that pass the filters.
    /// </summary>
    private async Task FilterGamesAsync(
        string inputPath,
        string outputPath,
        string username,
        LichessUserGameFilters filters,
        CancellationToken ct)
    {
        await using var outputStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

        var trimmedUser = username.Trim();
        var firstOutput = true;

        await foreach (var game in _pgnReader.ReadGamesAsync(inputPath, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (!ShouldKeepGame(trimmedUser, game, filters))
            {
                continue;
            }

            if (!firstOutput)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await _pgnWriter.WriteGameAsync(writer, game, ct).ConfigureAwait(false);
            firstOutput = false;
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static bool ShouldKeepGame(string username, PgnGame game, LichessUserGameFilters filters)
    {
        if (filters.OnlyUserWins && !DidUserWin(username, game))
        {
            return false;
        }

        if (filters.OnlyCheckmates && !IsCheckmate(game))
        {
            return false;
        }

        if (filters.ExcludeBullet && IsBullet(game))
        {
            return false;
        }

        if (filters.ExcludeNonStandard && !IsStandard(game))
        {
            return false;
        }

        return true;
    }

    private static bool DidUserWin(string username, PgnGame game)
    {
        var result = GetHeader(game, "Result");

        if (string.Equals(GetHeader(game, "White"), username, StringComparison.OrdinalIgnoreCase))
        {
            return result == "1-0";
        }

        if (string.Equals(GetHeader(game, "Black"), username, StringComparison.OrdinalIgnoreCase))
        {
            return result == "0-1";
        }

        return false;
    }

    // Lichess marks checkmating moves with '#' in the move text (e.g. "Qh7#").
    private static bool IsCheckmate(PgnGame game) =>
        game.MoveText.Contains('#');

    private static bool IsBullet(PgnGame game)
    {
        // Lichess names the speed in the Event header ("Rated Bullet game", etc.).
        if (GetHeader(game, "Event").Contains("Bullet", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Fall back to the TimeControl header ("base+increment" seconds; "-" for correspondence).
        var timeControl = GetHeader(game, "TimeControl");
        if (string.IsNullOrEmpty(timeControl) || timeControl == "-")
        {
            return false;
        }

        var plusIndex = timeControl.IndexOf('+');
        var baseSpan = plusIndex >= 0 ? timeControl.AsSpan(0, plusIndex) : timeControl.AsSpan();
        return int.TryParse(baseSpan, out var seconds) && seconds < 180;
    }

    // Lichess omits the Variant header (or sets it to "Standard") for standard chess.
    private static bool IsStandard(PgnGame game)
    {
        var variant = GetHeader(game, "Variant");
        return string.IsNullOrEmpty(variant) ||
               string.Equals(variant, "Standard", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetHeader(PgnGame game, string key) =>
        game.Headers.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
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
