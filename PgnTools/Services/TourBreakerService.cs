using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PgnTools.Helpers;

namespace PgnTools.Services;

public interface ITourBreakerService
{
    Task<int> BreakTournamentsAsync(
        string inputFilePath,
        string outputDirectory,
        int minElo,
        int minGames,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Splits a PGN database into tournament files based on event/site/date filters.
/// </summary>
public sealed class TourBreakerService : ITourBreakerService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private const long ProgressTimeIntervalMs = 100;
    private const int MaxSlugLength = 80;
    private const int MaxOpenWriters = 64;
    private const char KeySeparator = '\u001F';

    private static readonly Regex SlugRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly string[] FullDateFormats =
    [
        "yyyy.MM.dd",
        "yyyy.M.dd",
        "yyyy.MM.d",
        "yyyy.M.d",
        "yyyy-MM-dd",
        "yyyy-M-dd",
        "yyyy-MM-d",
        "yyyy-M-d",
        "yyyy/MM/dd",
        "yyyy/M/dd",
        "yyyy/MM/d",
        "yyyy/M/d"
    ];

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    private sealed class TourMeta
    {
        public string Name { get; set; } = string.Empty;
        public string Site { get; set; } = string.Empty;
        public string Year { get; set; } = "????";
        public int Games { get; set; }
        public HashSet<string> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AllMeetElo { get; set; } = true;
        public bool HasMissingPlayerNames { get; set; }
        public DateOnly? MinDate { get; set; }
        public DateOnly? MaxDate { get; set; }
    }

    private sealed class WriterCache : IAsyncDisposable
    {
        private sealed class CachedWriter(StreamWriter writer, LinkedListNode<string> node)
        {
            public StreamWriter Writer { get; } = writer;
            public LinkedListNode<string> Node { get; } = node;
        }

        private readonly Dictionary<string, CachedWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, bool> _hasContent = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _createdPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _maxOpen;

        public WriterCache(int maxOpen)
        {
            _maxOpen = maxOpen;
        }

        public async Task<StreamWriter> GetWriterAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_writers.TryGetValue(path, out var existing))
            {
                Touch(existing.Node);
                return existing.Writer;
            }

            if (_writers.Count >= _maxOpen)
            {
                await EvictOldestAsync(cancellationToken).ConfigureAwait(false);
            }

            var mode = _createdPaths.Contains(path) ? FileMode.Append : FileMode.Create;
            var stream = new FileStream(
                path,
                mode,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), BufferSize, leaveOpen: false);
            var node = _lru.AddLast(path);

            _writers[path] = new CachedWriter(writer, node);
            _createdPaths.Add(path);
            return writer;
        }

        public bool HasContent(string path)
        {
            return _hasContent.TryGetValue(path, out var hasContent) && hasContent;
        }

        public void MarkWritten(string path)
        {
            _hasContent[path] = true;
        }

        private void Touch(LinkedListNode<string> node)
        {
            _lru.Remove(node);
            _lru.AddLast(node);
        }

        private async Task EvictOldestAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = _lru.First;
            if (node == null)
            {
                return;
            }

            var path = node.Value;
            _lru.RemoveFirst();
            if (_writers.Remove(path, out var cachedWriter))
            {
                var error = await TryFlushAndDisposeAsync(cachedWriter.Writer, cancellationToken).ConfigureAwait(false);
                if (error != null)
                {
                    throw error;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception>? errors = null;
            foreach (var cachedWriter in _writers.Values)
            {
                var error = await TryFlushAndDisposeAsync(cachedWriter.Writer, CancellationToken.None).ConfigureAwait(false);
                if (error != null)
                {
                    (errors ??= []).Add(error);
                }
            }

            _writers.Clear();
            _lru.Clear();
            _hasContent.Clear();
            _createdPaths.Clear();

            if (errors is { Count: 1 })
            {
                throw errors[0];
            }

            if (errors is { Count: > 1 })
            {
                throw new AggregateException("One or more tournament writers failed to flush or close.", errors);
            }
        }

        private static async Task<Exception?> TryFlushAndDisposeAsync(StreamWriter writer, CancellationToken cancellationToken)
        {
            List<Exception>? errors = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }

            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }

            return errors switch
            {
                null => null,
                { Count: 1 } => errors[0],
                _ => new AggregateException("A tournament writer failed to flush and close.", errors)
            };
        }
    }

    public TourBreakerService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task<int> BreakTournamentsAsync(
        string inputFilePath,
        string outputDirectory,
        int minElo,
        int minGames,
        IProgress<double>? progress = null,
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

        if (minElo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minElo), "Minimum Elo must be non-negative.");
        }

        if (minGames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minGames), "Minimum games must be at least 1.");
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputDirectory);

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException($"Input PGN file not found: {inputFullPath}", inputFullPath);
        }

        await EnsureWritableDirectoryAsync(outputFullPath, cancellationToken).ConfigureAwait(false);

        var tournaments = new Dictionary<string, TourMeta>(StringComparer.OrdinalIgnoreCase);
        var totalGames = 0L;

        await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalGames++;

            var key = GetKey(game, out var eventName, out var siteName, out var year);
            if (!tournaments.TryGetValue(key, out var meta))
            {
                meta = new TourMeta
                {
                    Name = eventName,
                    Site = siteName,
                    Year = year
                };
                tournaments[key] = meta;
            }

            meta.Games++;

            if (game.Headers.TryGetHeaderValue("White", out var white) && !string.IsNullOrWhiteSpace(white))
            {
                meta.Players.Add(white);
            }
            else
            {
                meta.HasMissingPlayerNames = true;
            }

            if (game.Headers.TryGetHeaderValue("Black", out var black) && !string.IsNullOrWhiteSpace(black))
            {
                meta.Players.Add(black);
            }
            else
            {
                meta.HasMissingPlayerNames = true;
            }

            if (!PlayerMeetsElo(game.Headers, "WhiteElo", minElo) ||
                !PlayerMeetsElo(game.Headers, "BlackElo", minElo))
            {
                meta.AllMeetElo = false;
            }

            if (TryParseFullDate(game.Headers.GetHeaderValueOrDefault("Date", string.Empty), out var date))
            {
                if (!meta.MinDate.HasValue || date < meta.MinDate.Value)
                {
                    meta.MinDate = date;
                }

                if (!meta.MaxDate.HasValue || date > meta.MaxDate.Value)
                {
                    meta.MaxDate = date;
                }
            }
        }

        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in tournaments)
        {
            var playerCount = kv.Value.Players.Count;
            if (playerCount < 2 || kv.Value.HasMissingPlayerNames)
            {
                continue;
            }

            var playerMin = Math.Max(1, (playerCount + 1) / 2);
            if (kv.Value.AllMeetElo && kv.Value.Games >= minGames && kv.Value.Games >= playerMin)
            {
                validKeys.Add(kv.Key);
            }
        }

        if (validKeys.Count == 0 || totalGames == 0)
        {
            progress?.Report(100);
            return 0;
        }

        var finalPathsByKey = BuildFinalPaths(validKeys, tournaments, outputFullPath);
        var tempPathsByFinalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var processedGames = 0L;
        var lastProgressReportTick = 0L;
        var commitStarted = false;

        var writerCache = new WriterCache(MaxOpenWriters);
        var writerCacheDisposed = false;

        try
        {
            await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedGames++;

                var key = GetKey(game);
                if (!validKeys.Contains(key))
                {
                    if (ShouldReportProgress(processedGames, ref lastProgressReportTick))
                    {
                        ReportProgress(progress, processedGames, totalGames);
                    }

                    continue;
                }

                var finalPath = finalPathsByKey[key];
                var tempPath = GetOrCreateTempPath(finalPath, tempPathsByFinalPath);
                var needsSeparator = writerCache.HasContent(tempPath);
                var writer = await writerCache.GetWriterAsync(tempPath, cancellationToken).ConfigureAwait(false);

                if (needsSeparator)
                {
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }

                await _pgnWriter.WriteGameAsync(writer, game, cancellationToken).ConfigureAwait(false);
                writerCache.MarkWritten(tempPath);

                if (ShouldReportProgress(processedGames, ref lastProgressReportTick))
                {
                    ReportProgress(progress, processedGames, totalGames);
                }
            }

            await writerCache.DisposeAsync().ConfigureAwait(false);
            writerCacheDisposed = true;

            commitStarted = true;
            await CommitTempOutputsAsync(tempPathsByFinalPath, cancellationToken).ConfigureAwait(false);

            progress?.Report(100);
            return validKeys.Count;
        }
        catch
        {
            if (!writerCacheDisposed)
            {
                await DisposeWriterCacheForFailureAsync(writerCache).ConfigureAwait(false);
                writerCacheDisposed = true;
            }

            if (!commitStarted)
            {
                CleanupTempOutputs(tempPathsByFinalPath.Values);
            }

            throw;
        }
        finally
        {
            if (!writerCacheDisposed)
            {
                await DisposeWriterCacheForFailureAsync(writerCache).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<string, string> BuildFinalPaths(
        HashSet<string> validKeys,
        Dictionary<string, TourMeta> tournaments,
        string outputFullPath)
    {
        var finalPathsByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in validKeys)
        {
            var filename = GenerateFilename(tournaments[key], key);
            var finalPath = Path.Combine(outputFullPath, filename);

            if (pathToKey.TryGetValue(finalPath, out var existingKey) &&
                !string.Equals(existingKey, key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Filename collision between tournament keys '{existingKey}' and '{key}'.");
            }

            pathToKey[finalPath] = key;
            finalPathsByKey[key] = finalPath;
        }

        return finalPathsByKey;
    }

    private static string GetOrCreateTempPath(
        string finalPath,
        Dictionary<string, string> tempPathsByFinalPath)
    {
        if (tempPathsByFinalPath.TryGetValue(finalPath, out var tempPath))
        {
            return tempPath;
        }

        tempPath = FileReplacementHelper.CreateTempFilePath(finalPath);
        tempPathsByFinalPath[finalPath] = tempPath;
        return tempPath;
    }

    private static async Task CommitTempOutputsAsync(
        Dictionary<string, string> tempPathsByFinalPath,
        CancellationToken cancellationToken)
    {
        foreach (var (finalPath, tempPath) in tempPathsByFinalPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await FileReplacementHelper.ReplaceFileAsync(tempPath, finalPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureWritableDirectoryAsync(string outputFullPath, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(outputFullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"Cannot create output directory: {outputFullPath}", ex);
        }

        var testFile = Path.Combine(outputFullPath, $".pgn_tools_write_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(testFile, "test", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"No write access to output directory: {outputFullPath}", ex);
        }
        finally
        {
            TryDelete(testFile);
        }
    }

    private static void CleanupTempOutputs(IEnumerable<string> tempPaths)
    {
        foreach (var tempPath in tempPaths)
        {
            TryDelete(tempPath);
        }
    }

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

    private static async Task DisposeWriterCacheForFailureAsync(WriterCache writerCache)
    {
        try
        {
            await writerCache.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void ReportProgress(IProgress<double>? progress, long processedGames, long totalGames)
    {
        if (progress == null || totalGames <= 0)
        {
            return;
        }

        var percent = Math.Clamp((double)processedGames / totalGames * 100.0, 0, 100);
        progress.Report(percent);
    }

    private static bool PlayerMeetsElo(IReadOnlyDictionary<string, string> headers, string key, int minElo)
    {
        return headers.TryGetHeaderValue(key, out var value) &&
               int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var elo) &&
               elo >= minElo;
    }

    private static string GetKey(PgnGame game, out string eventName, out string siteName, out string year)
    {
        eventName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Event", "?"));
        siteName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Site", "?"));
        year = ExtractYear(game.Headers.GetHeaderValueOrDefault("Date", "????.??.??"));
        var dateKey = ExtractDateKey(game.Headers, year);
        return BuildKey(eventName, siteName, dateKey);
    }

    private static string GetKey(PgnGame game)
    {
        var eventName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Event", "?"));
        var siteName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Site", "?"));
        var year = ExtractYear(game.Headers.GetHeaderValueOrDefault("Date", "????.??.??"));
        var dateKey = ExtractDateKey(game.Headers, year);
        return BuildKey(eventName, siteName, dateKey);
    }

    private static string ExtractDateKey(IReadOnlyDictionary<string, string> headers, string fallbackYear)
    {
        var eventDate = headers.GetHeaderValueOrDefault("EventDate", string.Empty);
        if (!string.IsNullOrWhiteSpace(eventDate) && !eventDate.Contains('?', StringComparison.Ordinal))
        {
            return NormalizeKeyPart(eventDate);
        }

        return fallbackYear;
    }

    private static string BuildKey(string eventName, string siteName, string dateKey)
    {
        return string.Join(KeySeparator, eventName, siteName, dateKey);
    }

    private static string GenerateFilename(TourMeta meta, string key)
    {
        var start = FormatDate(meta.MinDate);
        var end = FormatDate(meta.MaxDate);
        var safeEvent = Slugify(meta.Name, "tournament", MaxSlugLength);
        var safeSite = Slugify(meta.Site, "site", 40);
        var hash = GetKeyHash(key);

        return $"{start}-{end}-{safeEvent}-{safeSite}-{meta.Year}-{hash}.pgn";
    }

    private static string FormatDate(DateOnly? date)
    {
        return date.HasValue
            ? date.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : "00000000";
    }

    private static bool TryParseFullDate(string raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        return DateOnly.TryParseExact(
            raw.Trim(),
            FullDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static string ExtractYear(string raw)
    {
        raw = raw.Trim();
        if (raw.Length >= 4 && int.TryParse(raw.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return year.ToString("D4", CultureInfo.InvariantCulture);
        }

        return "????";
    }

    private static string NormalizeKeyPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        var trimmed = value.Trim().Replace('|', ' ').Replace(KeySeparator, ' ');
        return WhitespaceRegex.Replace(trimmed, " ");
    }

    private static string Slugify(string value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var slug = SlugRegex.Replace(value.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return fallback;
        }

        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].Trim('-');
            if (string.IsNullOrWhiteSpace(slug))
            {
                return fallback;
            }
        }

        return slug;
    }

    private static string GetKeyHash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static bool ShouldReportProgress(long games, ref long lastReportTick)
    {
        if (games <= 0)
        {
            return false;
        }

        var currentTick = Environment.TickCount64;
        var gameIntervalMet = games == 1 || games % ProgressGameInterval == 0;
        var timeIntervalMet = currentTick - lastReportTick >= ProgressTimeIntervalMs;
        if (!gameIntervalMet && !timeIntervalMet)
        {
            return false;
        }

        lastReportTick = currentTick;
        return true;
    }
}
