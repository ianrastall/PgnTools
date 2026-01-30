using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;

namespace PgnTools.Services;

/// <summary>
/// Split strategies supported by the PGN splitter.
/// </summary>
public enum PgnSplitStrategy
{
    Chunk,
    Event,
    Site,
    Eco,
    Date
}

/// <summary>
/// Date precision options for PGN splitting.
/// </summary>
public enum PgnDatePrecision
{
    Century,
    Decade,
    Year,
    Month,
    Day
}

/// <summary>
/// Result summary for a split operation.
/// </summary>
public sealed record PgnSplitResult(long Games, int FilesCreated);

/// <summary>
/// Interface for splitting PGN files into multiple outputs.
/// </summary>
public interface IPgnSplitterService
{
    Task<PgnSplitResult> SplitAsync(
        string inputFilePath,
        string outputDirectory,
        PgnSplitStrategy strategy,
        int chunkSize = 1000,
        PgnDatePrecision datePrecision = PgnDatePrecision.Year,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service that splits PGN files by chunk size, event, or site.
/// </summary>
public class PgnSplitterService : IPgnSplitterService
{
    private const int BufferSize = 65536;
    private const int MaxOpenWriters = 50;
    private const int SplitQueueCapacity = 128;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public PgnSplitterService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task<PgnSplitResult> SplitAsync(
        string inputFilePath,
        string outputDirectory,
        PgnSplitStrategy strategy,
        int chunkSize = 1000,
        PgnDatePrecision datePrecision = PgnDatePrecision.Year,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new ArgumentException("Input file path is required.", nameof(inputFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        }

        if (chunkSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be at least 1.");
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputDirectory);

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        Directory.CreateDirectory(outputFullPath);

        var filesCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = 0L;

        switch (strategy)
        {
            case PgnSplitStrategy.Chunk:
                games = await SplitByChunkAsync(inputFullPath, outputFullPath, chunkSize, filesCreated, progress, cancellationToken);
                break;
            case PgnSplitStrategy.Event:
                games = await SplitByHeaderAsync(inputFullPath, outputFullPath, "Event", filesCreated, progress, cancellationToken);
                break;
            case PgnSplitStrategy.Site:
                games = await SplitByHeaderAsync(inputFullPath, outputFullPath, "Site", filesCreated, progress, cancellationToken);
                break;
            case PgnSplitStrategy.Eco:
                games = await SplitByHeaderAsync(inputFullPath, outputFullPath, "ECO", filesCreated, progress, cancellationToken);
                break;
            case PgnSplitStrategy.Date:
                games = await SplitByDateAsync(inputFullPath, outputFullPath, datePrecision, filesCreated, progress, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported split strategy.");
        }

        return new PgnSplitResult(games, filesCreated.Count);
    }

    private async Task<long> SplitByChunkAsync(
        string inputFilePath,
        string outputDirectory,
        int chunkSize,
        HashSet<string> filesCreated,
        IProgress<(long games, string message)>? progress,
        CancellationToken cancellationToken)
    {
        var games = 0L;
        StreamWriter? writer = null;
        var chunkIndex = 1;
        var chunkGameCount = 0;
        var lastProgressReport = DateTime.MinValue;

        try
        {
            await foreach (var game in _pgnReader.ReadGamesAsync(inputFilePath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (writer == null || chunkGameCount == 0)
                {
                    if (writer != null)
                    {
                        await writer.FlushAsync();
                        writer.Dispose();
                    }

                    var fileName = $"games_chunk_{chunkIndex:0000}.pgn";
                    var filePath = Path.Combine(outputDirectory, fileName);
                    filesCreated.Add(filePath);
                    writer = CreateWriter(filePath, FileMode.Create);
                    chunkIndex++;
                }

                if (chunkGameCount > 0)
                {
                    await writer.WriteLineAsync();
                }

                await _pgnWriter.WriteGameAsync(writer, game, cancellationToken);
                chunkGameCount++;
                games++;

                if (chunkGameCount >= chunkSize)
                {
                    chunkGameCount = 0;
                }

                if (ShouldReportProgress(games, ref lastProgressReport))
                {
                    progress?.Report((games, $"Processing Game {games:N0}..."));
                }
            }
        }
        finally
        {
            if (writer != null)
            {
                await writer.FlushAsync();
                writer.Dispose();
            }
        }

        return games;
    }

    private async Task<long> SplitByHeaderAsync(
        string inputFilePath,
        string outputDirectory,
        string headerKey,
        HashSet<string> filesCreated,
        IProgress<(long games, string message)>? progress,
        CancellationToken cancellationToken)
    {
        return await SplitWithChannelAsync(
                inputFilePath,
                outputDirectory,
                filesCreated,
                progress,
                cancellationToken,
                game =>
                {
                    var rawValue = game.Headers.TryGetHeaderValue(headerKey, out var headerValue)
                        ? headerValue
                        : "Unknown";
                    var safeValue = SanitizeFileName(rawValue);
                    return $"{safeValue}.pgn";
                })
            .ConfigureAwait(false);
    }

    private async Task<long> SplitByDateAsync(
        string inputFilePath,
        string outputDirectory,
        PgnDatePrecision precision,
        HashSet<string> filesCreated,
        IProgress<(long games, string message)>? progress,
        CancellationToken cancellationToken)
    {
        return await SplitWithChannelAsync(
                inputFilePath,
                outputDirectory,
                filesCreated,
                progress,
                cancellationToken,
                game =>
                {
                    var rawDate = game.Headers.TryGetHeaderValue("Date", out var dateValue)
                        ? dateValue
                        : "????.??.??";
                    return GenerateDateFileName(rawDate, precision);
                })
            .ConfigureAwait(false);
    }

    private async Task<long> SplitWithChannelAsync(
        string inputFilePath,
        string outputDirectory,
        HashSet<string> filesCreated,
        IProgress<(long games, string message)>? progress,
        CancellationToken cancellationToken,
        Func<PgnGame, string> fileNameSelector)
    {
        var writers = new LruWriterCache(MaxOpenWriters, StringComparer.OrdinalIgnoreCase);
        var channel = Channel.CreateBounded<SplitWorkItem>(new BoundedChannelOptions(SplitQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        var consumerTask = ConsumeSplitChannelAsync(channel.Reader, writers, progress, cancellationToken);
        Exception? producerException = null;

        try
        {
            await foreach (var game in _pgnReader.ReadGamesAsync(inputFilePath, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = fileNameSelector(game);
                var filePath = Path.Combine(outputDirectory, fileName);
                filesCreated.Add(filePath);

                await channel.Writer.WriteAsync(new SplitWorkItem(filePath, game), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            producerException = ex;
        }
        finally
        {
            channel.Writer.TryComplete(producerException);
        }

        try
        {
            var games = await consumerTask.ConfigureAwait(false);
            if (producerException != null)
            {
                ExceptionDispatchInfo.Capture(producerException).Throw();
            }

            return games;
        }
        finally
        {
            await writers.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<long> ConsumeSplitChannelAsync(
        ChannelReader<SplitWorkItem> reader,
        LruWriterCache writers,
        IProgress<(long games, string message)>? progress,
        CancellationToken cancellationToken)
    {
        var games = 0L;
        var lastProgressReport = DateTime.MinValue;

        await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var cachedWriter = await writers.GetOrCreateAsync(
                    item.FilePath,
                    () =>
                    {
                        var needsSeparator = File.Exists(item.FilePath) && new FileInfo(item.FilePath).Length > 0;
                        return new CachedWriter(CreateWriter(item.FilePath, FileMode.Append), needsSeparator);
                    })
                .ConfigureAwait(false);

            if (cachedWriter.NeedsSeparator)
            {
                await cachedWriter.Writer.WriteLineAsync().ConfigureAwait(false);
            }

            await _pgnWriter.WriteGameAsync(cachedWriter.Writer, item.Game, cancellationToken).ConfigureAwait(false);
            cachedWriter.NeedsSeparator = true;
            games++;

            if (ShouldReportProgress(games, ref lastProgressReport))
            {
                progress?.Report((games, $"Processing Game {games:N0}..."));
            }
        }

        return games;
    }

    private static StreamWriter CreateWriter(string filePath, FileMode mode)
    {
        var stream = new FileStream(
            filePath,
            mode,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        return new StreamWriter(stream, new UTF8Encoding(false), BufferSize, leaveOpen: false);
    }

    private static bool ShouldReportProgress(long games, ref DateTime lastReportUtc)
    {
        if (games <= 0)
        {
            return false;
        }

        if (games != 1 && games % ProgressGameInterval != 0)
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

    private sealed class CachedWriter
    {
        public CachedWriter(StreamWriter writer, bool needsSeparator)
        {
            Writer = writer;
            NeedsSeparator = needsSeparator;
        }

        public StreamWriter Writer { get; }
        public bool NeedsSeparator { get; set; }
    }

    private sealed class LruWriterCache : IAsyncDisposable
    {
        private readonly int _maxOpenWriters;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache;
        private readonly LinkedList<CacheEntry> _lruList;

        public LruWriterCache(int maxOpenWriters, IEqualityComparer<string> comparer)
        {
            _maxOpenWriters = maxOpenWriters;
            _cache = new Dictionary<string, LinkedListNode<CacheEntry>>(comparer);
            _lruList = new LinkedList<CacheEntry>();
        }

        public async Task<CachedWriter> GetOrCreateAsync(string filePath, Func<CachedWriter> createWriter)
        {
            if (_cache.TryGetValue(filePath, out var existing))
            {
                _lruList.Remove(existing);
                _lruList.AddLast(existing);
                return existing.Value.Writer;
            }

            if (_cache.Count >= _maxOpenWriters)
            {
                var node = _lruList.First;
                if (node != null)
                {
                    _lruList.RemoveFirst();
                    _cache.Remove(node.Value.FilePath);
                    await node.Value.Writer.Writer.FlushAsync().ConfigureAwait(false);
                    await node.Value.Writer.Writer.DisposeAsync().ConfigureAwait(false);
                }
            }

            var writer = createWriter();
            var entry = new CacheEntry(filePath, writer);
            var newNode = new LinkedListNode<CacheEntry>(entry);
            _lruList.AddLast(newNode);
            _cache[filePath] = newNode;
            return writer;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAllAsync().ConfigureAwait(false);
        }

        private async Task DisposeAllAsync()
        {
            foreach (var entry in _lruList)
            {
                await entry.Writer.Writer.FlushAsync().ConfigureAwait(false);
                await entry.Writer.Writer.DisposeAsync().ConfigureAwait(false);
            }

            _lruList.Clear();
            _cache.Clear();
        }

        private sealed record CacheEntry(string FilePath, CachedWriter Writer);
    }

    private sealed record SplitWorkItem(string FilePath, PgnGame Game);

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var c in value.Trim())
        {
            builder.Append(invalidChars.Contains(c) ? '_' : c);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    private static string GenerateDateFileName(string rawDate, PgnDatePrecision precision)
    {
        var normalized = string.IsNullOrWhiteSpace(rawDate)
            ? "????.??.??"
            : rawDate.Replace('?', 'x');

        var parts = normalized.Split('.');
        var year = parts.Length > 0 ? parts[0] : "xxxx";
        var month = parts.Length > 1 ? parts[1] : "xx";
        var day = parts.Length > 2 ? parts[2] : "xx";

        year = NormalizeYear(year);
        month = NormalizeDatePart(month, 2);
        day = NormalizeDatePart(day, 2);

        var fileName = precision switch
        {
            PgnDatePrecision.Century => $"{year[..2]}xx.pgn",
            PgnDatePrecision.Decade => $"{year[..3]}x.pgn",
            PgnDatePrecision.Year => $"{year}.pgn",
            PgnDatePrecision.Month => $"{year}-{month}.pgn",
            PgnDatePrecision.Day => $"{year}-{month}-{day}.pgn",
            _ => $"{year}.pgn"
        };

        return SanitizeFileName(fileName);
    }

    private static string NormalizeYear(string year)
    {
        if (string.IsNullOrWhiteSpace(year))
        {
            return "xxxx";
        }

        var trimmed = year.Trim();
        if (trimmed.Length >= 4)
        {
            return trimmed[..4];
        }

        if (trimmed.All(char.IsDigit))
        {
            return trimmed.PadLeft(4, '0');
        }

        return trimmed.PadRight(4, 'x');
    }

    private static string NormalizeDatePart(string part, int length)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            return new string('x', length);
        }

        var trimmed = part.Trim();
        if (trimmed.Length >= length)
        {
            return trimmed[..length];
        }

        if (trimmed.All(char.IsDigit))
        {
            return trimmed.PadLeft(length, '0');
        }

        return trimmed.PadRight(length, 'x');
    }
}
