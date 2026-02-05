using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public interface ICategoryTaggerService
{
    Task TagCategoriesAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tags tournaments with a computed FIDE event category based on average Elo.
/// </summary>
public sealed class CategoryTaggerService : ICategoryTaggerService
{
    private const int BufferSize = 65536;
    private const int MinGamesThreshold = 6;
    private const double MinGamesPerPlayer = 0.6;
    private const int ProgressGameInterval = 500;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(200);

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    private readonly record struct TournamentKey(
        string Event,
        string Site,
        string EventDate,
        string Section,
        string Stage);

    private sealed class TournamentKeyComparer : IEqualityComparer<TournamentKey>
    {
        public static TournamentKeyComparer Instance { get; } = new();

        public bool Equals(TournamentKey x, TournamentKey y)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(x.Event, y.Event) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Site, y.Site) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.EventDate, y.EventDate) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Section, y.Section) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.Stage, y.Stage);
        }

        public int GetHashCode(TournamentKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Event, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Site, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.EventDate, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Section, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Stage, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private sealed class TournamentData
    {
        public HashSet<string> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PlayerRatings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int GameCount { get; set; }

        public int? CalculateCategory()
        {
            if (GameCount < MinGamesThreshold) return null;
            if (Players.Count < 2) return null;

            var avgGamesPerPlayer = (2.0 * GameCount) / Players.Count;
            if (avgGamesPerPlayer < MinGamesPerPlayer) return null;

            var validRatings = PlayerRatings.Values.Where(r => r > 0).ToList();
            if (validRatings.Count == 0) return null;

            var avgRating = validRatings.Average();
            if (avgRating < 2251) return null;

            return 1 + (int)Math.Floor((avgRating - 2251) / 25.0);
        }
    }

    public CategoryTaggerService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        ArgumentNullException.ThrowIfNull(pgnReader);
        ArgumentNullException.ThrowIfNull(pgnWriter);
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task TagCategoriesAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new ArgumentException("Input file path is required.", nameof(inputFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFilePath));
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputFilePath);
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Input and output files must be different.");
        }

        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var (stats, totalGames) = await AnalyzeStatsAsync(inputFullPath, cancellationToken).ConfigureAwait(false);

        var categories = new Dictionary<TournamentKey, int>(TournamentKeyComparer.Instance);
        foreach (var entry in stats)
        {
            var category = entry.Value.CalculateCategory();
            if (category.HasValue)
            {
                categories[entry.Key] = category.Value;
            }
        }

        try
        {
            var processedGames = 0L;
            var firstOutput = true;
            var lastProgressReport = DateTime.UtcNow;

            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: false))
            {
                await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processedGames++;

                    if (TryBuildTournamentKey(game.Headers, out var tournamentKey) &&
                        categories.TryGetValue(tournamentKey, out var category))
                    {
                        const string categoryKey = "EventCategory";
                        game.Headers[categoryKey] = category.ToString();
                        EnsureHeaderOrder(game.HeaderOrder, categoryKey, insertAfterKey: "Event");
                    }

                    if (!firstOutput)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await _pgnWriter.WriteGameAsync(writer, game, cancellationToken).ConfigureAwait(false);
                    firstOutput = false;

                    if (totalGames > 0 && ShouldReportProgress(processedGames, ref lastProgressReport))
                    {
                        var percent = Math.Clamp((double)processedGames / totalGames * 100.0, 0, 100);
                        progress?.Report(percent);
                    }
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }

            await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(100);
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

    private async Task<(Dictionary<TournamentKey, TournamentData> Stats, long TotalGames)> AnalyzeStatsAsync(
        string inputFilePath,
        CancellationToken cancellationToken)
    {
        var stats = new Dictionary<TournamentKey, TournamentData>(TournamentKeyComparer.Instance);
        var totalGames = 0L;

        await foreach (var game in _pgnReader.ReadGamesAsync(inputFilePath, readMoveText: false, cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalGames++;

            if (!TryBuildTournamentKey(game.Headers, out var tournamentKey))
            {
                continue;
            }

            if (!stats.TryGetValue(tournamentKey, out var data))
            {
                data = new TournamentData();
                stats[tournamentKey] = data;
            }

            data.GameCount++;

            if (game.Headers.TryGetHeaderValue("White", out var white) && !string.IsNullOrWhiteSpace(white))
            {
                var whiteName = white.Trim();
                if (!string.IsNullOrEmpty(whiteName))
                {
                    data.Players.Add(whiteName);
                    if (TryParseElo(game.Headers, "WhiteElo", out var rating))
                    {
                        TryAddRating(data, whiteName, rating);
                    }
                }
            }

            if (game.Headers.TryGetHeaderValue("Black", out var black) && !string.IsNullOrWhiteSpace(black))
            {
                var blackName = black.Trim();
                if (!string.IsNullOrEmpty(blackName))
                {
                    data.Players.Add(blackName);
                    if (TryParseElo(game.Headers, "BlackElo", out var rating))
                    {
                        TryAddRating(data, blackName, rating);
                    }
                }
            }
        }

        return (stats, totalGames);
    }

    private static bool TryParseElo(IReadOnlyDictionary<string, string> headers, string key, out int rating)
    {
        rating = 0;
        if (!headers.TryGetHeaderValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        var length = 0;
        while (length < span.Length && char.IsDigit(span[length]))
        {
            length++;
        }

        if (length == 0)
        {
            return false;
        }

        return int.TryParse(span[..length], out rating) && rating > 0;
    }

    private static void TryAddRating(TournamentData data, string player, int rating)
    {
        if (data.PlayerRatings.TryGetValue(player, out var existing))
        {
            if (rating > existing)
            {
                data.PlayerRatings[player] = rating;
            }
            return;
        }

        data.PlayerRatings[player] = rating;
    }

    private static bool TryBuildTournamentKey(IReadOnlyDictionary<string, string> headers, out TournamentKey key)
    {
        var eventName = headers.GetHeaderValueOrDefault("Event", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(eventName))
        {
            key = default;
            return false;
        }

        var site = headers.GetHeaderValueOrDefault("Site", string.Empty).Trim();
        var eventDate = headers.GetHeaderValueOrDefault("EventDate", string.Empty).Trim();
        var date = headers.GetHeaderValueOrDefault("Date", string.Empty).Trim();
        var section = headers.GetHeaderValueOrDefault("Section", string.Empty).Trim();
        var stage = headers.GetHeaderValueOrDefault("Stage", string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(eventDate))
        {
            eventDate = date;
        }

        key = new TournamentKey(eventName, site, eventDate, section, stage);
        return true;
    }

    private static void EnsureHeaderOrder(List<string> order, string key, string? insertAfterKey = null)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(insertAfterKey))
        {
            for (var i = 0; i < order.Count; i++)
            {
                if (string.Equals(order[i], insertAfterKey, StringComparison.OrdinalIgnoreCase))
                {
                    order.Insert(i + 1, key);
                    return;
                }
            }
        }

        order.Add(key);
    }

    private static bool ShouldReportProgress(long processedGames, ref DateTime lastReportUtc)
    {
        if (processedGames % ProgressGameInterval == 0)
        {
            lastReportUtc = DateTime.UtcNow;
            return true;
        }

        var now = DateTime.UtcNow;
        if (now - lastReportUtc >= ProgressTimeInterval)
        {
            lastReportUtc = now;
            return true;
        }

        return false;
    }
}
