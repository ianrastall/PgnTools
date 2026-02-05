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

        var categories = stats
            .Select(kv => (Event: kv.Key, Category: kv.Value.CalculateCategory()))
            .Where(x => x.Category.HasValue)
            .ToDictionary(x => x.Event, x => x.Category!.Value, StringComparer.OrdinalIgnoreCase);

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

                    var tournamentKey = BuildTournamentKey(game.Headers);
                    if (!string.IsNullOrWhiteSpace(tournamentKey) &&
                        categories.TryGetValue(tournamentKey, out var category))
                    {
                        game.Headers["EventCategory"] = category.ToString();
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

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
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

    private async Task<(Dictionary<string, TournamentData> Stats, long TotalGames)> AnalyzeStatsAsync(
        string inputFilePath,
        CancellationToken cancellationToken)
    {
        var stats = new Dictionary<string, TournamentData>(StringComparer.OrdinalIgnoreCase);
        var totalGames = 0L;

        await foreach (var game in _pgnReader.ReadGamesAsync(inputFilePath, readMoveText: false, cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalGames++;

            var tournamentKey = BuildTournamentKey(game.Headers);
            if (string.IsNullOrWhiteSpace(tournamentKey))
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
                data.Players.Add(white);
                if (TryParseElo(game.Headers, "WhiteElo", out var rating))
                {
                    TryAddRating(data, white, rating);
                }
            }

            if (game.Headers.TryGetHeaderValue("Black", out var black) && !string.IsNullOrWhiteSpace(black))
            {
                data.Players.Add(black);
                if (TryParseElo(game.Headers, "BlackElo", out var rating))
                {
                    TryAddRating(data, black, rating);
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
        if (!data.PlayerRatings.ContainsKey(player))
        {
            data.PlayerRatings[player] = rating;
        }
    }

    private static string BuildTournamentKey(IReadOnlyDictionary<string, string> headers)
    {
        var eventName = headers.GetHeaderValueOrDefault("Event", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return string.Empty;
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

        return string.Join(" | ",
            eventName,
            site,
            eventDate,
            section,
            stage);
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
