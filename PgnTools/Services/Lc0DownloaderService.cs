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
    string OutputFolderPath,
    DateTime? StartDate,
    DateTime? EndDate,
    int? MaxPages,
    int? MaxMatches);

public sealed record Lc0DownloadProgress(
    Lc0DownloadPhase Phase,
    string Message,
    int? Current = null,
    int? Total = null,
    double? Percent = null);

public sealed record Lc0DownloadResult(
    int TotalMatches,
    int ProcessedMatches,
    int SkippedMatches,
    int FailedMatches);

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
        if (string.IsNullOrWhiteSpace(options.OutputFolderPath))
        {
            throw new ArgumentException("Output folder path is required.", nameof(options.OutputFolderPath));
        }

        var outputRoot = Path.GetFullPath(options.OutputFolderPath);
        Directory.CreateDirectory(outputRoot);

        var statePath = Path.Combine(outputRoot, "lc0_processed_ids.txt");
        var processedIds = LoadProcessedIds(statePath);

        progress.Report(new Lc0DownloadProgress(
            Lc0DownloadPhase.Scraping,
            "Scraping match metadata..."));

        var allMatches = await ScrapeMatchesAsync(options, progress, ct);

        var skippedExisting = 0;
        if (processedIds.Count > 0)
        {
            skippedExisting = allMatches.RemoveAll(m => processedIds.Contains(m.MatchId));
        }

        if (options.MaxMatches is { } cap && cap > 0 && allMatches.Count > cap)
        {
            allMatches = allMatches.Take(cap).ToList();
        }

        if (allMatches.Count == 0)
        {
            progress.Report(new Lc0DownloadProgress(
                Lc0DownloadPhase.Completed,
                "No matches to download after filtering.",
                0,
                0,
                100));

            return new Lc0DownloadResult(0, 0, skippedExisting, 0);
        }

        allMatches.Sort((a, b) => a.Date.CompareTo(b.Date));

        var processedCount = 0;
        var failedCount = 0;
        var totalMatches = allMatches.Count;

        await using var stateStream = new FileStream(
            statePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);
        await using var stateWriter = new StreamWriter(stateStream, new UTF8Encoding(false));

        await using var writerCache = new MonthlyWriterCache(outputRoot, _pgnWriter);

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

            var outcome = await DownloadAndProcessMatchAsync(match, writerCache, ct);
            if (outcome.Success)
            {
                processedCount++;
                await stateWriter.WriteLineAsync(match.MatchId.ToString());
                await stateWriter.FlushAsync();
            }
            else
            {
                failedCount++;
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

        progress.Report(new Lc0DownloadProgress(
            Lc0DownloadPhase.Completed,
            "Lc0 download complete.",
            totalMatches,
            totalMatches,
            100));

        return new Lc0DownloadResult(totalMatches, processedCount, skippedExisting, failedCount);
    }

    private async Task<List<Lc0MatchEntry>> ScrapeMatchesAsync(
        Lc0DownloadOptions options,
        IProgress<Lc0DownloadProgress> progress,
        CancellationToken ct)
    {
        var matches = new List<Lc0MatchEntry>();
        var emptyPages = 0;
        var page = 1;
        var maxPages = options.MaxPages;

        while (!maxPages.HasValue || page <= maxPages.Value)
        {
            ct.ThrowIfCancellationRequested();

            var percent = maxPages.HasValue && maxPages.Value > 0
                ? (page - 1) / (double)maxPages.Value * 100.0
                : (double?)null;

            progress.Report(new Lc0DownloadProgress(
                Lc0DownloadPhase.Scraping,
                $"Scraping page {page}...",
                page,
                maxPages,
                percent));

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

            var filtered = FilterMatches(pageMatches, options.StartDate, options.EndDate);
            matches.AddRange(filtered);

            if (options.MaxMatches is { } cap && cap > 0 && matches.Count >= cap)
            {
                break;
            }

            if (options.StartDate.HasValue && pageMatches.All(m => m.Date.Date < options.StartDate.Value.Date))
            {
                break;
            }

            page++;
        }

        return matches;
    }

    private static List<Lc0MatchEntry> FilterMatches(
        IEnumerable<Lc0MatchEntry> matches,
        DateTime? startDate,
        DateTime? endDate)
    {
        var filtered = new List<Lc0MatchEntry>();

        foreach (var match in matches)
        {
            var matchDate = match.Date.Date;

            if (startDate.HasValue && matchDate < startDate.Value.Date)
            {
                continue;
            }

            if (endDate.HasValue && matchDate > endDate.Value.Date)
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
        MonthlyWriterCache writerCache,
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
                var gamesWritten = await ProcessDownloadedFileAsync(result.TempPath, candidate.FileKind, match, writerCache, ct);
                if (gamesWritten > 0)
                {
                    return new Lc0DownloadOutcome(true, $"Processed {gamesWritten} game(s).");
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
                    if (!string.IsNullOrWhiteSpace(result.TempPath) && File.Exists(result.TempPath))
                    {
                        File.Delete(result.TempPath);
                    }
                }
                catch
                {
                }
            }
        }

        return new Lc0DownloadOutcome(false, lastError ?? "All URL patterns failed.");
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

    private async Task<int> ProcessDownloadedFileAsync(
        string filePath,
        Lc0FileKind fileKind,
        Lc0MatchEntry match,
        MonthlyWriterCache writerCache,
        CancellationToken ct)
    {
        var gamesWritten = 0;

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

                gamesWritten += await ProcessPgnStreamAsync(entry.DataStream, match, writerCache, ct);
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

            gamesWritten += await ProcessPgnStreamAsync(pgnStream, match, writerCache, ct);
        }

        return gamesWritten;
    }

    private async Task<int> ProcessPgnStreamAsync(
        Stream stream,
        Lc0MatchEntry match,
        MonthlyWriterCache writerCache,
        CancellationToken ct)
    {
        var count = 0;
        await foreach (var game in _pgnReader.ReadGamesAsync(stream, ct))
        {
            ct.ThrowIfCancellationRequested();

            UpdateHeaders(game, match);
            await writerCache.AppendGameAsync(match.Date, game, ct);
            count++;
        }

        return count;
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
        var plainBase = matchId.ToString(CultureInfo.InvariantCulture);
        var matchBase = $"match_{matchId}";
        var runPart = trainingRunId > 0
            ? $"{trainingRunId}/"
            : string.Empty;
        return
        [
            // Match PGNs are stored under match_pgns/{run}/{matchId}.pgn
            (new Uri(StorageBaseUri, $"{runPart}{plainBase}.pgn"), Lc0FileKind.Pgn),
            (new Uri(StorageBaseUri, $"{runPart}{plainBase}.pgn.tar.gz"), Lc0FileKind.TarGz),
            (new Uri(StorageBaseUri, $"{runPart}{matchBase}.pgn"), Lc0FileKind.Pgn),
            (new Uri(StorageBaseUri, $"{runPart}{matchBase}.pgn.tar.gz"), Lc0FileKind.TarGz),
            // Root directory fallbacks
            (new Uri(StorageBaseUri, $"{plainBase}.pgn"), Lc0FileKind.Pgn),
            (new Uri(StorageBaseUri, $"{plainBase}.pgn.tar.gz"), Lc0FileKind.TarGz),
            (new Uri(StorageBaseUri, $"{matchBase}.pgn.tar.gz"), Lc0FileKind.TarGz),
            (new Uri(StorageBaseUri, $"{matchBase}.pgn"), Lc0FileKind.Pgn)
        ];
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

    private static HashSet<int> LoadProcessedIds(string path)
    {
        var ids = new HashSet<int>();

        if (!File.Exists(path))
        {
            return ids;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
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

    private sealed record Lc0DownloadOutcome(bool Success, string Message);

    private sealed class MonthlyWriterCache : IAsyncDisposable
    {
        private readonly string _outputRoot;
        private readonly PgnWriter _pgnWriter;
        private readonly Dictionary<string, MonthlyWriter> _writers = new(StringComparer.OrdinalIgnoreCase);

        public MonthlyWriterCache(string outputRoot, PgnWriter pgnWriter)
        {
            _outputRoot = outputRoot;
            _pgnWriter = pgnWriter;
        }

        public async Task AppendGameAsync(DateTimeOffset date, PgnGame game, CancellationToken ct)
        {
            var key = $"{date:yyyy-MM}";
            if (!_writers.TryGetValue(key, out var writer))
            {
                writer = await CreateWriterAsync(date);
                _writers[key] = writer;
            }

            if (writer.NeedsSeparator)
            {
                await writer.Writer.WriteLineAsync().ConfigureAwait(false);
            }

            await _pgnWriter.WriteGameAsync(writer.Writer, game, ct).ConfigureAwait(false);
            writer.NeedsSeparator = true;
        }

        private async Task<MonthlyWriter> CreateWriterAsync(DateTimeOffset date)
        {
            var yearFolder = Path.Combine(_outputRoot, date.Year.ToString(CultureInfo.InvariantCulture));
            var monthFolder = Path.Combine(yearFolder, date.Month.ToString("D2", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(monthFolder);

            var fileName = $"lc0_matches_{date:yyyy_MM}.pgn";
            var filePath = Path.Combine(monthFolder, fileName);

            var stream = new FileStream(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous);

            stream.Seek(0, SeekOrigin.End);
            var needsSeparator = stream.Length > 0;

            var writer = new StreamWriter(stream, new UTF8Encoding(false), BufferSize);
            return new MonthlyWriter(writer, needsSeparator);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var writer in _writers.Values)
            {
                try
                {
                    await writer.Writer.FlushAsync().ConfigureAwait(false);
                    await writer.Writer.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _writers.Clear();
        }

        private sealed class MonthlyWriter
        {
            public MonthlyWriter(StreamWriter writer, bool needsSeparator)
            {
                Writer = writer;
                NeedsSeparator = needsSeparator;
            }

            public StreamWriter Writer { get; }
            public bool NeedsSeparator { get; set; }
        }
    }
}
