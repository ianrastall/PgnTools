using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PgnTools.Services;

public sealed record ChesscomMonthlyCrawlOptions(
    DateOnly TargetMonth,
    int MinElo,
    int MaxElo,
    string SeedFilePath,
    string ProcessedFilePath,
    string OutputFilePath,
    string? LogFilePath = null,
    bool ExcludeBullet = false,
    bool RequireRealNames = false,
    int MaxPlayers = 500000);

public sealed record ChesscomMonthlyCrawlProgress(
    string Message,
    int PlayersProcessed,
    int PlayersTotal,
    long GamesSaved,
    int CandidatePlayers,
    int NewPlayers,
    int FailedPlayers,
    int SeedSize);

public sealed record ChesscomMonthlyCrawlResult(
    int PlayersProcessed,
    int PlayersTotal,
    int FailedPlayers,
    long GamesSaved,
    int CandidatePlayers,
    int NewPlayers,
    int SeedSize);

public interface IChesscomMonthlyDownloaderService
{
    Task<ChesscomMonthlyCrawlResult> CrawlMonthAsync(
        ChesscomMonthlyCrawlOptions options,
        IProgress<ChesscomMonthlyCrawlProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class ChesscomMonthlyDownloaderService : IChesscomMonthlyDownloaderService
{
    private const int BufferSize = 65536;
    private const int FlushGameInterval = 50;
    private const int RateLimitMinMs = 800;
    private const int RateLimitMaxMs = 1400;
    private const int MaxFailures = 3;
    private const int MaxRateLimitRetries = 3;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const string PgnKeyPrefix = "pgn:";
    private const string UrlKeyPrefix = "url:";
    private const string UuidKeyPrefix = "uuid:";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(65);
    private static readonly HttpClient HttpClient = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ChesscomMonthlyCrawlResult> CrawlMonthAsync(
        ChesscomMonthlyCrawlOptions options,
        IProgress<ChesscomMonthlyCrawlProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.SeedFilePath))
            throw new ArgumentException("Seed file path is required.", nameof(options.SeedFilePath));
        if (string.IsNullOrWhiteSpace(options.ProcessedFilePath))
            throw new ArgumentException("Processed file path is required.", nameof(options.ProcessedFilePath));
        if (string.IsNullOrWhiteSpace(options.OutputFilePath))
            throw new ArgumentException("Output file path is required.", nameof(options.OutputFilePath));
        if (options.MinElo < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MinElo), "Minimum Elo must be non-negative.");
        if (options.MaxElo <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxElo), "Maximum Elo must be greater than zero.");
        if (options.MaxElo < options.MinElo)
            throw new ArgumentOutOfRangeException(nameof(options.MaxElo), "Maximum Elo must be greater than or equal to minimum Elo.");
        if (options.MaxPlayers <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPlayers), "Max players must be greater than zero.");
        if (!File.Exists(options.SeedFilePath))
            throw new FileNotFoundException("Seed file not found.", options.SeedFilePath);

        var seedPath = Path.GetFullPath(options.SeedFilePath);
        var processedPath = Path.GetFullPath(options.ProcessedFilePath);
        var outputPath = Path.GetFullPath(options.OutputFilePath);
        var logPath = string.IsNullOrWhiteSpace(options.LogFilePath)
            ? null
            : Path.GetFullPath(options.LogFilePath);

        if (string.Equals(seedPath, processedPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(seedPath, outputPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processedPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Seed, processed, and output paths must be different files.");
        }

        if (logPath != null &&
            (string.Equals(logPath, seedPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(logPath, processedPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(logPath, outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Log file path must be different from seed, processed, and output files.");
        }

        var allPlayers = LoadPlayers(seedPath, required: true);
        var processedPlayers = LoadPlayers(processedPath, required: false);
        var existingOutput = await LoadExistingOutputStateAsync(outputPath, ct).ConfigureAwait(false);
        var reprocessProcessedPlayers = !existingOutput.HasGames && processedPlayers.Count > 0;
        var resumeProcessedPlayers = reprocessProcessedPlayers
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : processedPlayers;

        var initialQueue = allPlayers
            .Except(resumeProcessedPlayers, StringComparer.OrdinalIgnoreCase)
            .ToList();
        initialQueue.Sort(StringComparer.OrdinalIgnoreCase);
        var uncappedInitialQueueCount = initialQueue.Count;
        if (initialQueue.Count > options.MaxPlayers)
        {
            initialQueue.RemoveRange(options.MaxPlayers, initialQueue.Count - options.MaxPlayers);
        }
        var initialQueueWasCapped = uncappedInitialQueueCount > initialQueue.Count;

        progress?.Report(new ChesscomMonthlyCrawlProgress(
            initialQueue.Count == 0
                ? "No unprocessed players found."
                : reprocessProcessedPlayers
                    ? $"Output file has no PGN games; reprocessing {initialQueue.Count:N0} seed player(s) for {options.TargetMonth:yyyy-MM}..."
                : initialQueueWasCapped
                    ? $"Starting crawl for {options.TargetMonth:yyyy-MM} with a {options.MaxPlayers:N0}-player cap..."
                    : $"Starting crawl for {options.TargetMonth:yyyy-MM}...",
            0,
            initialQueue.Count,
            0,
            0,
            0,
            0,
            allPlayers.Count));

        var playersProcessed = 0;
        var failedPlayers = 0;
        var newPlayers = 0;
        var candidatePlayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long gamesSaved = 0;
        var gamesSinceFlush = 0;
        var totalPlayers = initialQueue.Count;

        var lockFiles = new List<LockFileHandle>(4);
        StreamWriter? logWriter = null;

        var profileCache = new Dictionary<string, ResolvedPlayer>(StringComparer.OrdinalIgnoreCase);
        var emptyProfile = new ResolvedPlayer(null, null);

        async Task<ResolvedPlayer> GetOrFetchProfileAsync(string uname)
        {
            if (profileCache.TryGetValue(uname, out var cached)) return cached;
            var profile = await FetchPlayerProfileAsync(uname, ct).ConfigureAwait(false);
            var resolved = new ResolvedPlayer(RealNameCleaner.NormalizeName(profile?.Name), profile?.Fide);
            profileCache[uname] = resolved;
            return resolved;
        }

        async Task<ResolvedPlayer> GetProfileForEnrichmentAsync(string uname)
        {
            try
            {
                return await GetOrFetchProfileAsync(uname).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                profileCache[uname] = emptyProfile;
                return emptyProfile;
            }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                var logDirectory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                lockFiles.Add(AcquireLockFile(logPath));
                var logStream = new FileStream(
                    logPath, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                logWriter = new StreamWriter(logStream, new UTF8Encoding(false), BufferSize);

                await WriteLogAsync(logWriter, "=== Chess.com monthly crawl ===").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Target month: {options.TargetMonth:yyyy-MM}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Minimum Elo: {options.MinElo}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Maximum Elo: {options.MaxElo}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Exclude bullet: {options.ExcludeBullet}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Require real names: {options.RequireRealNames}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Max players: {options.MaxPlayers:N0}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Seed list: {seedPath}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Processed list: {processedPath}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Output file: {outputPath}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Seed size: {allPlayers.Count:N0}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Processed size: {processedPlayers.Count:N0}").ConfigureAwait(false);
                if (reprocessProcessedPlayers)
                {
                    await WriteLogAsync(
                            logWriter,
                            "Output file has no PGN games, so the processed list is not being used to skip seed players for this run.")
                        .ConfigureAwait(false);
                }
                await WriteLogAsync(
                        logWriter,
                        initialQueueWasCapped
                            ? $"Initial queue: {initialQueue.Count:N0}/{uncappedInitialQueueCount:N0} (capped)"
                            : $"Initial queue: {initialQueue.Count:N0}")
                    .ConfigureAwait(false);
                await logWriter.FlushAsync().ConfigureAwait(false);
            }

            if (initialQueue.Count == 0)
            {
                await WriteLogAsync(logWriter, "No unprocessed players found.").ConfigureAwait(false);
                return new ChesscomMonthlyCrawlResult(0, 0, 0, 0, 0, 0, allPlayers.Count);
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            var processedDirectory = Path.GetDirectoryName(processedPath);
            if (!string.IsNullOrWhiteSpace(processedDirectory)) Directory.CreateDirectory(processedDirectory);

            var seedDirectory = Path.GetDirectoryName(seedPath);
            if (!string.IsNullOrWhiteSpace(seedDirectory)) Directory.CreateDirectory(seedDirectory);

            lockFiles.Add(AcquireLockFile(seedPath));
            lockFiles.Add(AcquireLockFile(processedPath));
            lockFiles.Add(AcquireLockFile(outputPath));
            var seenGameKeys = existingOutput.GameKeys;

            if (logWriter != null)
            {
                await WriteLogAsync(logWriter, $"Existing games tracked: {seenGameKeys.Count:N0}").ConfigureAwait(false);
                if (File.Exists(outputPath) && !existingOutput.HasGames)
                {
                    await WriteLogAsync(logWriter, "Existing output file contains no PGN game headers. Treating it as empty for resume purposes.").ConfigureAwait(false);
                }
                await logWriter.FlushAsync().ConfigureAwait(false);
            }

            var outputMode = existingOutput.HasGames ? FileMode.OpenOrCreate : FileMode.Create;
            await using var outputStream = new FileStream(
                outputPath, outputMode, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            outputStream.Seek(0, SeekOrigin.End);
            using var outputWriter = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

            await using var seedStream = new FileStream(
                seedPath, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            using var seedWriter = new StreamWriter(seedStream, new UTF8Encoding(false), BufferSize);

            await using var processedStream = new FileStream(
                processedPath, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            using var processedWriter = new StreamWriter(processedStream, new UTF8Encoding(false), BufferSize);

            var needsSeparator = existingOutput.HasGames && outputStream.Length > 0;
            var pending = new Queue<string>(initialQueue);
            var queuedPlayers = new HashSet<string>(initialQueue, StringComparer.OrdinalIgnoreCase);
            var queueCapReachedLogged = initialQueueWasCapped;

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var current = pending.Dequeue();
                var normalizedPlayer = NormalizeUsername(current);
                if (string.IsNullOrWhiteSpace(normalizedPlayer)) continue;

                var playerGames = 0;
                var playerNewPlayers = 0;
                var playerFailed = false;
                var playerCompleted = false;
                var playerCancelled = false;
                var playerDuplicatesSkipped = 0;
                var playerCandidatePlayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var hasRealName = !options.RequireRealNames;
                string? cleanedName = null;
                var currentProfile = emptyProfile;
                var currentProfileLoaded = false;
                var seedWritten = false;
                var processedWritten = false;
                Exception? playerError = null;
                List<string>? discoveredPlayers = logWriter == null ? null : new List<string>();

                if (logWriter != null)
                {
                    await WriteLogAsync(logWriter, $"PLAYER start: {normalizedPlayer} ({playersProcessed + 1:N0}/{totalPlayers:N0})").ConfigureAwait(false);
                }

                try
                {
                    if (options.RequireRealNames)
                    {
                        currentProfile = await GetOrFetchProfileAsync(normalizedPlayer).ConfigureAwait(false);
                        currentProfileLoaded = true;
                        cleanedName = currentProfile.CleanedName;
                        hasRealName = cleanedName != null;
                    }

                    if (!options.RequireRealNames || hasRealName)
                    {
                        var response = await FetchMonthlyGamesAsync(
                            normalizedPlayer,
                            options.TargetMonth.Year,
                            options.TargetMonth.Month,
                            ct).ConfigureAwait(false);

                        if (response?.Games.Count > 0)
                        {
                            foreach (var game in response.Games)
                            {
                                ct.ThrowIfCancellationRequested();

                                if (!IsEligibleGame(game, options.MinElo, options.MaxElo, options.ExcludeBullet)) continue;

                                if (!string.IsNullOrWhiteSpace(game.Pgn))
                                {
                                    if (TryGetOpponent(normalizedPlayer, game, out var opponent))
                                    {
                                        ResolvedPlayer? oppProfile = null;
                                        if (options.RequireRealNames)
                                        {
                                            oppProfile = await GetOrFetchProfileAsync(opponent).ConfigureAwait(false);
                                            if (oppProfile.CleanedName == null) continue;
                                        }

                                        candidatePlayers.Add(opponent);
                                        playerCandidatePlayers.Add(opponent);

                                        if (await TryAddPlayerAsync(opponent, allPlayers, seedWriter).ConfigureAwait(false))
                                        {
                                            newPlayers++;
                                            playerNewPlayers++;
                                            seedWritten = true;
                                            discoveredPlayers?.Add(opponent);

                                            if (!resumeProcessedPlayers.Contains(opponent) &&
                                                totalPlayers < options.MaxPlayers &&
                                                queuedPlayers.Add(opponent))
                                            {
                                                pending.Enqueue(opponent);
                                                totalPlayers++;
                                            }
                                            else if (!queueCapReachedLogged &&
                                                     totalPlayers >= options.MaxPlayers &&
                                                     !resumeProcessedPlayers.Contains(opponent))
                                            {
                                                queueCapReachedLogged = true;
                                                await WriteLogAsync(
                                                        logWriter,
                                                        $"Queue cap reached at {options.MaxPlayers:N0} players. Additional discoveries will remain in the seed list for future runs.")
                                                    .ConfigureAwait(false);
                                            }
                                        }

                                        var gameKeys = GetGameKeys(game);
                                        if (IsDuplicateGame(seenGameKeys, gameKeys))
                                        {
                                            playerDuplicatesSkipped++;
                                            continue;
                                        }

                                        RegisterGameKeys(seenGameKeys, gameKeys);

                                        if (options.RequireRealNames && !currentProfileLoaded)
                                        {
                                            currentProfile = await GetProfileForEnrichmentAsync(normalizedPlayer).ConfigureAwait(false);
                                            currentProfileLoaded = true;
                                            cleanedName = currentProfile.CleanedName;
                                        }

                                        var finalPgn = game.Pgn;
                                        if (options.RequireRealNames)
                                        {
                                            finalPgn = EnrichPgnWithResolvedPlayers(
                                                finalPgn,
                                                normalizedPlayer,
                                                game,
                                                currentProfile,
                                                oppProfile ?? emptyProfile);
                                        }

                                        if (needsSeparator)
                                        {
                                            await outputWriter.WriteLineAsync().ConfigureAwait(false);
                                        }

                                        await outputWriter.WriteLineAsync(finalPgn.TrimEnd()).ConfigureAwait(false);
                                        needsSeparator = true;
                                        playerGames++;
                                        gamesSaved++;
                                        gamesSinceFlush++;

                                        if (gamesSinceFlush >= FlushGameInterval)
                                        {
                                            gamesSinceFlush = 0;
                                            await outputWriter.FlushAsync().ConfigureAwait(false);
                                            await outputStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    playerCompleted = true;
                }
                catch (OperationCanceledException)
                {
                    playerCancelled = true;
                    throw;
                }
                catch (Exception ex)
                {
                    playerFailed = true;
                    failedPlayers++;
                    playerError = ex;
                }
                finally
                {
                    if (gamesSinceFlush > 0)
                    {
                        gamesSinceFlush = 0;
                        await outputWriter.FlushAsync().ConfigureAwait(false);
                        await outputStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    if (!playerCancelled && (playerCompleted || playerFailed))
                    {
                        if (playerCompleted && processedPlayers.Add(normalizedPlayer))
                        {
                            await processedWriter.WriteLineAsync(normalizedPlayer).ConfigureAwait(false);
                            processedWritten = true;
                        }

                        playersProcessed++;
                    }

                    if (seedWritten || processedWritten)
                    {
                        await seedWriter.FlushAsync().ConfigureAwait(false);
                        await processedWriter.FlushAsync().ConfigureAwait(false);
                    }

                    if (logWriter != null)
                    {
                        var realNameDetail = string.IsNullOrWhiteSpace(cleanedName)
                            ? string.Empty
                            : $" [Real Name: {cleanedName}]";

                        if (playerCancelled)
                        {
                            await WriteLogAsync(logWriter, $"PLAYER cancelled: {normalizedPlayer}").ConfigureAwait(false);
                        }
                        else if (playerFailed)
                        {
                            var errorSummary = playerError == null ? "Unknown error" : $"{playerError.GetType().Name}: {playerError.Message}";
                            await WriteLogAsync(logWriter, $"PLAYER error: {normalizedPlayer} ({errorSummary})").ConfigureAwait(false);
                        }
                        else if (options.RequireRealNames && !hasRealName)
                        {
                            await WriteLogAsync(logWriter, $"PLAYER done: {normalizedPlayer} skipped (invalid or no real name)").ConfigureAwait(false);
                        }
                        else if (playerGames > 0)
                        {
                            var duplicateDetail = playerDuplicatesSkipped > 0 ? $" duplicates={playerDuplicatesSkipped:N0}" : string.Empty;
                            await WriteLogAsync(logWriter, $"PLAYER done: {normalizedPlayer}{realNameDetail} games={playerGames:N0} playerCandidates={playerCandidatePlayers.Count:N0} newPlayers={playerNewPlayers:N0}{duplicateDetail}").ConfigureAwait(false);
                        }
                        else if (playerDuplicatesSkipped > 0 || playerNewPlayers > 0)
                        {
                            await WriteLogAsync(logWriter, $"PLAYER done: {normalizedPlayer}{realNameDetail} games=0 playerCandidates={playerCandidatePlayers.Count:N0} newPlayers={playerNewPlayers:N0} duplicates={playerDuplicatesSkipped:N0}").ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteLogAsync(logWriter, $"PLAYER done: {normalizedPlayer}{realNameDetail} no games").ConfigureAwait(false);
                        }

                        if (discoveredPlayers is { Count: > 0 })
                        {
                            await WriteLogAsync(logWriter, $"PLAYER {normalizedPlayer} discovered {discoveredPlayers.Count:N0} new players").ConfigureAwait(false);
                            foreach (var discovery in discoveredPlayers)
                            {
                                await WriteLogAsync(logWriter, $"DISCOVERED {discovery}").ConfigureAwait(false);
                            }
                        }

                        await logWriter.FlushAsync().ConfigureAwait(false);
                    }
                }

                string message;
                if (playerFailed)
                {
                    message = $"{normalizedPlayer}: error";
                }
                else if (options.RequireRealNames && !hasRealName)
                {
                    message = $"{normalizedPlayer}: skipped (invalid or no real name)";
                }
                else
                {
                    var displayName = string.IsNullOrWhiteSpace(cleanedName)
                        ? normalizedPlayer
                        : $"{normalizedPlayer} ({cleanedName})";
                    var messageParts = new List<string>(3);
                    if (playerGames > 0) messageParts.Add($"{playerGames} games");
                    if (playerCandidatePlayers.Count > 0) messageParts.Add($"{playerCandidatePlayers.Count} players found");
                    if (playerNewPlayers > 0) messageParts.Add($"+{playerNewPlayers} new players");
                    if (playerDuplicatesSkipped > 0) messageParts.Add($"{playerDuplicatesSkipped} duplicates skipped");

                    message = messageParts.Count > 0 ? $"{displayName}: {string.Join(" | ", messageParts)}" : $"{displayName}: no games";
                }

                progress?.Report(new ChesscomMonthlyCrawlProgress(
                    message,
                    playersProcessed,
                    totalPlayers,
                    gamesSaved,
                    candidatePlayers.Count,
                    newPlayers,
                    failedPlayers,
                    allPlayers.Count));
            }

            await outputWriter.FlushAsync().ConfigureAwait(false);
            await outputStream.FlushAsync(ct).ConfigureAwait(false);
            await seedWriter.FlushAsync().ConfigureAwait(false);
            await processedWriter.FlushAsync().ConfigureAwait(false);

            if (logWriter != null)
            {
                await WriteLogAsync(logWriter, $"COMPLETE processed={playersProcessed:N0}/{totalPlayers:N0} games={gamesSaved:N0} playerCandidates={candidatePlayers.Count:N0} newPlayers={newPlayers:N0} failed={failedPlayers:N0} seedSize={allPlayers.Count:N0}").ConfigureAwait(false);
                await logWriter.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await WriteLogAsync(logWriter, "CANCELLED").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logWriter, $"ERROR: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (logWriter != null)
            {
                await logWriter.FlushAsync().ConfigureAwait(false);
                logWriter.Dispose();
            }

            foreach (var handle in lockFiles)
            {
                handle.Dispose();
            }
        }

        return new ChesscomMonthlyCrawlResult(playersProcessed, totalPlayers, failedPlayers, gamesSaved, candidatePlayers.Count, newPlayers, allPlayers.Count);
    }

    private static HashSet<string> LoadPlayers(string path, bool required)
    {
        if (!File.Exists(path))
        {
            if (required) throw new FileNotFoundException("Player list file not found.", path);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var normalized = NormalizeUsername(line);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                players.Add(normalized);
            }
        }
        return players;
    }

    private static async Task<ChesscomPlayerProfile?> FetchPlayerProfileAsync(string username, CancellationToken ct)
    {
        var safeUser = Uri.EscapeDataString(username.Trim());
        var url = new Uri($"https://api.chess.com/pub/player/{safeUser}");
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxFailures; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await GetAsyncWithRateLimitRetriesAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;

                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryableStatusCode(response.StatusCode) && attempt < MaxFailures)
                    {
                        await Task.Delay(GetRetryDelay(attempt, response), ct).ConfigureAwait(false);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                }
                return await response.Content.ReadFromJsonAsync<ChesscomPlayerProfile>(JsonOptions, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (JsonException) { throw; }
            catch (Exception ex) when (attempt < MaxFailures && IsRetryableException(ex))
            {
                lastError = ex;
                await Task.Delay(GetRetryDelay(attempt), ct).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException($"Failed to fetch Chess.com profile for '{username}' after {MaxFailures} attempts.", lastError);
    }

    private static async Task<ChesscomMonthlyGamesResponse?> FetchMonthlyGamesAsync(string username, int year, int month, CancellationToken ct)
    {
        var safeUser = Uri.EscapeDataString(username.Trim());
        var url = new Uri($"https://api.chess.com/pub/player/{safeUser}/games/{year}/{month:D2}");
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxFailures; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await GetAsyncWithRateLimitRetriesAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;

                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryableStatusCode(response.StatusCode) && attempt < MaxFailures)
                    {
                        await Task.Delay(GetRetryDelay(attempt, response), ct).ConfigureAwait(false);
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                }
                return await response.Content.ReadFromJsonAsync<ChesscomMonthlyGamesResponse>(JsonOptions, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (JsonException) { throw; }
            catch (Exception ex) when (attempt < MaxFailures && IsRetryableException(ex))
            {
                lastError = ex;
                await Task.Delay(GetRetryDelay(attempt), ct).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException($"Failed to fetch Chess.com monthly games for '{username}' after {MaxFailures} attempts.", lastError);
    }

    private static async Task<HttpResponseMessage> GetAsyncWithRateLimitRetriesAsync(Uri url, CancellationToken ct)
    {
        for (var rateLimitAttempt = 1; ; rateLimitAttempt++)
        {
            ct.ThrowIfCancellationRequested();
            await ApplyRateLimitAsync(ct).ConfigureAwait(false);

            var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || rateLimitAttempt >= MaxRateLimitRetries)
                return response;

            var delay = GetRateLimitDelay(response);
            response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static bool IsEligibleGame(ChesscomMonthlyGame game, int minElo, int maxElo, bool excludeBullet)
    {
        if (game.White == null || game.Black == null) return false;
        if (excludeBullet && IsBulletGame(game)) return false;

        var whiteRating = game.White.Rating ?? 0;
        var blackRating = game.Black.Rating ?? 0;
        return whiteRating >= minElo &&
               whiteRating <= maxElo &&
               blackRating >= minElo &&
               blackRating <= maxElo;
    }

    private static bool TryGetOpponent(string currentPlayer, ChesscomMonthlyGame game, out string opponent)
    {
        opponent = string.Empty;
        var white = NormalizeUsername(game.White?.Username);
        var black = NormalizeUsername(game.Black?.Username);

        if (string.IsNullOrWhiteSpace(white) || string.IsNullOrWhiteSpace(black)) return false;
        if (string.Equals(currentPlayer, white, StringComparison.OrdinalIgnoreCase))
        {
            opponent = black;
            return true;
        }
        if (string.Equals(currentPlayer, black, StringComparison.OrdinalIgnoreCase))
        {
            opponent = white;
            return true;
        }
        return false;
    }

    private static async Task<bool> TryAddPlayerAsync(string opponent, HashSet<string> allPlayers, StreamWriter writer)
    {
        if (string.IsNullOrWhiteSpace(opponent)) return false;
        if (!allPlayers.Add(opponent)) return false;

        await writer.WriteLineAsync(opponent).ConfigureAwait(false);
        return true;
    }

    private static Task WriteLogAsync(StreamWriter? writer, string message)
    {
        if (writer == null) return Task.CompletedTask;
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        return writer.WriteLineAsync($"[{timestamp}] {message}");
    }

    private static string NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return string.Empty;
        return username.Trim().ToLowerInvariant();
    }

    private static bool IsBulletGame(ChesscomMonthlyGame game)
    {
        if (!string.IsNullOrWhiteSpace(game.TimeClass))
        {
            return string.Equals(game.TimeClass.Trim(), "bullet", StringComparison.OrdinalIgnoreCase);
        }
        return !string.IsNullOrWhiteSpace(game.TimeControl) &&
               !game.TimeControl.Contains('/', StringComparison.Ordinal) &&
               TryParseLeadingInt(game.TimeControl.AsSpan(), out var seconds) &&
               seconds < 180;
    }

    private static bool TryParseLeadingInt(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        var length = 0;
        while (length < text.Length && char.IsDigit(text[length])) length++;
        if (length == 0) return false;
        return int.TryParse(text[..length], out value);
    }

    private static bool IsDuplicateGame(HashSet<ulong> seenGameKeys, IReadOnlyList<ulong> gameKeys)
    {
        for (var i = 0; i < gameKeys.Count; i++)
        {
            if (seenGameKeys.Contains(gameKeys[i])) return true;
        }
        return false;
    }

    private static void RegisterGameKeys(HashSet<ulong> seenGameKeys, IReadOnlyList<ulong> gameKeys)
    {
        for (var i = 0; i < gameKeys.Count; i++) seenGameKeys.Add(gameKeys[i]);
    }

    private static List<ulong> GetGameKeys(ChesscomMonthlyGame game)
    {
        var keys = new List<ulong>(3);
        AddGameKey(keys, BuildUuidGameKey(game.Uuid));
        AddGameKey(keys, BuildUrlGameKey(game.Url));

        if (keys.Count == 0 && TryExtractLinkFromPgn(game.Pgn, out var link))
        {
            AddGameKey(keys, BuildUrlGameKey(link));
        }
        if (keys.Count == 0 && !string.IsNullOrWhiteSpace(game.Pgn))
        {
            AddGameKey(keys, BuildPgnGameKey(game.Pgn));
        }
        return keys;
    }

    private static string EnrichPgnWithResolvedPlayers(
        string pgn,
        string normalizedPlayer,
        ChesscomMonthlyGame game,
        ResolvedPlayer currentProfile,
        ResolvedPlayer opponentProfile)
    {
        var isWhite = string.Equals(game.White?.Username, normalizedPlayer, StringComparison.OrdinalIgnoreCase);

        var whiteUsername = game.White?.Username ?? string.Empty;
        var blackUsername = game.Black?.Username ?? string.Empty;

        var whiteRealName = isWhite ? currentProfile.CleanedName : opponentProfile.CleanedName;
        var blackRealName = isWhite ? opponentProfile.CleanedName : currentProfile.CleanedName;

        var whiteFide = isWhite ? currentProfile.FideRating : opponentProfile.FideRating;
        var blackFide = isWhite ? opponentProfile.FideRating : currentProfile.FideRating;

        if (!string.IsNullOrEmpty(whiteRealName))
        {
            pgn = Regex.Replace(
                pgn,
                $@"^\[White\s+""{Regex.Escape(whiteUsername)}""\]",
                $"[White \"{whiteRealName}\"]",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        if (!string.IsNullOrEmpty(blackRealName))
        {
            pgn = Regex.Replace(
                pgn,
                $@"^\[Black\s+""{Regex.Escape(blackUsername)}""\]",
                $"[Black \"{blackRealName}\"]",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        if (whiteFide is > 0)
        {
            pgn = Regex.Replace(
                pgn,
                @"^\[WhiteElo\s+""\d+""\]",
                $"[WhiteElo \"{whiteFide}\"]",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        if (blackFide is > 0)
        {
            pgn = Regex.Replace(
                pgn,
                @"^\[BlackElo\s+""\d+""\]",
                $"[BlackElo \"{blackFide}\"]",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        return pgn;
    }

    private static async Task<ExistingOutputState> LoadExistingOutputStateAsync(string outputPath, CancellationToken ct)
    {
        var gameKeys = new HashSet<ulong>();
        var hasGames = false;
        if (!File.Exists(outputPath)) return new ExistingOutputState(gameKeys, false);

        await using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: BufferSize, leaveOpen: true);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null) break;

            if (!hasGames && TryGetQuotedHeaderValue(line, "Event", out _))
            {
                hasGames = true;
            }

            if (TryGetQuotedHeaderValue(line, "Link", out var link))
            {
                AddGameKey(gameKeys, BuildUrlGameKey(link));
                continue;
            }
            if (TryGetQuotedHeaderValue(line, "UUID", out var uuid))
            {
                AddGameKey(gameKeys, BuildUuidGameKey(uuid));
            }
        }
        return new ExistingOutputState(gameKeys, hasGames);
    }

    private static void AddGameKey(ICollection<ulong> target, ulong? key)
    {
        if (key.HasValue) target.Add(key.Value);
    }

    private static ulong? BuildUuidGameKey(string? uuid) => BuildNormalizedGameKey(UuidKeyPrefix, uuid, trimTrailingSlash: false);
    private static ulong? BuildUrlGameKey(string? url) => BuildNormalizedGameKey(UrlKeyPrefix, url, trimTrailingSlash: true);

    private static ulong? BuildNormalizedGameKey(string prefix, string? value, bool trimTrailingSlash)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var span = value.AsSpan().Trim();
        if (trimTrailingSlash)
        {
            while (!span.IsEmpty && span[^1] == '/') span = span[..^1];
        }

        if (span.IsEmpty) return null;
        var hash = CreateDeterministicHash(prefix);
        HashLowerInvariant(ref hash, span);
        return hash;
    }

    private static ulong BuildPgnGameKey(string pgn)
    {
        var span = pgn.AsSpan().Trim();
        var hash = CreateDeterministicHash(PgnKeyPrefix);

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (ch == '\r')
            {
                HashChar(ref hash, '\n');
                if (i + 1 < span.Length && span[i + 1] == '\n') i++;
                continue;
            }
            HashChar(ref hash, ch);
        }
        return hash;
    }

    private static bool TryExtractLinkFromPgn(string? pgn, out string link)
    {
        link = string.Empty;
        if (string.IsNullOrWhiteSpace(pgn)) return false;

        var remaining = pgn.AsSpan();
        while (!remaining.IsEmpty)
        {
            ReadOnlySpan<char> line;
            var lineBreakIndex = remaining.IndexOfAny('\r', '\n');
            if (lineBreakIndex < 0)
            {
                line = remaining;
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                line = remaining[..lineBreakIndex];
                var newlineLength = 1;
                if (remaining[lineBreakIndex] == '\r' && lineBreakIndex + 1 < remaining.Length && remaining[lineBreakIndex + 1] == '\n')
                {
                    newlineLength = 2;
                }
                remaining = remaining[(lineBreakIndex + newlineLength)..];
            }

            if (TryGetQuotedHeaderValue(line, "Link", out link)) return true;
        }

        link = string.Empty;
        return false;
    }

    private static bool TryGetQuotedHeaderValue(string line, string headerName, out string value) => TryGetQuotedHeaderValue(line.AsSpan(), headerName, out value);

    private static bool TryGetQuotedHeaderValue(ReadOnlySpan<char> line, string headerName, out string value)
    {
        value = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.IsEmpty || trimmed[0] != '[') return false;

        trimmed = trimmed[1..];
        if (!trimmed.StartsWith(headerName, StringComparison.OrdinalIgnoreCase)) return false;

        trimmed = trimmed[headerName.Length..];
        if (trimmed.Length < 4 || trimmed[0] != ' ' || trimmed[1] != '"' || trimmed[^1] != ']') return false;

        var rawValue = trimmed[2..^1];
        if (rawValue.IsEmpty || rawValue[^1] != '"') return false;

        rawValue = rawValue[..^1].Trim();
        if (rawValue.IsEmpty) return false;

        if (rawValue.IndexOf('\\') < 0)
        {
            value = rawValue.ToString();
            return true;
        }

        value = rawValue.ToString()
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        return true;
    }

    private static ulong CreateDeterministicHash(string prefix)
    {
        var hash = FnvOffsetBasis;
        Hash(ref hash, prefix.AsSpan());
        return hash;
    }

    private static void Hash(ref ulong hash, ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++) HashChar(ref hash, value[i]);
    }

    private static void HashLowerInvariant(ref ulong hash, ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++) HashChar(ref hash, char.ToLowerInvariant(value[i]));
    }

    private static void HashChar(ref ulong hash, char value)
    {
        hash ^= value;
        hash *= FnvPrime;
    }

    private static Task ApplyRateLimitAsync(CancellationToken ct)
    {
        var delayMs = Random.Shared.Next(RateLimitMinMs, RateLimitMaxMs);
        return Task.Delay(delayMs, ct);
    }

    private static TimeSpan GetRateLimitDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (retryAfter?.Date is { } date)
        {
            var dateDelay = date - DateTimeOffset.UtcNow;
            if (dateDelay > TimeSpan.Zero) return dateDelay;
        }
        return RateLimitBackoff;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsRetryableException(Exception ex) => ex is HttpRequestException or TaskCanceledException;

    private static TimeSpan GetRetryDelay(int attempt, HttpResponseMessage? response = null)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (retryAfter?.Date is { } date)
        {
            var dateDelay = date - DateTimeOffset.UtcNow;
            if (dateDelay > TimeSpan.Zero) return dateDelay;
        }
        var baseDelay = TimeSpan.FromSeconds(2 * attempt);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(200, 450));
        return baseDelay + jitter;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static LockFileHandle AcquireLockFile(string targetPath)
    {
        var lockPath = $"{targetPath}.lock";
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new LockFileHandle(lockPath, stream);
        }
        catch (IOException ex)
        {
            throw new IOException($"File is in use by another process: {targetPath}", ex);
        }
    }

    public static class RealNameCleaner
    {
        private static readonly HashSet<string> TitleTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "gm", "im", "fm", "cm", "nm", "wgm", "wim", "wfm", "wcm",
            "mr", "mrs", "ms", "miss", "dr", "prof", "professor", "coach"
        };

        private static readonly HashSet<string> LowercaseParticles = new(StringComparer.OrdinalIgnoreCase)
        {
            "al", "ap", "bin", "bint", "da", "das", "de", "del", "dela",
            "della", "der", "di", "dos", "du", "el", "la", "le", "st",
            "ten", "ter", "van", "von"
        };

        private static readonly HashSet<string> CommonShortNameTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "ai", "al", "an", "bo", "da", "de", "do", "du", "fu", "gu",
            "he", "hu", "im", "jo", "ju", "ko", "le", "li", "lu", "ma",
            "md", "mi", "mo", "ng", "ni", "om", "or", "qi", "sa", "so",
            "to", "tu", "vo", "wu", "xu", "ya", "yi", "yu"
        };

        private static readonly HashSet<string> GenericSubstrings = new(StringComparer.OrdinalIgnoreCase)
        {
            "account", "anonymous", "authorized", "chess", "deleted",
            "facebook", "guest", "instagram", "official", "private",
            "settings", "speedrun", "stream", "tactics", "tiktok",
            "twitch", "twitter", "unknown", "youtube"
        };

        private static readonly HashSet<string> GenericExact = new(StringComparer.OrdinalIgnoreCase)
        {
            "anonymous", "chess", "none", "nobody", "null", "player",
            "private", "someone", "unknown"
        };

        private static readonly HashSet<string> JokeTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "animal", "attack", "bobred", "captain", "chucky", "coachren",
            "dracula", "fantomas", "general", "goatplayer", "hedgehog",
            "krypton", "lamassu", "mirage", "number9", "powder", "rockula",
            "shadow", "shuqamuna", "skibidi", "snoopy", "sonic", "spider",
            "spot", "tarzan", "thecrusher", "undercover", "victory", "witchboy"
        };

        private static readonly HashSet<string> BadNameTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "alligator", "anonymous", "bye", "chess", "clash", "daily",
            "executioner", "grandmaster", "hi", "incognito", "item", "me",
            "mister", "naming", "no", "one", "patriot", "player", "russian",
            "title", "tv", "unknown", "you", "yt"
        };

        private static readonly HashSet<(string, string)> ExactBadNames = new()
        {
            ("actually", "inform"), ("doopy", "snoopy"), ("general of", "krypton"),
            ("man", "spider"), ("player", "chess"), ("sonic the", "hedgehog"),
            ("winner", "aggressive")
        };

        private static readonly HashSet<string> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        {
            "ii", "iii", "iv", "vi", "vii", "viii", "ix", "x"
        };

        private static readonly HashSet<string> TwoCharCjkSurnames = new()
        {
            "欧阳", "司马", "上官", "诸葛", "夏侯", "东方", "皇甫", "尉迟",
            "长孙", "司徒", "司空", "独孤", "南宫", "令狐", "钟离", "宇文",
            "闻人", "公孙", "慕容", "轩辕"
        };

        private static readonly Dictionary<char, string> ArabicMap = new()
        {
            {'ا', "a"}, {'أ', "a"}, {'إ', "i"}, {'آ', "a"}, {'ب', "b"},
            {'ة', "a"}, {'ت', "t"}, {'ث', "th"}, {'ج', "j"}, {'ح', "h"},
            {'خ', "kh"}, {'د', "d"}, {'ذ', "dh"}, {'ر', "r"}, {'ز', "z"},
            {'س', "s"}, {'ش', "sh"}, {'ص', "s"}, {'ض', "d"}, {'ط', "t"},
            {'ظ', "z"}, {'ع', "a"}, {'غ', "gh"}, {'ف', "f"}, {'ق', "q"},
            {'ك', "k"}, {'ل', "l"}, {'م', "m"}, {'ن', "n"}, {'ه', "h"},
            {'و', "w"}, {'ؤ', "u"}, {'ي', "y"}, {'ى', "a"}, {'ئ', "i"},
            {'ء', ""}, {'پ', "p"}, {'چ', "ch"}, {'ژ', "zh"}, {'گ', "g"},
            {'ک', "k"}, {'ی', "y"}, {'ۀ', "e"}, {'ﻻ', "la"}
        };

        private static readonly Dictionary<char, string> HebrewMap = new()
        {
            {'א', "a"}, {'ב', "b"}, {'ג', "g"}, {'ד', "d"}, {'ה', "h"},
            {'ו', "v"}, {'ז', "z"}, {'ח', "h"}, {'ט', "t"}, {'י', "y"},
            {'כ', "k"}, {'ך', "k"}, {'ל', "l"}, {'מ', "m"}, {'ם', "m"},
            {'נ', "n"}, {'ן', "n"}, {'ס', "s"}, {'ע', "a"}, {'פ', "p"},
            {'ף', "p"}, {'צ', "ts"}, {'ץ', "ts"}, {'ק', "k"}, {'ר', "r"},
            {'ש', "sh"}, {'ת', "t"}
        };

        private static readonly Regex HanRe = new(@"\p{IsCJKUnifiedIdeographs}+", RegexOptions.Compiled);
        private static readonly Regex ArabicRe = new(@"[\p{IsArabic}]+", RegexOptions.Compiled);
        private static readonly Regex HebrewRe = new(@"[\p{IsHebrew}]+", RegexOptions.Compiled);
        private static readonly Regex LetterRe = new(@"\p{L}", RegexOptions.Compiled);
        private static readonly Regex AllowedNameRe = new(@"^[A-Za-z .'\-]+$", RegexOptions.Compiled);
        private static readonly Regex EmojiOrSymbolRe = new(@"[\p{So}\p{Cs}\p{Co}]", RegexOptions.Compiled);
        private static readonly Regex RepeatedRe = new(@"(.)\1{4,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MultispaceRe = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex ParenWrapperRe = new(@"\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex BracketWrapperRe = new(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex BraceWrapperRe = new(@"\{[^}]*\}", RegexOptions.Compiled);
        private static readonly Regex BadCharsRe = new(@"[^\w .'\-]", RegexOptions.Compiled);
        private static readonly Regex DigitRe = new(@"\d", RegexOptions.Compiled);
        private static readonly Regex HandleRe = new(@"(^|[\s(])@[a-z0-9_]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SplitPieceRe = new(@"([\-'])", RegexOptions.Compiled);
        private static readonly Regex IdEloRe = new(@"\b(?:id|elo)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ExtraBadTokensRe = new(@"\b(?:expert|fearsome|hungry|gonna|crush|detective|cloaked|greatest|ultrabullet|barking|madman|slayer)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ExtraBadPhrasesRe = new(@"\b(?:god bless|no title|you know|thinking about|expert on|dark of)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string NormalizeSpaces(string text) => MultispaceRe.Replace(text, " ").Trim();

        private static string NormalizeRaw(string raw)
        {
            var text = WebUtility.HtmlDecode(raw);
            text = text.Normalize(NormalizationForm.FormKC);
            text = text.Replace("\u00A0", " ").Replace("\u200B", "").Replace("\u200C", "");
            text = text.Replace("\u200D", "").Replace("\u2060", "");
            text = text.Replace("，", ",").Replace("、", ",").Replace("；", ",").Replace(";", ",");
            text = text.Replace("：", ":").Replace("／", "/").Replace("｜", " ");
            text = text.Replace("“", "\"").Replace("”", "\"").Replace("’", "'").Replace("‘", "'");
            text = text.Replace("_", " ");
            return NormalizeSpaces(text);
        }

        private static string TransliterateArabic(string text)
        {
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (ArabicMap.TryGetValue(ch, out var val)) sb.Append(val);
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string TransliterateHebrew(string text)
        {
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (HebrewMap.TryGetValue(ch, out var val)) sb.Append(val);
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string RemoveDiacritics(string text)
        {
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }
            return stringBuilder.ToString().Normalize(NormalizationForm.FormC).Replace("`", "");
        }

        private static string TransliterateText(string text)
        {
            text = ArabicRe.Replace(text, m => TransliterateArabic(m.Value));
            text = HebrewRe.Replace(text, m => TransliterateHebrew(m.Value));
            text = RemoveDiacritics(text);
            return NormalizeSpaces(text);
        }

        private static bool HasLetters(string text) => LetterRe.IsMatch(text);

        private static string StripWrappers(string text)
        {
            text = ParenWrapperRe.Replace(text, " ");
            text = BracketWrapperRe.Replace(text, " ");
            text = BraceWrapperRe.Replace(text, " ");
            return NormalizeSpaces(text);
        }

        private static string RemoveBoundaryTitles(string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (tokens.Count > 0 && TitleTokens.Contains(tokens[0].TrimEnd('.'))) tokens.RemoveAt(0);
            while (tokens.Count > 0 && TitleTokens.Contains(tokens[^1].TrimEnd('.'))) tokens.RemoveAt(tokens.Count - 1);
            return string.Join(" ", tokens);
        }

        private static string DropGivenInitials(string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= 1) return text;
            if (!tokens.Any(t => t.TrimEnd('.').Length > 1)) return text;

            var cleaned = new List<string>();
            foreach (var token in tokens)
            {
                var bare = token.TrimEnd('.');
                if (bare.Length == 1 && char.IsLetter(bare[0])) continue;
                cleaned.Add(token);
            }
            return string.Join(" ", cleaned);
        }

        private static string CleanPart(string part, string field)
        {
            part = StripWrappers(part);
            part = TransliterateText(part);
            part = part.Replace("\"", "").Replace(":", " ").Replace("/", " ").Replace("\\", " ");
            part = part.Replace("&", " ");
            part = BadCharsRe.Replace(part, " ");
            part = part.Replace("_", " ");
            part = DigitRe.Replace(part, " ");
            part = NormalizeSpaces(part);
            part = RemoveBoundaryTitles(part);
            if (field == "given") part = DropGivenInitials(part);
            part = NormalizeSpaces(part).Trim(' ', '.', ',', '\'', '-');
            return part;
        }

        private static bool TokenIsInitial(string token)
        {
            if (token.Length == 1 && char.IsUpper(token[0])) return true;
            if (token.Length is >= 2 and <= 3 && token.All(char.IsUpper)) return !token.Any(c => "AEIOUY".Contains(c));
            return false;
        }

        private static bool HasTooManyInitials(string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return true;
            return tokens.Count(TokenIsInitial) == tokens.Length;
        }

        private static bool LooksLikeUrlOrHandle(string text)
        {
            var lower = text.ToLowerInvariant();
            return lower.Contains("http://") || lower.Contains("https://") || lower.Contains("www.") ||
                   HandleRe.IsMatch(lower) || lower.Contains(".com/") || lower.Contains(".com");
        }

        private static bool ContainsEmojiOrSymbol(string text) => EmojiOrSymbolRe.IsMatch(text);

        private static bool LooksLikeNoise(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || !HasLetters(raw)) return true;
            if (ContainsEmojiOrSymbol(raw)) return true;
            if (LooksLikeUrlOrHandle(raw)) return true;
            var lower = raw.ToLowerInvariant();
            if (GenericSubstrings.Any(s => lower.Contains(s))) return true;
            if (DigitRe.IsMatch(raw)) return true;
            if (RepeatedRe.IsMatch(lower)) return true;
            if (raw.IndexOfAny(new[] { '?', ';', ':', '<', '>', '{', '}', '[', ']', '|', '~', '*', '=', '+' }) >= 0) return true;
            return false;
        }

        private static string SmartCapToken(string token, int index, string field)
        {
            var lower = token.ToLowerInvariant();
            if (RomanNumerals.Contains(lower)) return lower.ToUpperInvariant();
            if (field == "last" && index > 0 && LowercaseParticles.Contains(lower)) return lower;
            if (token.Length == 1 && char.IsUpper(token[0])) return token;

            if (token.Length is >= 2 and <= 3 && token.All(char.IsUpper))
            {
                if (CommonShortNameTokens.Contains(lower)) return char.ToUpperInvariant(lower[0]) + lower[1..];
                if (!lower.Any(c => "aeiouy".Contains(c))) return token;
                if (field == "last") return token;
            }

            var pieces = SplitPieceRe.Split(lower);
            var outList = new List<string>();
            foreach (var piece in pieces)
            {
                if (piece == "-" || piece == "'")
                {
                    outList.Add(piece);
                    continue;
                }
                if (string.IsNullOrEmpty(piece)) continue;

                if (piece.StartsWith("mc") && piece.Length > 2 && piece.All(char.IsLetter))
                    outList.Add("Mc" + char.ToUpperInvariant(piece[2]) + piece[3..]);
                else if (piece.StartsWith("o'") && piece.Length > 2)
                    outList.Add("O'" + char.ToUpperInvariant(piece[2]) + piece[3..]);
                else
                    outList.Add(char.ToUpperInvariant(piece[0]) + piece[1..]);
            }
            return string.Join("", outList);
        }

        private static string SmartTitle(string text, string field)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            for (int i = 0; i < tokens.Length; i++) result.Add(SmartCapToken(tokens[i], i, field));
            return string.Join(" ", result);
        }

        private static (string last, string first)? ParseCjkNoComma(string text)
        {
            if (!HanRe.IsMatch(text)) return null;
            var matches = HanRe.Matches(text);
            if (matches.Count != 1 || matches[0].Value != text || text.Length is < 2 or > 4) return null;

            int surnameLen = text.Length >= 2 && TwoCharCjkSurnames.Contains(text[..2]) ? 2 : 1;
            if (text.Length <= surnameLen) return null;

            return null;
        }

        public static string? NormalizeName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            raw = NormalizeRaw(raw);
            if (LooksLikeNoise(raw)) return null;

            string last, first;
            if (!raw.Contains(','))
            {
                var parsed = ParseCjkNoComma(raw);
                if (parsed.HasValue)
                {
                    last = parsed.Value.last;
                    first = parsed.Value.first;
                }
                else
                {
                    var pieces = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (pieces.Length < 2) return null;
                    last = pieces[^1];
                    first = string.Join(" ", pieces.Take(pieces.Length - 1));
                }
            }
            else
            {
                var pieces = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length < 2) return null;
                last = pieces[0];
                first = string.Join(" ", pieces.Skip(1));
            }

            if (string.IsNullOrEmpty(last) || string.IsNullOrEmpty(first)) return null;

            last = CleanPart(last, "last");
            first = CleanPart(first, "given");
            if (string.IsNullOrEmpty(last) || string.IsNullOrEmpty(first)) return null;

            if (!AllowedNameRe.IsMatch(last) || !AllowedNameRe.IsMatch(first)) return null;

            var normLast = NormalizeSpaces(last);
            var normFirst = NormalizeSpaces(first);
            var lowLast = normLast.ToLowerInvariant();
            var lowFirst = normFirst.ToLowerInvariant();
            var lowFull = $"{lowLast}, {lowFirst}";

            if (GenericExact.Contains(lowLast) || GenericExact.Contains(lowFirst)) return null;
            if (lowLast == lowFirst && lowLast.Length < 4) return null;
            if (HasTooManyInitials(normLast) && HasTooManyInitials(normFirst)) return null;
            if (lowLast.Length <= 2 && lowLast.All(char.IsLetter) && lowFirst.Length <= 2 && lowFirst.All(char.IsLetter)) return null;

            if (lowLast.Split(' ').Any(t => JokeTokens.Contains(t))) return null;
            if (lowFirst.Split(' ').Any(t => JokeTokens.Contains(t))) return null;
            if (lowLast.Split(' ').Any(t => BadNameTokens.Contains(t))) return null;
            if (lowFirst.Split(' ').Any(t => BadNameTokens.Contains(t))) return null;
            if (ExactBadNames.Contains((lowLast, lowFirst))) return null;

            if (IdEloRe.IsMatch(lowFull)) return null;
            if (lowLast.Length == 1 && lowFirst.Length == 1) return null;

            if (ExtraBadTokensRe.IsMatch(lowFull)) return null;
            if (ExtraBadPhrasesRe.IsMatch(lowFull)) return null;

            normLast = SmartTitle(normLast, "last");
            normFirst = SmartTitle(normFirst, "given");

            if (string.IsNullOrEmpty(normLast) || string.IsNullOrEmpty(normFirst)) return null;
            return $"{normLast}, {normFirst}";
        }
    }

    private sealed class ChesscomMonthlyGamesResponse
    {
        [JsonPropertyName("games")]
        public List<ChesscomMonthlyGame> Games { get; set; } = new();
    }

    private sealed class ChesscomMonthlyGame
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("pgn")]
        public string? Pgn { get; set; }

        [JsonPropertyName("time_class")]
        public string? TimeClass { get; set; }

        [JsonPropertyName("time_control")]
        public string? TimeControl { get; set; }

        [JsonPropertyName("white")]
        public ChesscomMonthlyPlayer? White { get; set; }

        [JsonPropertyName("black")]
        public ChesscomMonthlyPlayer? Black { get; set; }
    }

    private sealed class ChesscomMonthlyPlayer
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("rating")]
        public int? Rating { get; set; }
    }

    private sealed class ChesscomPlayerProfile
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("fide")]
        public int? Fide { get; set; }
    }

    private sealed record ResolvedPlayer(string? CleanedName, int? FideRating);

    private sealed record ExistingOutputState(HashSet<ulong> GameKeys, bool HasGames);

    private sealed class LockFileHandle(string path, FileStream stream) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            stream.Dispose();
            TryDeleteLockFile(path);
        }

        private static void TryDeleteLockFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
