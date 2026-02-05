// PGNTOOLS-LC0-BEGIN
using System.Diagnostics;
using HtmlAgilityPack;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

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
    private const int MaxScrapeRetries = 3;
    private const int FailedPageLimit = 3;
    private const int EmptyPageLimit = 2;
    private const int MaxScrapePages = 5000;
    private static readonly Uri MatchesBaseUri = new("https://training.lczero.org/matches/");
    private static readonly Uri StorageBaseUri = new("https://storage.lczero.org/files/match_pgns/");
    private static readonly HttpClient HttpClient = CreateClient();

    private static readonly string[] MatchDateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "MMM d, yyyy",
        "MMM dd, yyyy",
        "M/d/yyyy",
        "MM/dd/yyyy"
    ];

    private static readonly JsonSerializerOptions VersionMapJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<IReadOnlyList<VersionCutoff>> VersionMap = new(LoadVersionMap);

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

        var allMatches = await ScrapeMatchesAsync(monthStart, monthEnd, progress, ct).ConfigureAwait(false);
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
        var tempOutputPath = BuildTempOutputPath(outputPath);

        try
        {
            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous))
            {
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
                        ct).ConfigureAwait(false);

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
                        var delayMs = Random.Shared.Next(200, 450);
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    }
                }

                await writer.FlushAsync().ConfigureAwait(false);
                await outputStream.FlushAsync(ct).ConfigureAwait(false);
            }

            ReplaceOutputFile(tempOutputPath, outputPath);
        }
        catch
        {
            TryDeleteFile(tempOutputPath);
            throw;
        }

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
        var failedPages = 0;
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

            var pageResult = await FetchMatchesPageAsync(page, progress, ct).ConfigureAwait(false);
            if (pageResult.Status == PageFetchStatus.Failed)
            {
                failedPages++;
                emptyPages = 0;
                if (failedPages >= FailedPageLimit)
                {
                    break;
                }

                page++;
                continue;
            }

            failedPages = 0;
            var pageMatches = pageResult.Matches;
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

    private async Task<PageFetchResult> FetchMatchesPageAsync(
        int page,
        IProgress<Lc0DownloadProgress> progress,
        CancellationToken ct)
    {
        var uri = new Uri($"{MatchesBaseUri}?page={page}&show_all=1");
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxScrapeRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var response = await HttpClient
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return PageFetchResult.Success([]);
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastError = FormatStatus(response.StatusCode, response.ReasonPhrase);
                    if (IsRetryableStatusCode(response.StatusCode) && attempt < MaxScrapeRetries)
                    {
                        await Task.Delay(GetRetryDelay(attempt, response), ct).ConfigureAwait(false);
                        continue;
                    }

                    return PageFetchResult.Failed(lastError);
                }

                var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return PageFetchResult.Success(ParseMatchList(html));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxScrapeRetries)
            {
                lastError = ex.Message;
                await Task.Delay(GetRetryDelay(attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                break;
            }
        }

        progress.Report(new Lc0DownloadProgress(
            Lc0DownloadPhase.Scraping,
            $"Failed to fetch page {page}: {lastError ?? "Unknown error"}"));
        return PageFetchResult.Failed(lastError ?? "Unknown error");
    }

    private static List<Lc0MatchEntry> ParseMatchList(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var matches = new List<Lc0MatchEntry>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tbody/tr");
        if (rows == null)
        {
            return matches;
        }

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 2)
            {
                continue;
            }

            var matchIdText = NormalizeCellText(cells[0].InnerText);
            if (!int.TryParse(matchIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var matchId))
            {
                continue;
            }

            var runText = NormalizeCellText(cells[1].InnerText);
            if (!int.TryParse(runText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runId))
            {
                continue;
            }

            var dateText = NormalizeCellText(cells[^1].InnerText);
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
            ct.ThrowIfCancellationRequested();

            var result = await DownloadToTempAsync(candidate.Url, candidate.FileKind, ct).ConfigureAwait(false);

            if (result.Status == DownloadStatus.NotFound)
            {
                continue;
            }

            if (result.Status != DownloadStatus.Success || string.IsNullOrWhiteSpace(result.TempPath))
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    lastError = $"{candidate.Url} -> {result.Message}";
                }
                else
                {
                    lastError = $"{candidate.Url} -> Download failed.";
                }
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
                    ct).ConfigureAwait(false);

                if (processResult.GamesSeen > 0)
                {
                    return new Lc0DownloadOutcome(
                        true,
                        $"Kept {processResult.GamesKept:N0} game(s).",
                        processResult.GamesSeen,
                        processResult.GamesKept);
                }

                lastError = $"{candidate.Url} -> No PGN games found.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"{candidate.Url} -> {ex.Message}";
            }
            finally
            {
                TryDeleteFile(result.TempPath);
            }
        }

        return new Lc0DownloadOutcome(false, lastError ?? "All URL patterns failed.", 0, 0);
    }

    private async Task<DownloadResult> DownloadToTempAsync(Uri url, Lc0FileKind fileKind, CancellationToken ct)
    {
        var extension = fileKind == Lc0FileKind.TarGz ? ".tar.gz" : ".pgn";
        var tempPath = Path.Combine(Path.GetTempPath(), $"lc0_match_{Guid.NewGuid():N}{extension}");
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var response = await HttpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return DownloadResult.NotFound();
                }

                if (response.IsSuccessStatusCode)
                {
                    await using var responseStream = await response.Content
                        .ReadAsStreamAsync(ct)
                        .ConfigureAwait(false);
                    await using var fileStream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        BufferSize,
                        FileOptions.Asynchronous);

                    await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                    return DownloadResult.Success(tempPath);
                }

                lastError = FormatStatus(response.StatusCode, response.ReasonPhrase);
                if (IsRetryableStatusCode(response.StatusCode) && attempt < MaxRetries)
                {
                    await Task.Delay(GetRetryDelay(attempt, response), ct).ConfigureAwait(false);
                    continue;
                }

                return DownloadResult.Failed(lastError ?? "Download failed.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                lastError = ex.Message;
                TryDeleteFile(tempPath);

                await Task.Delay(GetRetryDelay(attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                break;
            }
        }

        TryDeleteFile(tempPath);
        return DownloadResult.Failed(lastError ?? "Download failed.");
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

                // Do not dispose entry.DataStream: TarReader expects sequential consumption.
                var result = await ProcessPgnStreamAsync(
                    entry.DataStream,
                    match,
                    outputWriter,
                    excludeNonStandard,
                    onlyCheckmates,
                    ct).ConfigureAwait(false);
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
                ct).ConfigureAwait(false);
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
        if (headers.TryGetHeaderValue("SetUp", out var setup) &&
            setup.Trim().Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (headers.TryGetHeaderValue("FEN", out var fen) && !string.IsNullOrWhiteSpace(fen))
        {
            return false;
        }

        if (!headers.TryGetHeaderValue("Variant", out var variant) || string.IsNullOrWhiteSpace(variant))
        {
            return true;
        }

        var normalized = variant.Trim();
        return normalized.Equals("Standard", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Chess", StringComparison.OrdinalIgnoreCase);
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
        var dateOnly = DateOnly.FromDateTime(match.Date.UtcDateTime);
        var dateString = dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var version = GetVersion(dateOnly);

        game.Headers["Event"] = $"Lc0 match {match.MatchId}";
        game.Headers["Date"] = dateString;
        game.Headers["White"] = $"Lc0 {version}";
        game.Headers["Black"] = $"Lc0 {version}";
    }

    private static string GetVersion(DateOnly date)
    {
        foreach (var cutoff in VersionMap.Value)
        {
            if (date >= cutoff.Start)
            {
                return cutoff.Version;
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
        if (string.IsNullOrWhiteSpace(raw))
        {
            date = default;
            return false;
        }

        var cleaned = raw.Trim();
        if (DateTimeOffset.TryParseExact(
                cleaned,
                MatchDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out date))
        {
            return true;
        }

        return DateTimeOffset.TryParse(
            cleaned,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out date);
    }

    private static string NormalizeCellText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decoded = HtmlEntity.DeEntitize(text);
        return string.Join(" ", decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<VersionCutoff> LoadVersionMap()
    {
        var defaults = CreateDefaultVersionMap();
        var configPath = ResolveVersionMapPath();
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return defaults;
        }

        try
        {
            using var stream = File.OpenRead(configPath);
            var config = JsonSerializer.Deserialize<VersionMapConfig>(stream, VersionMapJsonOptions);
            if (config?.VersionMap == null || config.VersionMap.Count == 0)
            {
                return defaults;
            }

            var parsed = new List<VersionCutoff>(config.VersionMap.Count);
            foreach (var entry in config.VersionMap)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Start) || string.IsNullOrWhiteSpace(entry.Version))
                {
                    continue;
                }

                if (!DateOnly.TryParseExact(
                        entry.Start.Trim(),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var start))
                {
                    continue;
                }

                parsed.Add(new VersionCutoff(start, entry.Version.Trim()));
            }

            if (parsed.Count == 0)
            {
                return defaults;
            }

            parsed.Sort((a, b) => b.Start.CompareTo(a.Start));
            return parsed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load lc0-version-map.json: {ex.Message}");
            return defaults;
        }
    }

    private static List<VersionCutoff> CreateDefaultVersionMap() =>
    [
        new VersionCutoff(new DateOnly(2025, 1, 1), "v0.32.0"),
        new VersionCutoff(new DateOnly(2024, 6, 1), "v0.31.0"),
        new VersionCutoff(new DateOnly(2023, 7, 1), "v0.30.0"),
        new VersionCutoff(new DateOnly(2023, 1, 1), "v0.29.0"),
        new VersionCutoff(new DateOnly(2022, 1, 1), "v0.28.0"),
        new VersionCutoff(new DateOnly(2021, 1, 1), "v0.27.0"),
        new VersionCutoff(new DateOnly(2020, 1, 1), "v0.26.0"),
        new VersionCutoff(new DateOnly(2019, 1, 1), "v0.25.0"),
        new VersionCutoff(new DateOnly(2018, 1, 1), "v0.24.0"),
        new VersionCutoff(new DateOnly(1970, 1, 1), "v0.23.0")
    ];

    private static string? ResolveVersionMapPath()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "Assets", "lc0-version-map.json");
        if (File.Exists(primary))
        {
            return primary;
        }

        var secondary = Path.Combine(AppContext.BaseDirectory, "lc0-version-map.json");
        return File.Exists(secondary) ? secondary : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // Rely on caller-provided CancellationToken for long downloads.
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static string BuildTempOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        var fileName = Path.GetFileName(outputPath);

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Output file path must include a valid directory and file name.");
        }

        return Path.Combine(directory, $"{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static void ReplaceOutputFile(string tempPath, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            File.Replace(tempPath, outputPath, null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(tempPath, outputPath, overwrite: true);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to delete temp file '{path}': {ex.Message}");
        }
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(int attempt, HttpResponseMessage? response = null)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var dateDelay = date - DateTimeOffset.UtcNow;
            if (dateDelay > TimeSpan.Zero)
            {
                return dateDelay;
            }
        }

        var baseDelay = TimeSpan.FromSeconds(2 * attempt);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(200, 450));
        return baseDelay + jitter;
    }

    private static string FormatStatus(HttpStatusCode statusCode, string? reasonPhrase)
    {
        var reason = string.IsNullOrWhiteSpace(reasonPhrase) ? string.Empty : $" {reasonPhrase}";
        return $"{(int)statusCode}{reason}";
    }

    private enum Lc0FileKind
    {
        TarGz,
        Pgn
    }

    private sealed record Lc0MatchEntry(int MatchId, int TrainingRunId, DateTimeOffset Date);

    private sealed record VersionCutoff(DateOnly Start, string Version);

    private sealed record VersionMapConfig(List<VersionMapEntry>? VersionMap);

    private sealed record VersionMapEntry(string? Start, string? Version);

    private sealed record PageFetchResult(PageFetchStatus Status, List<Lc0MatchEntry> Matches, string? ErrorMessage)
    {
        public static PageFetchResult Success(List<Lc0MatchEntry> matches) =>
            new(PageFetchStatus.Success, matches, null);

        public static PageFetchResult Failed(string message) =>
            new(PageFetchStatus.Failed, [], message);
    }

    private enum PageFetchStatus
    {
        Success,
        Failed
    }

    private sealed record DownloadResult(DownloadStatus Status, string? TempPath, string? Message)
    {
        public static DownloadResult Success(string path) => new(DownloadStatus.Success, path, null);
        public static DownloadResult NotFound() => new(DownloadStatus.NotFound, null, null);
        public static DownloadResult Failed(string message) => new(DownloadStatus.Failed, null, message);
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
