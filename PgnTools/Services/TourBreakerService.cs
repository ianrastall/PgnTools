using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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
/// Splits a PGN database into tournament files based on event/site/year filters.
/// </summary>
public sealed class TourBreakerService : ITourBreakerService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxSlugLength = 80;
    private const int MaxOpenWriters = 64;
    private static readonly Regex SlugRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

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
        public DateOnly? MinDate { get; set; }
        public DateOnly? MaxDate { get; set; }
    }

    private sealed class WriterCache : IAsyncDisposable
    {
        private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
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
            if (_writers.TryGetValue(path, out var existing))
            {
                Touch(path);
                return existing;
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

            _writers[path] = writer;
            _lru.AddLast(path);
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

        private void Touch(string path)
        {
            var node = _lru.Find(path);
            if (node != null)
            {
                _lru.Remove(node);
                _lru.AddLast(node);
            }
            else
            {
                _lru.AddLast(path);
            }
        }

        private async Task EvictOldestAsync(CancellationToken cancellationToken)
        {
            var node = _lru.First;
            if (node == null)
            {
                return;
            }

            var path = node.Value;
            _lru.RemoveFirst();
            if (_writers.TryGetValue(path, out var writer))
            {
                _writers.Remove(path);
                try
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var writer in _writers.Values)
            {
                await writer.FlushAsync().ConfigureAwait(false);
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            _writers.Clear();
            _lru.Clear();
            _hasContent.Clear();
            _createdPaths.Clear();
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
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        Directory.CreateDirectory(outputFullPath);

        // Pass 1: scan tournaments
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

            if (game.Headers.TryGetHeaderValue("Black", out var black) && !string.IsNullOrWhiteSpace(black))
            {
                meta.Players.Add(black);
            }

            if (TryParseElo(game.Headers, "WhiteElo", out var whiteElo) && whiteElo < minElo)
            {
                meta.AllMeetElo = false;
            }

            if (TryParseElo(game.Headers, "BlackElo", out var blackElo) && blackElo < minElo)
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

        // Filter valid tournaments
        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in tournaments)
        {
            var playerMin = Math.Max(1, kv.Value.Players.Count / 2);
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

        // Pass 2: write filtered tournaments
        var processedGames = 0L;
        var lastProgressReport = DateTime.MinValue;

        await using var writerCache = new WriterCache(MaxOpenWriters);

        await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedGames++;

            var key = GetKey(game);
            if (!validKeys.Contains(key))
            {
                if (ShouldReportProgress(processedGames, ref lastProgressReport))
                {
                    ReportProgress(progress, processedGames, totalGames);
                }
                continue;
            }

            var meta = tournaments[key];
            var filename = GenerateFilename(meta, key);
            var path = Path.Combine(outputFullPath, filename);

            var needsSeparator = writerCache.HasContent(path);

            var writer = await writerCache.GetWriterAsync(path, cancellationToken).ConfigureAwait(false);

            if (needsSeparator)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await _pgnWriter.WriteGameAsync(writer, game, cancellationToken).ConfigureAwait(false);
            writerCache.MarkWritten(path);

            if (ShouldReportProgress(processedGames, ref lastProgressReport))
            {
                ReportProgress(progress, processedGames, totalGames);
            }
        }

        progress?.Report(100);
        return validKeys.Count;
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

    private static bool TryParseElo(IReadOnlyDictionary<string, string> headers, string key, out int elo)
    {
        if (headers.TryGetHeaderValue(key, out var value) && int.TryParse(value, out var parsed))
        {
            elo = parsed;
            return true;
        }

        elo = 0;
        return false;
    }

    private static string GetKey(PgnGame game, out string eventName, out string siteName, out string year)
    {
        eventName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Event", "?"));
        siteName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Site", "?"));
        year = ExtractYear(game.Headers.GetHeaderValueOrDefault("Date", "????.??.??"));
        return BuildKey(eventName, siteName, year);
    }

    private static string GetKey(PgnGame game)
    {
        var eventName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Event", "?"));
        var siteName = NormalizeKeyPart(game.Headers.GetHeaderValueOrDefault("Site", "?"));
        var year = ExtractYear(game.Headers.GetHeaderValueOrDefault("Date", "????.??.??"));
        return BuildKey(eventName, siteName, year);
    }

    private static string BuildKey(string eventName, string siteName, string year)
    {
        return $"{eventName}|{siteName}|{year}";
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

        return DateOnly.TryParseExact(raw, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParseExact(raw, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string ExtractYear(string raw)
    {
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

        var trimmed = value.Trim();
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
        }

        return slug;
    }

    private static string GetKeyHash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static bool ShouldReportProgress(long games, ref DateTime lastReportUtc)
    {
        if (games <= 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var gameIntervalMet = games == 1 || games % ProgressGameInterval == 0;
        var timeIntervalMet = now - lastReportUtc >= ProgressTimeInterval;
        if (!gameIntervalMet && !timeIntervalMet)
        {
            return false;
        }

        lastReportUtc = now;
        return true;
    }
}
