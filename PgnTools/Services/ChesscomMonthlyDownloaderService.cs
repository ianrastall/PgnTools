using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgnTools.Services;

public sealed record ChesscomMonthlyCrawlOptions(
    DateOnly TargetMonth,
    int MinElo,
    string SeedFilePath,
    string ProcessedFilePath,
    string OutputFilePath,
    string? LogFilePath = null,
    bool ExcludeBullet = false);

public sealed record ChesscomMonthlyCrawlProgress(
    string Message,
    int PlayersProcessed,
    int PlayersTotal,
    long GamesSaved,
    int NewPlayers,
    int FailedPlayers,
    int SeedSize);

public sealed record ChesscomMonthlyCrawlResult(
    int PlayersProcessed,
    int PlayersTotal,
    int FailedPlayers,
    long GamesSaved,
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
        {
            throw new ArgumentException("Seed file path is required.", nameof(options.SeedFilePath));
        }
        if (string.IsNullOrWhiteSpace(options.ProcessedFilePath))
        {
            throw new ArgumentException("Processed file path is required.", nameof(options.ProcessedFilePath));
        }
        if (string.IsNullOrWhiteSpace(options.OutputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(options.OutputFilePath));
        }
        if (options.MinElo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MinElo), "Minimum Elo must be non-negative.");
        }
        if (!File.Exists(options.SeedFilePath))
        {
            throw new FileNotFoundException("Seed file not found.", options.SeedFilePath);
        }

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

        var initialQueue = allPlayers
            .Except(processedPlayers, StringComparer.OrdinalIgnoreCase)
            .ToList();
        initialQueue.Sort(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new ChesscomMonthlyCrawlProgress(
            initialQueue.Count == 0
                ? "No unprocessed players found."
                : $"Starting crawl for {options.TargetMonth:yyyy-MM}...",
            0,
            initialQueue.Count,
            0,
            0,
            0,
            allPlayers.Count));

        var playersProcessed = 0;
        var failedPlayers = 0;
        var newPlayers = 0;
        long gamesSaved = 0;
        var gamesSinceFlush = 0;
        var totalPlayers = initialQueue.Count;

        var lockFiles = new List<LockFileHandle>(4);
        StreamWriter? logWriter = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                var logDirectory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                lockFiles.Add(AcquireLockFile(logPath));
                var logStream = new FileStream(
                    logPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous);
                logWriter = new StreamWriter(logStream, new UTF8Encoding(false), BufferSize);

                await WriteLogAsync(logWriter, "=== Chess.com monthly crawl ===").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Target month: {options.TargetMonth:yyyy-MM}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Minimum Elo: {options.MinElo}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Exclude bullet: {options.ExcludeBullet}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Seed list: {seedPath}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Processed list: {processedPath}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Output file: {outputPath}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Seed size: {allPlayers.Count:N0}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Processed size: {processedPlayers.Count:N0}").ConfigureAwait(false);
                await WriteLogAsync(logWriter, $"Initial queue: {initialQueue.Count:N0}").ConfigureAwait(false);
                await logWriter.FlushAsync().ConfigureAwait(false);
            }

            if (initialQueue.Count == 0)
            {
                await WriteLogAsync(logWriter, "No unprocessed players found.").ConfigureAwait(false);
                return new ChesscomMonthlyCrawlResult(0, 0, 0, 0, 0, allPlayers.Count);
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var processedDirectory = Path.GetDirectoryName(processedPath);
            if (!string.IsNullOrWhiteSpace(processedDirectory))
            {
                Directory.CreateDirectory(processedDirectory);
            }

            var seedDirectory = Path.GetDirectoryName(seedPath);
            if (!string.IsNullOrWhiteSpace(seedDirectory))
            {
                Directory.CreateDirectory(seedDirectory);
            }

            lockFiles.Add(AcquireLockFile(seedPath));
            lockFiles.Add(AcquireLockFile(processedPath));
            lockFiles.Add(AcquireLockFile(outputPath));
            var seenGameKeys = await LoadExistingGameKeysAsync(outputPath, ct).ConfigureAwait(false);

            if (logWriter != null)
            {
                await WriteLogAsync(logWriter, $"Existing games tracked: {seenGameKeys.Count:N0}").ConfigureAwait(false);
                await logWriter.FlushAsync().ConfigureAwait(false);
            }

            await using var outputStream = new FileStream(
                outputPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            outputStream.Seek(0, SeekOrigin.End);
            using var outputWriter = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

            await using var seedStream = new FileStream(
                seedPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous);
            using var seedWriter = new StreamWriter(seedStream, new UTF8Encoding(false), BufferSize);

            await using var processedStream = new FileStream(
                processedPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous);
            using var processedWriter = new StreamWriter(processedStream, new UTF8Encoding(false), BufferSize);

            var needsSeparator = outputStream.Length > 0;
            var pending = new Queue<string>(initialQueue);
            var queuedPlayers = new HashSet<string>(initialQueue, StringComparer.OrdinalIgnoreCase);

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var current = pending.Dequeue();
                var normalizedPlayer = NormalizeUsername(current);
                if (string.IsNullOrWhiteSpace(normalizedPlayer))
                {
                    continue;
                }

                var playerGames = 0;
                var playerNewPlayers = 0;
                var playerFailed = false;
                var playerCompleted = false;
                var playerCancelled = false;
                var playerDuplicatesSkipped = 0;
                var seedWritten = false;
                var processedWritten = false;
                Exception? playerError = null;
                List<string>? discoveredPlayers = logWriter == null ? null : new List<string>();

                if (logWriter != null)
                {
                    await WriteLogAsync(
                            logWriter,
                            $"PLAYER start: {normalizedPlayer} ({playersProcessed + 1:N0}/{totalPlayers:N0})")
                        .ConfigureAwait(false);
                }

                try
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

                            if (!IsEligibleGame(game, options.MinElo, options.ExcludeBullet))
                            {
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(game.Pgn))
                            {
                                if (TryGetOpponent(normalizedPlayer, game, out var opponent) &&
                                    await TryAddPlayerAsync(opponent, allPlayers, seedWriter).ConfigureAwait(false))
                                {
                                    newPlayers++;
                                    playerNewPlayers++;
                                    seedWritten = true;
                                    discoveredPlayers?.Add(opponent);

                                    if (!processedPlayers.Contains(opponent) && queuedPlayers.Add(opponent))
                                    {
                                        pending.Enqueue(opponent);
                                        totalPlayers++;
                                    }
                                }

                                var gameKeys = GetGameKeys(game);
                                if (IsDuplicateGame(seenGameKeys, gameKeys))
                                {
                                    playerDuplicatesSkipped++;
                                    continue;
                                }

                                RegisterGameKeys(seenGameKeys, gameKeys);

                                if (needsSeparator)
                                {
                                    await outputWriter.WriteLineAsync().ConfigureAwait(false);
                                }

                                await outputWriter.WriteLineAsync(game.Pgn.TrimEnd()).ConfigureAwait(false);
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
                        if (processedPlayers.Add(normalizedPlayer))
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
                        if (playerCancelled)
                        {
                            await WriteLogAsync(logWriter, $"PLAYER cancelled: {normalizedPlayer}").ConfigureAwait(false);
                        }
                        else if (playerFailed)
                        {
                            var errorSummary = playerError == null
                                ? "Unknown error"
                                : $"{playerError.GetType().Name}: {playerError.Message}";
                            await WriteLogAsync(
                                    logWriter,
                                    $"PLAYER error: {normalizedPlayer} ({errorSummary})")
                                .ConfigureAwait(false);
                        }
                        else if (playerGames > 0)
                        {
                            var duplicateDetail = playerDuplicatesSkipped > 0
                                ? $" duplicates={playerDuplicatesSkipped:N0}"
                                : string.Empty;
                            await WriteLogAsync(
                                    logWriter,
                                    $"PLAYER done: {normalizedPlayer} games={playerGames:N0} newPlayers={playerNewPlayers:N0}{duplicateDetail}")
                                .ConfigureAwait(false);
                        }
                        else if (playerDuplicatesSkipped > 0 || playerNewPlayers > 0)
                        {
                            await WriteLogAsync(
                                    logWriter,
                                    $"PLAYER done: {normalizedPlayer} games=0 newPlayers={playerNewPlayers:N0} duplicates={playerDuplicatesSkipped:N0}")
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteLogAsync(logWriter, $"PLAYER done: {normalizedPlayer} no games").ConfigureAwait(false);
                        }

                        if (discoveredPlayers is { Count: > 0 })
                        {
                            await WriteLogAsync(
                                    logWriter,
                                    $"PLAYER {normalizedPlayer} discovered {discoveredPlayers.Count:N0} new players")
                                .ConfigureAwait(false);
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
                else
                {
                    var messageParts = new List<string>(3);
                    if (playerGames > 0)
                    {
                        messageParts.Add($"{playerGames} games");
                    }

                    if (playerNewPlayers > 0)
                    {
                        messageParts.Add($"+{playerNewPlayers} new players");
                    }

                    if (playerDuplicatesSkipped > 0)
                    {
                        messageParts.Add($"{playerDuplicatesSkipped} duplicates skipped");
                    }

                    message = messageParts.Count > 0
                        ? $"{normalizedPlayer}: {string.Join(" | ", messageParts)}"
                        : $"{normalizedPlayer}: no games";
                }

                progress?.Report(new ChesscomMonthlyCrawlProgress(
                    message,
                    playersProcessed,
                    totalPlayers,
                    gamesSaved,
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
                await WriteLogAsync(
                        logWriter,
                        $"COMPLETE processed={playersProcessed:N0}/{totalPlayers:N0} " +
                        $"games={gamesSaved:N0} newPlayers={newPlayers:N0} " +
                        $"failed={failedPlayers:N0} seedSize={allPlayers.Count:N0}")
                    .ConfigureAwait(false);
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

        return new ChesscomMonthlyCrawlResult(
            playersProcessed,
            totalPlayers,
            failedPlayers,
            gamesSaved,
            newPlayers,
            allPlayers.Count);
    }

    private static HashSet<string> LoadPlayers(string path, bool required)
    {
        if (!File.Exists(path))
        {
            if (required)
            {
                throw new FileNotFoundException("Player list file not found.", path);
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(
            path,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: true);
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

    private static async Task<ChesscomMonthlyGamesResponse?> FetchMonthlyGamesAsync(
        string username,
        int year,
        int month,
        CancellationToken ct)
    {
        var safeUser = Uri.EscapeDataString(username.Trim());
        var url = new Uri($"https://api.chess.com/pub/player/{safeUser}/games/{year}/{month:D2}");
        Exception? lastError = null;
        var rateLimitRetries = 0;

        for (var attempt = 1; attempt <= MaxFailures; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await ApplyRateLimitAsync(ct).ConfigureAwait(false);

            try
            {
                using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    rateLimitRetries++;
                    if (rateLimitRetries >= MaxRateLimitRetries)
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    await Task.Delay(GetRateLimitDelay(response), ct).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryableStatusCode(response.StatusCode) && attempt < MaxFailures)
                    {
                        await Task.Delay(GetRetryDelay(attempt, response), ct).ConfigureAwait(false);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                }

                return await response.Content
                    .ReadFromJsonAsync<ChesscomMonthlyGamesResponse>(JsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxFailures && IsRetryableException(ex))
            {
                lastError = ex;
                await Task.Delay(GetRetryDelay(attempt), ct).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException(
            $"Failed to fetch Chess.com monthly games for '{username}' after {MaxFailures} attempts.",
            lastError);
    }

    private static bool IsEligibleGame(ChesscomMonthlyGame game, int minElo, bool excludeBullet)
    {
        if (game.White == null || game.Black == null)
        {
            return false;
        }

        if (excludeBullet && IsBulletGame(game))
        {
            return false;
        }

        return (game.White.Rating ?? 0) >= minElo && (game.Black.Rating ?? 0) >= minElo;
    }

    private static bool TryGetOpponent(string currentPlayer, ChesscomMonthlyGame game, out string opponent)
    {
        opponent = string.Empty;

        var white = NormalizeUsername(game.White?.Username);
        var black = NormalizeUsername(game.Black?.Username);

        if (string.IsNullOrWhiteSpace(white) || string.IsNullOrWhiteSpace(black))
        {
            return false;
        }

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

    private static async Task<bool> TryAddPlayerAsync(
        string opponent,
        HashSet<string> allPlayers,
        StreamWriter writer)
    {
        if (string.IsNullOrWhiteSpace(opponent))
        {
            return false;
        }

        if (!allPlayers.Add(opponent))
        {
            return false;
        }

        await writer.WriteLineAsync(opponent).ConfigureAwait(false);
        return true;
    }

    private static Task WriteLogAsync(StreamWriter? writer, string message)
    {
        if (writer == null)
        {
            return Task.CompletedTask;
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        return writer.WriteLineAsync($"[{timestamp}] {message}");
    }

    private static string NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return string.Empty;
        }

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
        while (length < text.Length && char.IsDigit(text[length]))
        {
            length++;
        }

        if (length == 0)
        {
            return false;
        }

        return int.TryParse(text[..length], out value);
    }

    private static bool IsDuplicateGame(HashSet<string> seenGameKeys, IReadOnlyList<string> gameKeys)
    {
        for (var i = 0; i < gameKeys.Count; i++)
        {
            if (seenGameKeys.Contains(gameKeys[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static void RegisterGameKeys(HashSet<string> seenGameKeys, IReadOnlyList<string> gameKeys)
    {
        for (var i = 0; i < gameKeys.Count; i++)
        {
            seenGameKeys.Add(gameKeys[i]);
        }
    }

    private static List<string> GetGameKeys(ChesscomMonthlyGame game)
    {
        var keys = new List<string>(3);
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

    private static async Task<HashSet<string>> LoadExistingGameKeysAsync(string outputPath, CancellationToken ct)
    {
        var gameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(outputPath))
        {
            return gameKeys;
        }

        await using var stream = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: BufferSize,
            leaveOpen: true);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
            {
                break;
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

        return gameKeys;
    }

    private static void AddGameKey(ICollection<string> target, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            target.Add(key);
        }
    }

    private static string? BuildUuidGameKey(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return null;
        }

        return $"uuid:{uuid.Trim().ToLowerInvariant()}";
    }

    private static string? BuildUrlGameKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return $"url:{url.Trim().TrimEnd('/').ToLowerInvariant()}";
    }

    private static string BuildPgnGameKey(string pgn)
    {
        var normalized = pgn.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return $"pgn:{Convert.ToHexString(SHA256.HashData(bytes))}";
    }

    private static bool TryExtractLinkFromPgn(string? pgn, out string link)
    {
        link = string.Empty;
        if (string.IsNullOrWhiteSpace(pgn))
        {
            return false;
        }

        using var reader = new StringReader(pgn);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryGetQuotedHeaderValue(line, "Link", out link))
            {
                return true;
            }
        }

        link = string.Empty;
        return false;
    }

    private static bool TryGetQuotedHeaderValue(string line, string headerName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        var prefix = $"[{headerName} \"";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !trimmed.EndsWith("\"]", StringComparison.Ordinal))
        {
            return false;
        }

        var rawValue = trimmed.Substring(prefix.Length, trimmed.Length - prefix.Length - 2).Trim();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        value = rawValue.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        return true;
    }

    private static Task ApplyRateLimitAsync(CancellationToken ct)
    {
        var delayMs = Random.Shared.Next(RateLimitMinMs, RateLimitMaxMs);
        return Task.Delay(delayMs, ct);
    }

    private static TimeSpan GetRateLimitDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
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

        return RateLimitBackoff;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsRetryableException(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException;

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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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

    private sealed class LockFileHandle(string path, FileStream stream) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stream.Dispose();
            TryDeleteLockFile(path);
        }

        private static void TryDeleteLockFile(string path)
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
    }
}
