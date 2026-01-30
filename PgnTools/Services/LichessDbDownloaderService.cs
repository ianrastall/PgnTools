using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using ZstdSharp;

namespace PgnTools.Services;

public interface ILichessDbDownloaderService
{
    Task DownloadAndFilterAsync(
        string url,
        string outputPath,
        int minElo,
        bool excludeBullet,
        bool excludeNonStandard,
        bool onlyCheckmates,
        IProgress<LichessDbProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class LichessDbDownloaderService : ILichessDbDownloaderService
{
    private const int BufferSize = 65536;
    private const double EstimatedCompressionRatio = 7.1;
    private const int ProgressGameInterval = 5000;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(750);
    private static readonly HttpClient HttpClient = CreateClient();

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public LichessDbDownloaderService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task DownloadAndFilterAsync(
        string url,
        string outputPath,
        int minElo,
        bool excludeBullet,
        bool excludeNonStandard,
        bool onlyCheckmates,
        IProgress<LichessDbProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Archive URL is required.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputPath));
        }

        if (minElo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minElo), "Minimum Elo must be non-negative.");
        }

        var outputFullPath = Path.GetFullPath(outputPath);
        if (Path.GetDirectoryName(outputFullPath) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await EnsureDiskSpaceAsync(url, outputFullPath, ct);

        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
        using var countingStream = new CountingStream(networkStream);
        using var decompressor = new DecompressionStream(countingStream);

        await using var outputStream = new FileStream(
            outputFullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);
        var firstGame = true;
        var gamesSeen = 0L;
        var gamesKept = 0L;
        var lastProgressReport = DateTime.MinValue;
        var totalBytes = response.Content.Headers.ContentLength;
        var progressStopwatch = progress != null ? System.Diagnostics.Stopwatch.StartNew() : null;

        progress?.Report(new LichessDbProgress(
            LichessDbProgressStage.Downloading,
            countingStream.BytesRead,
            totalBytes,
            gamesSeen,
            gamesKept,
            progressStopwatch?.Elapsed ?? TimeSpan.Zero));

        await foreach (var game in _pgnReader.ReadGamesAsync(decompressor, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            gamesSeen++;

            if (ShouldKeepGame(game, minElo, excludeBullet, excludeNonStandard, onlyCheckmates))
            {
                if (!firstGame)
                {
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }

                await _pgnWriter.WriteGameAsync(writer, game, CancellationToken.None).ConfigureAwait(false);
                firstGame = false;
                gamesKept++;
            }

            if (progress != null && ShouldReportProgress(gamesSeen, ref lastProgressReport))
            {
                var stage = totalBytes.HasValue && countingStream.BytesRead < totalBytes.Value
                    ? LichessDbProgressStage.Downloading
                    : LichessDbProgressStage.Filtering;

                progress.Report(new LichessDbProgress(
                    stage,
                    countingStream.BytesRead,
                    totalBytes,
                    gamesSeen,
                    gamesKept,
                    progressStopwatch?.Elapsed ?? TimeSpan.Zero));
            }
        }

        await writer.FlushAsync().ConfigureAwait(false);

        progress?.Report(new LichessDbProgress(
            LichessDbProgressStage.Completed,
            countingStream.BytesRead,
            totalBytes,
            gamesSeen,
            gamesKept,
            progressStopwatch?.Elapsed ?? TimeSpan.Zero));
    }

    private async Task EnsureDiskSpaceAsync(string url, string outputPath, CancellationToken ct)
    {
        long? compressedSize = null;

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await HttpClient.SendAsync(headRequest, ct);
            if (headResponse.IsSuccessStatusCode)
            {
                compressedSize = headResponse.Content.Headers.ContentLength;
            }
        }
        catch
        {
            return;
        }

        if (!compressedSize.HasValue || compressedSize.Value <= 0)
        {
            return;
        }

        var root = Path.GetPathRoot(outputPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return;
            }

            var estimatedUncompressed = (long)(compressedSize.Value * EstimatedCompressionRatio);
            if (drive.AvailableFreeSpace < estimatedUncompressed)
            {
                var neededGiB = estimatedUncompressed / 1024 / 1024 / 1024;
                var availableGiB = drive.AvailableFreeSpace / 1024 / 1024 / 1024;
                throw new IOException(
                    $"Insufficient disk space. Need ~{neededGiB} GB, but only {availableGiB} GB available.");
            }
        }
        catch
        {
            return;
        }
    }

    private static bool ShouldKeepGame(
        PgnGame game,
        int minElo,
        bool excludeBullet,
        bool excludeNonStandard,
        bool onlyCheckmates)
    {
        if (minElo > 0)
        {
            if (!TryParseElo(game.Headers, out var whiteElo, out var blackElo) ||
                (whiteElo < minElo && blackElo < minElo))
            {
                return false;
            }
        }

        if (excludeNonStandard && !IsStandardVariant(game.Headers))
        {
            return false;
        }

        if (excludeBullet && IsBulletTimeControl(game.Headers))
        {
            return false;
        }

        if (onlyCheckmates && !IsCheckmateGame(game))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseElo(
        IReadOnlyDictionary<string, string> headers,
        out int whiteElo,
        out int blackElo)
    {
        var hasWhite = TryParsePositiveElo(headers, "WhiteElo", out whiteElo);
        var hasBlack = TryParsePositiveElo(headers, "BlackElo", out blackElo);
        return hasWhite || hasBlack;
    }

    private static bool TryParsePositiveElo(
        IReadOnlyDictionary<string, string> headers,
        string key,
        out int value)
    {
        value = 0;
        if (headers.TryGetHeaderValue(key, out var raw) &&
            int.TryParse(raw, out var parsed) &&
            parsed > 0)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static bool IsStandardVariant(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetHeaderValue("Variant", out var variant) || string.IsNullOrWhiteSpace(variant))
        {
            return true;
        }

        var normalized = variant.Trim();
        return normalized.Equals("Standard", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Chess", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("From Position", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBulletTimeControl(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetHeaderValue("TimeControl", out var timeControl) ||
            string.IsNullOrWhiteSpace(timeControl))
        {
            return false;
        }

        if (!TryParseLeadingInt(timeControl.AsSpan(), out var seconds))
        {
            return false;
        }

        return seconds < 180;
    }

    private static bool TryParseLeadingInt(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        var length = 0;
        while (length < text.Length && char.IsDigit(text[length]))
        {
            length++;
        }

        if (length == 0)
        {
            return false;
        }

        return int.TryParse(text[..length], out value);
    }

    private static bool IsCheckmateGame(PgnGame game)
    {
        if (game.Headers.TryGetHeaderValue("Termination", out var termination) &&
            termination.Contains("checkmate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(game.MoveText) && game.MoveText.Contains('#');
    }

    private static bool ShouldReportProgress(long gamesSeen, ref DateTime lastReportUtc)
    {
        if (gamesSeen <= 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - lastReportUtc < ProgressTimeInterval &&
            gamesSeen % ProgressGameInterval != 0)
        {
            return false;
        }

        lastReportUtc = now;
        return true;
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner;

        public long BytesRead { get; private set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}

public enum LichessDbProgressStage
{
    Downloading,
    Filtering,
    Completed
}

public sealed record LichessDbProgress(
    LichessDbProgressStage Stage,
    long BytesRead,
    long? TotalBytes,
    long GamesSeen,
    long GamesKept,
    TimeSpan Elapsed);
