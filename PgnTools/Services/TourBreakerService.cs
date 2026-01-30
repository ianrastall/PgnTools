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

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    private sealed class TourMeta
    {
        public string Name { get; set; } = string.Empty;
        public int Games { get; set; }
        public HashSet<string> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AllMeetElo { get; set; } = true;
        public string MinDate { get; set; } = "9999.99.99";
        public string MaxDate { get; set; } = "0000.00.00";
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

        await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalGames++;

            var key = GetKey(game);
            if (!tournaments.TryGetValue(key, out var meta))
            {
                meta = new TourMeta
                {
                    Name = game.Headers.GetHeaderValueOrDefault("Event", "?")
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

            var whiteElo = TryParseElo(game.Headers, "WhiteElo");
            var blackElo = TryParseElo(game.Headers, "BlackElo");
            if (whiteElo < minElo || blackElo < minElo)
            {
                meta.AllMeetElo = false;
            }

            var date = game.Headers.GetHeaderValueOrDefault("Date", "????.??.??");
            if (string.Compare(date, meta.MinDate, StringComparison.Ordinal) < 0)
            {
                meta.MinDate = date;
            }

            if (string.Compare(date, meta.MaxDate, StringComparison.Ordinal) > 0)
            {
                meta.MaxDate = date;
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

        await foreach (var game in _pgnReader.ReadGamesAsync(inputFullPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedGames++;

            var key = GetKey(game);
            if (!validKeys.Contains(key))
            {
                ReportProgress(progress, processedGames, totalGames);
                continue;
            }

            var meta = tournaments[key];
            var filename = GenerateFilename(meta);
            var path = Path.Combine(outputFullPath, filename);

            var needsSeparator = File.Exists(path) && new FileInfo(path).Length > 0;

            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            using var writer = new StreamWriter(stream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

            if (needsSeparator)
            {
                await writer.WriteLineAsync();
            }

            await _pgnWriter.WriteGameAsync(writer, game, cancellationToken);
            await writer.FlushAsync();

            ReportProgress(progress, processedGames, totalGames);
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

    private static int TryParseElo(IReadOnlyDictionary<string, string> headers, string key)
    {
        return headers.TryGetHeaderValue(key, out var value) && int.TryParse(value, out var elo)
            ? elo
            : 0;
    }

    private static string GetKey(PgnGame game)
    {
        var evt = game.Headers.GetHeaderValueOrDefault("Event", "?");
        var site = game.Headers.GetHeaderValueOrDefault("Site", "?");
        var date = game.Headers.GetHeaderValueOrDefault("Date", "????");
        var year = date.Length >= 4 ? date[..4] : "????";
        return $"{evt}|{site}|{year}";
    }

    private static string GenerateFilename(TourMeta meta)
    {
        var start = meta.MinDate.Replace(".", string.Empty, StringComparison.Ordinal).Replace("?", "0", StringComparison.Ordinal);
        var end = meta.MaxDate.Replace(".", string.Empty, StringComparison.Ordinal).Replace("?", "0", StringComparison.Ordinal);
        var safeName = Regex.Replace(meta.Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "tournament";
        }

        return $"{start}-{end}-{safeName}.pgn";
    }
}
