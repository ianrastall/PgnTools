// PGNTOOLS-LC0-BEGIN
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PgnTools.Services;

public enum Lc0DownloadPhase
{
    Scraping,
    Downloading,
    Processing,
    Completed
}

public sealed record Lc0DownloadOptions(
    string OutputFilePath,
    DateOnly ArchiveMonth,
    bool ExcludeNonStandard,
    bool OnlyCheckmates);

public sealed record Lc0DownloadProgress(
    Lc0DownloadPhase Phase,
    string Message,
    int? Current = null,
    int? Total = null,
    double? Percent = null);

public sealed record Lc0DownloadResult(
    int TotalMatches,
    int ProcessedMatches,
    int FailedMatches,
    long GamesSeen,
    long GamesKept);

public interface ILc0DownloaderService
{
    Task<Lc0DownloadResult> DownloadAndProcessAsync(
        Lc0DownloadOptions options,
        IProgress<Lc0DownloadProgress> progress,
        CancellationToken ct = default);
}

public sealed partial class Lc0DownloaderService : ILc0DownloaderService
{
    private const int BufferSize = 65536;
    private const int MaxRetries = 3;
    private const int EmptyPageLimit = 2;
    private const int MaxScrapePages = 5000;
    private static readonly Uri MatchesBaseUri = new("https://training.lczero.org/matches/");
    private static readonly Uri StorageBaseUri = new("https://storage.lczero.org/files/match_pgns/");
    private static readonly HttpClient HttpClient = CreateClient();
    private static readonly Random RandomJitter = new();

    private static readonly (DateTime Date, string Version)[] VersionMap =
    [
        (new DateTime(2025, 1, 1), "v0.32.0"),
        (new DateTime(2024, 6, 1), "v0.31.0"),
        (new DateTime(2023, 7, 1), "v0.30.0"),
        (new DateTime(2023, 1, 1), "v0.29.0"),
        (new DateTime(2022, 1, 1), "v0.28.0"),
        (new DateTime(2021, 1, 1), "v0.27.0"),
        (new DateTime(2020, 1, 1), "v0.26.0"),
        (new DateTime(2019, 1, 1), "v0.25.0"),
        (new DateTime(2018, 1, 1), "v0.24.0"),
        (new DateTime(1970, 1, 1), "v0.23.0")
    ];

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public Lc0DownloaderService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task<Lc0DownloadResult> DownloadAndProcessAsync(
        Lc0DownloadOptions options,
        IProgress<Lc0DownloadProgress> progress,
        CancellationToken ct = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(options.OutputFilePath));
        }

        var outputPath = Path.GetFullPath(options.OutputFilePath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("Output file path must include a valid directory.");
        }

        Directory.CreateDirectory(outputDirectory);

        var monthStart = new DateTime(options.ArchiveMonth.Year, options.ArchiveMonth.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        progress.Report(new Lc0DownloadProgress(
            Lc0DownloadPhase.Scraping,
            $"Scraping match metadata for {options.ArchiveMonth:yyyy-MM}..."));

        var allMatches = await ScrapeMatchesAsync(monthStart, monthEnd, progress, ct);
        if (allMatches.Count == 0)
        {
            progress.Report(new Lc0DownloadProgress(
                Lc0DownloadPhase.Completed,
                $"No matches found for {options.ArchiveMonth:yyyy-MM}.",
                0,
                0,
                100));

            return new Lc0DownloadResult(0, 0, 0, 0, 0);
        }

        allMatches.Sort((a, b) => a.Date.CompareTo(b.Date));

        var processedMatches = 0;
        var failedMatches = 0;
        long gamesSeen = 0;
        long gamesKept = 0;

        var totalMatches = allMatches.Count;
        await using var outputStream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);
        using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);
        var outputWriter = new OutputWriter(writer);

        for (var i = 0; i < allMatches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var match = allMatches[i];
            var percent = (i / (double)totalMatches) * 100.0;
            progress.Report(new Lc0DownloadProgress(
                Lc0DownloadPhase.Downloading,
                $"Downloading match {match.MatchId} ({i + 1}/{totalMatches})...",
                i + 1,
                totalMatches,
                percent));

            var outcome = await DownloadAndProcessMatchAsync(
                match,
                outputWriter,
                options.ExcludeNonStandard,
                options.OnlyCheckmates,
                ct);

            gamesSeen += outcome.GamesSeen;
            gamesKept += outcome.GamesKept;

            if (outcome.Success)
            {
                processedMatches++;
            }
            else
            {
                failedMatches++;
                if (!string.IsNullOrWhiteSpace(outcome.Message))
                {
                    progress.Report(new Lc0DownloadProgress(
                        Lc0DownloadPhase.Downloading,
                        $"Match {match.MatchId} failed: {outcome.Message}",
                        i + 1,
                        totalMatches,
                        percent));
                }
            }

            if (i < allMatches.Count - 1)
            {
                var delayMs = RandomJitter.Next(200, 450);
                await Task.Delay(delayMs, ct);
            }
        }

        await writer.FlushAsync().ConfigureAwait(false);

        progress.Report(new Lc0DownloadProgress(
            Lc0DownloadPhase.Completed,
            $"Lc0 archive complete. Kept {gamesKept:N0} game(s).",
            totalMatches,
            totalMatches,
            100));

        return new Lc0DownloadResult(totalMatches, processedMatches, failedMatches, gamesSeen, gamesKept);
    }

    private async Task<List<Lc0MatchEntry>> ScrapeMatchesAsync(
        DateTime startDate,
        DateTime endDate,
        IProgress<Lc0DownloadProgress> progress,
        CancellationToken ct)
    {
        var matches = new List<Lc0MatchEntry>();
        var seenMatchIds = new HashSet<int>();
        var emptyPages = 0;
        var page = 1;

        while (page <= MaxScrapePages)
        {
            ct.ThrowIfCancellationRequested();

            progress.Report(new Lc0DownloadProgress(
                Lc0DownloadPhase.Scraping,
                $"Scraping page {page}...",
                page,
                null,
                null));

            var pageMatches = await FetchMatchesPageAsync(page, progress, ct);
            if (pageMatches.Count == 0)
            {
                emptyPages++;
                if (emptyPages >= EmptyPageLimit)
                {
                    break;
                }

                page++;
                continue;
            }

            emptyPages = 0;
            var filtered = FilterMatches(pageMatches, startDate, endDate);
            foreach (var match in filtered)
            {
                if (seenMatchIds.Add(match.MatchId))
                {
                    matches.Add(match);
                }
            }

            // Pages are newest to oldest; stop once we moved before the selected month.
            if (pageMatches.All(m => m.Date.Date < startDate.Date))
            {
                break;
            }

            page++;
        }

        return matches;
    }

    private static List<Lc0MatchEntry> FilterMatches(
        IEnumerable<Lc0MatchEntry> matches,
        DateTime startDate,
        DateTime endDate)
    {
        var filtered = new List<Lc0MatchEntry>();

        foreach (var match in matches)
        {
            var matchDate = match.Date.Date;

            if (matchDate < startDate.Date)
            {
                continue;
            }

            if (matchDate > endDate.Date)
            {
                continue;
            }

            filtered.Add(match);
        }

        return filtered;
    }

    private async Task<List<Lc0MatchEntry>> FetchMatchesPageAsync(
        int page,
        IProgress<Lc0DownloadProgress> progress,
        CancellationToken ct)
    {
        var uri = new Uri($"{MatchesBaseUri}?page={page}&show_all=1");
        try
        {
            var html = await HttpClient.GetStringAsync(uri, ct);
            return ParseMatchList(html);
        }
        catch (Exception ex)
        {
            progress.Report(new Lc0DownloadProgress(
                Lc0DownloadPhase.Scraping,
                $"Failed to fetch page {page}: {ex.Message}"));
            return [];
        }
    }

    private static List<Lc0MatchEntry> ParseMatchList(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var tbodyMatch = TbodyRegex().Match(html);
        if (!tbodyMatch.Success)
        {
            return [];
        }

        var body = tbodyMatch.Groups["body"].Value;
        var matches = new List<Lc0MatchEntry>();

        foreach (Match row in RowRegex().Matches(body))
        {
            if (!row.Success)
            {
                continue;
            }

            var cells = CellRegex().Matches(row.Groups["row"].Value);
            if (cells.Count < 2)
            {
                continue;
            }

            var matchIdText = StripHtml(cells[0].Groups["cell"].Value);
            if (!int.TryParse(matchIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var matchId))
            {
                continue;
            }

            var runText = StripHtml(cells[1].Groups["cell"].Value);
            if (!int.TryParse(runText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runId))
            {
                continue;
            }

            var dateText = StripHtml(cells[^1].Groups["cell"].Value);
            if (!TryParseMatchDate(dateText, out var matchDate))
            {
                continue;
            }

            matches.Add(new Lc0MatchEntry(matchId, runId, matchDate));
        }

        return matches;
    }

    private async Task<Lc0DownloadOutcome> DownloadAndProcessMatchAsync(
        Lc0MatchEntry match,
        OutputWriter outputWriter,
        bool excludeNonStandard,
        bool onlyCheckmates,
        CancellationToken ct)
    {
        string? lastError = null;

        foreach (var candidate in BuildMatchUrls(match.TrainingRunId, match.MatchId))
        {
            var result = await DownloadToTempAsync(candidate.Url, candidate.FileKind, ct);

            if (result.Status == DownloadStatus.NotFound)
            {
                continue;
            }

            if (result.Status != DownloadStatus.Success || string.IsNullOrWhiteSpace(result.TempPath))
            {
                lastError = "Download failed.";
                continue;
            }

            try
            {
                var processResult = await ProcessDownloadedFileAsync(
                    result.TempPath,
                    candidate.FileKind,
                    match,
                    outputWriter,
                    excludeNonStandard,
                    onlyCheckmates,
                    ct);

                if (processResult.GamesSeen > 0)
                {
                    return new Lc0DownloadOutcome(
                        true,
                        $"Kept {processResult.GamesKept:N0} game(s).",
                        processResult.GamesSeen,
                        processResult.GamesKept);
                }

                lastError = "No PGN games found.";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            finally
            {
                try
                {
                    if (File.Exists(result.TempPath))
                    {
                        File.Delete(result.TempPath);
                    }
                }
                catch
                {
                }
            }
        }

        return new Lc0DownloadOutcome(false, lastError ?? "All URL patterns failed.", 0, 0);
    }

    private async Task<DownloadResult> DownloadToTempAsync(Uri url, Lc0FileKind fileKind, CancellationToken ct)
    {
        var extension = fileKind == Lc0FileKind.TarGz ? ".tar.gz" : ".pgn";
        var tempPath = Path.Combine(Path.GetTempPath(), $"lc0_match_{Guid.NewGuid():N}{extension}");

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return DownloadResult.NotFound();
                }

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous);

                await responseStream.CopyToAsync(fileStream, ct);
                return DownloadResult.Success(tempPath);
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        return DownloadResult.Failed();
    }

    private async Task<Lc0ProcessingResult> ProcessDownloadedFileAsync(
        string filePath,
        Lc0FileKind fileKind,
        Lc0MatchEntry match,
        OutputWriter outputWriter,
        bool excludeNonStandard,
        bool onlyCheckmates,
        CancellationToken ct)
    {
        long gamesSeen = 0;
        long gamesKept = 0;

        if (fileKind == Lc0FileKind.TarGz)
        {
            await using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using var tar = new TarReader(gzipStream);

            while (tar.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream == null)
                {
                    continue;
                }

                if (!entry.Name.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = await ProcessPgnStreamAsync(
                    entry.DataStream,
                    match,
                    outputWriter,
                    excludeNonStandard,
                    onlyCheckmates,
                    ct);
                gamesSeen += result.GamesSeen;
                gamesKept += result.GamesKept;
            }
        }
        else
        {
            await using var pgnStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            var result = await ProcessPgnStreamAsync(
                pgnStream,
                match,
                outputWriter,
                excludeNonStandard,
                onlyCheckmates,
                ct);
            gamesSeen += result.GamesSeen;
            gamesKept += result.GamesKept;
        }

        return new Lc0ProcessingResult(gamesSeen, gamesKept);
    }

    private async Task<Lc0ProcessingResult> ProcessPgnStreamAsync(
        Stream stream,
        Lc0MatchEntry match,
        OutputWriter outputWriter,
        bool excludeNonStandard,
        bool onlyCheckmates,
        CancellationToken ct)
    {
        long gamesSeen = 0;
        long gamesKept = 0;

        await foreach (var game in _pgnReader.ReadGamesAsync(stream, ct))
        {
            ct.ThrowIfCancellationRequested();
            gamesSeen++;

            if (!ShouldKeepGame(game, excludeNonStandard, onlyCheckmates))
            {
                continue;
            }

            UpdateHeaders(game, match);

            if (outputWriter.NeedsSeparator)
            {
                await outputWriter.Writer.WriteLineAsync().ConfigureAwait(false);
            }

            await _pgnWriter.WriteGameAsync(outputWriter.Writer, game, ct).ConfigureAwait(false);
            outputWriter.NeedsSeparator = true;
            gamesKept++;
        }

        return new Lc0ProcessingResult(gamesSeen, gamesKept);
    }

    private static bool ShouldKeepGame(PgnGame game, bool excludeNonStandard, bool onlyCheckmates)
    {
        if (excludeNonStandard && !IsStandardVariant(game.Headers))
        {
            return false;
        }

        if (onlyCheckmates && !IsCheckmateGame(game))
        {
            return false;
        }

        return true;
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

    private static bool IsCheckmateGame(PgnGame game)
    {
        if (game.Headers.TryGetHeaderValue("Termination", out var termination) &&
            termination.Contains("checkmate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(game.MoveText) && game.MoveText.Contains('#');
    }

    private static void UpdateHeaders(PgnGame game, Lc0MatchEntry match)
    {
        var dateString = match.Date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var version = GetVersion(match.Date.UtcDateTime.Date);

        game.Headers["Event"] = $"Lc0 match {match.MatchId}";
        game.Headers["Date"] = dateString;
        game.Headers["White"] = $"Lc0 {version}";
        game.Headers["Black"] = $"Lc0 {version}";
    }

    private static string GetVersion(DateTime date)
    {
        foreach (var (cutoff, version) in VersionMap)
        {
            if (date >= cutoff)
            {
                return version;
            }
        }

        return "v0.23.0";
    }

    private static IReadOnlyList<(Uri Url, Lc0FileKind FileKind)> BuildMatchUrls(int trainingRunId, int matchId)
    {
        var runSegments = trainingRunId > 0
            ? new[] { $"{trainingRunId}/", string.Empty }
            : [string.Empty];

        var baseNames = new[]
        {
            matchId.ToString(CultureInfo.InvariantCulture),
            $"match_{matchId}"
        };

        var suffixes = new[]
        {
            (Suffix: ".pgn", Kind: Lc0FileKind.Pgn),
            (Suffix: ".pgn.tar.gz", Kind: Lc0FileKind.TarGz)
        };

        var urls = new List<(Uri Url, Lc0FileKind FileKind)>(runSegments.Length * baseNames.Length * suffixes.Length);
        foreach (var runSegment in runSegments)
        {
            foreach (var baseName in baseNames)
            {
                foreach (var (suffix, kind) in suffixes)
                {
                    urls.Add((new Uri(StorageBaseUri, $"{runSegment}{baseName}{suffix}"), kind));
                }
            }
        }

        return urls;
    }

    private static bool TryParseMatchDate(string raw, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out date);
    }

    private static string StripHtml(string html)
    {
        var text = HtmlTagRegex().Replace(html, string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    [GeneratedRegex("<tbody[^>]*>(?<body>.*?)</tbody>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TbodyRegex();

    [GeneratedRegex("<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex("<td[^>]*>(?<cell>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    private enum Lc0FileKind
    {
        TarGz,
        Pgn
    }

    private sealed record Lc0MatchEntry(int MatchId, int TrainingRunId, DateTimeOffset Date);

    private sealed record DownloadResult(DownloadStatus Status, string? TempPath)
    {
        public static DownloadResult Success(string path) => new(DownloadStatus.Success, path);
        public static DownloadResult NotFound() => new(DownloadStatus.NotFound, null);
        public static DownloadResult Failed() => new(DownloadStatus.Failed, null);
    }

    private enum DownloadStatus
    {
        Success,
        NotFound,
        Failed
    }

    private sealed record Lc0DownloadOutcome(bool Success, string Message, long GamesSeen, long GamesKept);

    private sealed record Lc0ProcessingResult(long GamesSeen, long GamesKept);

    private sealed class OutputWriter
    {
        public OutputWriter(StreamWriter writer)
        {
            Writer = writer;
        }

        public StreamWriter Writer { get; }

        public bool NeedsSeparator { get; set; }
    }
}
// PGNTOOLS-LC0-END
