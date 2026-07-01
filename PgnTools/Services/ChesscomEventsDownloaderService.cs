using System.Globalization;
using System.Net;
using System.Text;

namespace PgnTools.Services;

public sealed record ChesscomEventsDownloadOptions(
    int StartEventId,
    int EndEventId,
    string OutputFilePath,
    string TitledTuesdayOutputFilePath,
    string StatusFilePath,
    string? CookieHeader = null,
    bool ResumeFromStatus = true,
    int MinDelayMs = 0,
    int MaxDelayMs = 0,
    int MaxRetries = 3,
    long MaxEventBytes = 100L * 1024L * 1024L,
    bool InferEveryFifthDeadIds = true);

public sealed record ChesscomEventsDownloadProgress(
    string Message,
    int CurrentEventId,
    int Processed,
    int Total,
    int Saved,
    int Missing,
    int NonPgn,
    int Failed,
    int Skipped,
    long BytesWritten,
    int TitledTuesdaySaved = 0,
    long TitledTuesdayBytesWritten = 0);

public sealed record ChesscomEventsDownloadResult(
    int Processed,
    int Total,
    int Saved,
    int Missing,
    int NonPgn,
    int Failed,
    int Skipped,
    long BytesWritten,
    int TitledTuesdaySaved = 0,
    long TitledTuesdayBytesWritten = 0);

public interface IChesscomEventsDownloaderService
{
    Task<ChesscomEventsDownloadResult> DownloadEventsAsync(
        ChesscomEventsDownloadOptions options,
        IProgress<ChesscomEventsDownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class ChesscomEventsDownloaderService : IChesscomEventsDownloaderService
{
    private const int BufferSize = 65536;
    private const int MaxRedirects = 5;
    private const int MaxConsecutiveFailures = 5;

    // Adaptive serial pacing. Chess.com throttles aggressive callers, so instead of
    // bursting and eating multi-minute stalls, we nudge a per-request delay upward the
    // moment a throttle appears and let it decay back toward the user floor on a clean run.
    private const int AdaptiveCeilingMs = 5000;
    private const int AdaptiveThrottleFloorMs = 250;
    private const int AdaptiveDecayMs = 100;

    // Cap for both Retry-After waits and the exponential fallback backoff.
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(65);
    private static readonly Uri EventsBaseUri = new("https://www.chess.com/events/pgn/");
    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "chess.com",
        "www.chess.com"
    };

    private static readonly HttpClient HttpClient = CreateClient();

    public async Task<ChesscomEventsDownloadResult> DownloadEventsAsync(
        ChesscomEventsDownloadOptions options,
        IProgress<ChesscomEventsDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ValidateOptions(options);

        var outputPath = Path.GetFullPath(options.OutputFilePath);
        var titledTuesdayOutputPath = Path.GetFullPath(options.TitledTuesdayOutputFilePath);
        var statusPath = Path.GetFullPath(options.StatusFilePath);

        if (string.Equals(outputPath, statusPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Output PGN and status CSV paths must be different files.");
        }

        if (string.Equals(titledTuesdayOutputPath, statusPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(titledTuesdayOutputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Main output PGN, Titled Tuesday PGN, and status CSV paths must be different files.");
        }

        CreateParentDirectory(outputPath);
        CreateParentDirectory(titledTuesdayOutputPath);
        CreateParentDirectory(statusPath);

        var resumeOutput = options.ResumeFromStatus && File.Exists(outputPath);
        var resumeTitledTuesdayOutput = options.ResumeFromStatus && File.Exists(titledTuesdayOutputPath);
        var resumeStatus = options.ResumeFromStatus && File.Exists(statusPath);
        var resumeState = resumeStatus
            ? LoadResumeState(statusPath, options.InferEveryFifthDeadIds)
            : ResumeState.Empty;

        var total = options.EndEventId - options.StartEventId + 1;
        var processed = 0;
        var saved = 0;
        var missing = 0;
        var nonPgn = 0;
        var failed = 0;
        var skipped = 0;
        var titledTuesdaySaved = 0;
        var consecutiveFailures = 0;
        long bytesWritten = 0;
        long titledTuesdayBytesWritten = 0;

        // Pacing state: starts at the user-configured floor and adapts to throttling.
        var floorDelayMs = Math.Max(0, options.MinDelayMs);
        var adaptiveDelayMs = floorDelayMs;
        var requestsSent = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        progress?.Report(new ChesscomEventsDownloadProgress(
            resumeStatus
                ? $"Resuming Chess.com event scan with {resumeState.KnownStatusCount:N0} recorded event status row(s)..."
                : "Starting Chess.com event scan...",
            options.StartEventId,
            0,
            total,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0));

        await using var outputStream = new FileStream(
            outputPath,
            resumeOutput ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        outputStream.Seek(0, SeekOrigin.End);

        await using var statusStream = new FileStream(
            statusPath,
            resumeStatus ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        statusStream.Seek(0, SeekOrigin.End);

        await using var titledTuesdayOutputStream = new FileStream(
            titledTuesdayOutputPath,
            resumeTitledTuesdayOutput ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        titledTuesdayOutputStream.Seek(0, SeekOrigin.End);

        using var outputWriter = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);
        using var titledTuesdayOutputWriter = new StreamWriter(titledTuesdayOutputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);
        using var statusWriter = new StreamWriter(statusStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

        if (statusStream.Length == 0)
        {
            await statusWriter.WriteLineAsync("TimestampUtc,EventId,Result,HttpStatus,Bytes,Games,Message,Url,OutputKind");
        }

        var outputHasContent = outputStream.Length > 0;
        var titledTuesdayOutputHasContent = titledTuesdayOutputStream.Length > 0;

        for (var eventId = options.StartEventId; eventId <= options.EndEventId; eventId++)
        {
            ct.ThrowIfCancellationRequested();

            if (TryGetResumeSkip(eventId, resumeState, resumeOutput, resumeTitledTuesdayOutput, out var skipResult, out var skipMessage))
            {
                processed++;
                skipped++;

                switch (skipResult.Status)
                {
                    case EventFetchStatus.Success:
                        saved++;
                        if (skipResult.OutputKind == EventOutputKind.TitledTuesday)
                        {
                            titledTuesdaySaved++;
                        }
                        break;

                    case EventFetchStatus.Missing:
                    case EventFetchStatus.Gone:
                        missing++;
                        break;

                    case EventFetchStatus.NonPgn:
                    case EventFetchStatus.SkippedByPattern:
                        nonPgn++;
                        break;
                }

                if (skipResult.Status == EventFetchStatus.SkippedByPattern)
                {
                    await WriteStatusAsync(statusWriter, eventId, skipResult, BuildEventUrl(eventId), ct).ConfigureAwait(false);
                }

                progress?.Report(BuildProgress(
                    skipMessage,
                    eventId,
                    processed,
                    total,
                    saved,
                    missing,
                    nonPgn,
                    failed,
                    skipped,
                    bytesWritten,
                    titledTuesdaySaved,
                    titledTuesdayBytesWritten));
                continue;
            }

            progress?.Report(BuildProgress(
                $"Downloading event {eventId:N0} ({processed + 1:N0}/{total:N0}){DescribeThroughput(requestsSent, stopwatch.Elapsed, options.EndEventId - eventId, adaptiveDelayMs)}...",
                eventId,
                processed,
                total,
                saved,
                missing,
                nonPgn,
                failed,
                skipped,
                bytesWritten,
                titledTuesdaySaved,
                titledTuesdayBytesWritten));

            await DelayAsync(adaptiveDelayMs, ct).ConfigureAwait(false);
            var url = BuildEventUrl(eventId);
            requestsSent++;
            var fetch = await FetchEventPgnWithRetryAsync(eventId, url, options, ct).ConfigureAwait(false);

            // React to throttling: back off the pace on any throttle hit, relax it on a clean request.
            adaptiveDelayMs = fetch.ThrottleHits > 0
                ? Math.Min(AdaptiveCeilingMs, Math.Max(adaptiveDelayMs, AdaptiveThrottleFloorMs) * 2)
                : Math.Max(floorDelayMs, adaptiveDelayMs - AdaptiveDecayMs);

            if (fetch.Status == EventFetchStatus.AuthRequired)
            {
                await WriteStatusAsync(statusWriter, eventId, fetch, url, ct).ConfigureAwait(false);
                throw new InvalidOperationException("Chess.com redirected event PGNs to login. Use Open Sign In in the Events Downloader tab, then try again.");
            }

            if (fetch.Status == EventFetchStatus.RateLimited)
            {
                await WriteStatusAsync(statusWriter, eventId, fetch, url, ct).ConfigureAwait(false);
                throw new InvalidOperationException("Chess.com returned repeated rate-limit responses. The scan stopped before making more requests.");
            }

            processed++;

            switch (fetch.Status)
            {
                case EventFetchStatus.Success:
                    var outputKind = IsTitledTuesdayPgn(fetch.Pgn)
                        ? EventOutputKind.TitledTuesday
                        : EventOutputKind.Main;
                    fetch = fetch with { OutputKind = outputKind };

                    if (outputKind == EventOutputKind.TitledTuesday)
                    {
                        if (titledTuesdayOutputHasContent)
                        {
                            await titledTuesdayOutputWriter.WriteLineAsync().ConfigureAwait(false);
                            await titledTuesdayOutputWriter.WriteLineAsync().ConfigureAwait(false);
                        }

                        await titledTuesdayOutputWriter.WriteLineAsync(fetch.Pgn.AsMemory(), ct).ConfigureAwait(false);
                        titledTuesdayOutputHasContent = true;
                        titledTuesdayBytesWritten += fetch.Bytes;
                        titledTuesdaySaved++;
                    }
                    else
                    {
                        if (outputHasContent)
                        {
                            await outputWriter.WriteLineAsync().ConfigureAwait(false);
                            await outputWriter.WriteLineAsync().ConfigureAwait(false);
                        }

                        await outputWriter.WriteLineAsync(fetch.Pgn.AsMemory(), ct).ConfigureAwait(false);
                        outputHasContent = true;
                        bytesWritten += fetch.Bytes;
                    }

                    saved++;
                    consecutiveFailures = 0;
                    break;

                case EventFetchStatus.Missing:
                case EventFetchStatus.Gone:
                    missing++;
                    consecutiveFailures = 0;
                    break;

                case EventFetchStatus.NonPgn:
                    nonPgn++;
                    consecutiveFailures = 0;
                    break;

                default:
                    failed++;
                    consecutiveFailures++;
                    break;
            }

            await WriteStatusAsync(statusWriter, eventId, fetch, url, ct).ConfigureAwait(false);

            progress?.Report(BuildProgress(
                BuildStatusMessage(eventId, fetch),
                eventId,
                processed,
                total,
                saved,
                missing,
                nonPgn,
                failed,
                skipped,
                bytesWritten,
                titledTuesdaySaved,
                titledTuesdayBytesWritten));

            if (consecutiveFailures >= MaxConsecutiveFailures)
            {
                throw new InvalidOperationException($"Stopped after {MaxConsecutiveFailures:N0} consecutive failed event requests.");
            }
        }

        return new ChesscomEventsDownloadResult(
            processed,
            total,
            saved,
            missing,
            nonPgn,
            failed,
            skipped,
            bytesWritten,
            titledTuesdaySaved,
            titledTuesdayBytesWritten);
    }

    private static void ValidateOptions(ChesscomEventsDownloadOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.StartEventId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.StartEventId), "Start event ID must be greater than zero.");
        }

        if (options.EndEventId < options.StartEventId)
        {
            throw new ArgumentException("End event ID must be greater than or equal to start event ID.");
        }

        if (string.IsNullOrWhiteSpace(options.OutputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(options.OutputFilePath));
        }

        if (string.IsNullOrWhiteSpace(options.TitledTuesdayOutputFilePath))
        {
            throw new ArgumentException("Titled Tuesday output file path is required.", nameof(options.TitledTuesdayOutputFilePath));
        }

        if (string.IsNullOrWhiteSpace(options.StatusFilePath))
        {
            throw new ArgumentException("Status file path is required.", nameof(options.StatusFilePath));
        }

        if (options.MinDelayMs < 0 || options.MaxDelayMs < 0 || options.MaxDelayMs < options.MinDelayMs)
        {
            throw new ArgumentException("Request delay range is invalid.");
        }

        if (options.MaxRetries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxRetries), "Max retries must be greater than zero.");
        }

        if (options.MaxEventBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxEventBytes), "Max event bytes must be greater than zero.");
        }
    }

    private static async Task<EventFetchResult> FetchEventPgnWithRetryAsync(
        int eventId,
        Uri url,
        ChesscomEventsDownloadOptions options,
        CancellationToken ct)
    {
        var throttleHits = 0;

        for (var attempt = 1; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                var result = await FetchEventPgnAsync(eventId, url, options.CookieHeader, options.MaxEventBytes, ct)
                    .ConfigureAwait(false);
                return throttleHits > 0 ? result with { ThrottleHits = throttleHits } : result;
            }
            catch (RateLimitException ex) when (attempt < options.MaxRetries)
            {
                throttleHits++;
                var wait = ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                    ? (retryAfter < MaxBackoff ? retryAfter : MaxBackoff)
                    : FallbackBackoff(attempt);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (RateLimitException ex)
            {
                return new EventFetchResult(EventFetchStatus.RateLimited, HttpStatusCode.TooManyRequests, string.Empty, 0, 0, ex.Message, ThrottleHits: throttleHits + 1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (attempt >= options.MaxRetries)
            {
                return new EventFetchResult(EventFetchStatus.Failed, null, string.Empty, 0, 0, $"Timed out: {ex.Message}");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                if (attempt >= options.MaxRetries)
                {
                    return new EventFetchResult(EventFetchStatus.Failed, null, string.Empty, 0, 0, ex.Message);
                }

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        return new EventFetchResult(EventFetchStatus.Failed, null, string.Empty, 0, 0, "Request failed.");
    }

    private static async Task<EventFetchResult> FetchEventPgnAsync(
        int eventId,
        Uri url,
        string? cookieHeader,
        long maxEventBytes,
        CancellationToken ct)
    {
        using var response = await SendWithRedirectsAsync(url, cookieHeader, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new EventFetchResult(EventFetchStatus.AuthRequired, response.StatusCode, string.Empty, 0, 0, "Authentication required.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new EventFetchResult(EventFetchStatus.Missing, response.StatusCode, string.Empty, 0, 0, "Not found.");
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return new EventFetchResult(EventFetchStatus.Gone, response.StatusCode, string.Empty, 0, 0, "Gone.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RateLimitException($"Rate limited while requesting event {eventId:N0}.", GetRetryAfter(response));
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new HttpRequestException($"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new EventFetchResult(EventFetchStatus.Failed, response.StatusCode, string.Empty, 0, 0, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxEventBytes)
        {
            return new EventFetchResult(EventFetchStatus.Failed, response.StatusCode, string.Empty, 0, 0, $"Response exceeded {maxEventBytes:N0} bytes.");
        }

        var (content, bytesRead) = await ReadContentLimitedAsync(response, maxEventBytes, ct).ConfigureAwait(false);

        if (LooksLikeLoginPage(content))
        {
            return new EventFetchResult(EventFetchStatus.AuthRequired, response.StatusCode, string.Empty, 0, bytesRead, "Authentication required.");
        }

        var pgn = content.Trim();
        if (!LooksLikePgn(pgn))
        {
            return new EventFetchResult(EventFetchStatus.NonPgn, response.StatusCode, string.Empty, 0, bytesRead, "Response was not PGN.");
        }

        return new EventFetchResult(
            EventFetchStatus.Success,
            response.StatusCode,
            pgn,
            CountGames(pgn),
            bytesRead,
            "Saved.");
    }

    private static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri url,
        string? cookieHeader,
        CancellationToken ct)
    {
        var current = url;

        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
            request.Headers.Accept.ParseAdd("application/x-chess-pgn");
            request.Headers.Accept.ParseAdd("text/plain;q=0.9");
            request.Headers.Accept.ParseAdd("*/*;q=0.5");

            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!IsRedirectStatusCode(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location == null)
            {
                return response;
            }

            var next = ValidateRedirect(current, location);
            if (IsLoginRedirect(next))
            {
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    ReasonPhrase = "Redirected to Chess.com login"
                };
            }

            response.Dispose();
            current = next;
        }

        throw new HttpRequestException($"Too many redirects when requesting {url}.");
    }

    private static Uri ValidateRedirect(Uri current, Uri location)
    {
        var next = location.IsAbsoluteUri ? location : new Uri(current, location);

        if (next.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException($"Refusing non-HTTPS redirect to {next.Host}.");
        }

        if (!AllowedRedirectHosts.Contains(next.Host))
        {
            throw new HttpRequestException($"Refusing redirect from {current.Host} to {next.Host}.");
        }

        return next;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.Moved ||
        statusCode == HttpStatusCode.Redirect ||
        statusCode == HttpStatusCode.RedirectMethod ||
        statusCode == HttpStatusCode.TemporaryRedirect ||
        statusCode == HttpStatusCode.PermanentRedirect;

    private static bool IsLoginRedirect(Uri uri) =>
        uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath.Contains("login_and_go", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Content, long BytesRead)> ReadContentLimitedAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken ct)
    {
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[BufferSize];
        long total = 0;

        while (true)
        {
            var read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"Response exceeded {maxBytes:N0} bytes.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return (Encoding.UTF8.GetString(memory.ToArray()), total);
    }

    private static bool LooksLikeLoginPage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.Contains("login_and_go", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Sign In", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePgn(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.TrimStart('\uFEFF', '\r', '\n', '\t', ' ');
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.Contains("[Event ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("[Site ", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountGames(string pgn)
    {
        var count = 0;
        using var reader = new StringReader(pgn);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
        if (line.StartsWith("[Event ", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsTitledTuesdayPgn(string pgn)
    {
        using var reader = new StringReader(pgn);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            if (!line.StartsWith("[Event ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("Titled Tuesday", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Uri BuildEventUrl(int eventId) => new(EventsBaseUri, $"{eventId.ToString(CultureInfo.InvariantCulture)}/0");

    private static async Task DelayAsync(int delayMs, CancellationToken ct)
    {
        if (delayMs <= 0)
        {
            return;
        }

        await Task.Delay(delayMs, ct).ConfigureAwait(false);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait;
            }
        }

        return null;
    }

    // Exponential fallback used only when the 429 response carries no Retry-After header: 3s, 6s, 12s, ... capped.
    private static TimeSpan FallbackBackoff(int attempt)
    {
        var seconds = 3d * Math.Pow(2, attempt - 1);
        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }

    private static string DescribeThroughput(int requestsSent, TimeSpan elapsed, int remaining, int adaptiveDelayMs)
    {
        if (requestsSent < 5 || elapsed.TotalSeconds < 1)
        {
            return string.Empty;
        }

        var rate = requestsSent / elapsed.TotalSeconds;
        if (rate <= 0)
        {
            return string.Empty;
        }

        var suffix = $" • {rate:0.0} req/s";

        if (remaining > 0)
        {
            var eta = TimeSpan.FromSeconds(remaining / rate);
            suffix += eta.TotalHours >= 1
                ? $" • ETA {(int)eta.TotalHours}h{eta.Minutes:00}m"
                : $" • ETA {eta.Minutes}m{eta.Seconds:00}s";
        }

        if (adaptiveDelayMs > 0)
        {
            suffix += $" • pacing {adaptiveDelayMs:N0}ms";
        }

        return suffix;
    }

    private static ChesscomEventsDownloadProgress BuildProgress(
        string message,
        int currentEventId,
        int processed,
        int total,
        int saved,
        int missing,
        int nonPgn,
        int failed,
        int skipped,
        long bytesWritten,
        int titledTuesdaySaved,
        long titledTuesdayBytesWritten) =>
        new(
            message,
            currentEventId,
            processed,
            total,
            saved,
            missing,
            nonPgn,
            failed,
            skipped,
            bytesWritten,
            titledTuesdaySaved,
            titledTuesdayBytesWritten);

    private static string BuildStatusMessage(int eventId, EventFetchResult fetch) =>
        fetch.Status switch
        {
            EventFetchStatus.Success => $"Saved event {eventId:N0} ({fetch.Games:N0} game(s)).",
            EventFetchStatus.Missing => $"Event {eventId:N0} not found.",
            EventFetchStatus.Gone => $"Event {eventId:N0} is gone.",
            EventFetchStatus.NonPgn => $"Event {eventId:N0} returned non-PGN content.",
            EventFetchStatus.SkippedByPattern => $"Skipped event {eventId:N0} using inferred dead-link pattern.",
            _ => $"Failed event {eventId:N0}: {fetch.Message}"
        };

    private static async Task WriteStatusAsync(
        StreamWriter writer,
        int eventId,
        EventFetchResult fetch,
        Uri url,
        CancellationToken ct)
    {
        var statusCode = fetch.HttpStatus.HasValue
            ? ((int)fetch.HttpStatus.Value).ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        var line = string.Join(
            ",",
            EscapeCsv(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            eventId.ToString(CultureInfo.InvariantCulture),
            EscapeCsv(fetch.Status.ToString()),
            statusCode,
            fetch.Bytes.ToString(CultureInfo.InvariantCulture),
            fetch.Games.ToString(CultureInfo.InvariantCulture),
            EscapeCsv(fetch.Message),
            EscapeCsv(url.ToString()),
            EscapeCsv(ToOutputKindCsv(fetch.OutputKind)));

        await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
    }

    private static ResumeState LoadResumeState(string statusPath, bool inferEveryFifthDeadIds)
    {
        var successIds = new HashSet<int>();
        var successOutputKinds = new Dictionary<int, EventOutputKind>();
        var missingIds = new HashSet<int>();
        var goneIds = new HashSet<int>();
        var nonPgnIds = new HashSet<int>();
        var exactDeadIds = new HashSet<int>();
        var statusRows = 0;
        var moduloStats = new DeadModuloStats[5];
        for (var i = 0; i < moduloStats.Length; i++)
        {
            moduloStats[i] = new DeadModuloStats();
        }

        foreach (var line in File.ReadLines(statusPath))
        {
            if (line.StartsWith("TimestampUtc,", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = SplitCsvLine(line, 9);
            if (fields.Count < 3)
            {
                continue;
            }

            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
            {
                continue;
            }

            var status = NormalizeStatus(fields[2]);
            if (status == null)
            {
                continue;
            }

            var outputKind = fields.Count >= 9
                ? NormalizeOutputKind(fields[8])
                : EventOutputKind.Unknown;

            statusRows++;
            var residue = Mod(eventId, 5);
            switch (status.Value)
            {
                case EventFetchStatus.Success:
                    successIds.Add(eventId);
                    successOutputKinds[eventId] = outputKind;
                    moduloStats[residue].Success++;
                    break;

                case EventFetchStatus.Missing:
                    missingIds.Add(eventId);
                    exactDeadIds.Add(eventId);
                    moduloStats[residue].Dead++;
                    break;

                case EventFetchStatus.Gone:
                    goneIds.Add(eventId);
                    exactDeadIds.Add(eventId);
                    moduloStats[residue].Dead++;
                    break;

                case EventFetchStatus.NonPgn:
                case EventFetchStatus.SkippedByPattern:
                    nonPgnIds.Add(eventId);
                    exactDeadIds.Add(eventId);
                    moduloStats[residue].Dead++;
                    break;
            }
        }

        var inferredDeadResidues = inferEveryFifthDeadIds
            ? InferDeadResidues(moduloStats)
            : new HashSet<int>();

        return new ResumeState(
            successIds,
            successOutputKinds,
            missingIds,
            goneIds,
            nonPgnIds,
            exactDeadIds,
            inferredDeadResidues,
            statusRows);
    }

    private static bool TryGetResumeSkip(
        int eventId,
        ResumeState resumeState,
        bool resumeOutput,
        bool resumeTitledTuesdayOutput,
        out EventFetchResult result,
        out string message)
    {
        if (resumeState.SuccessIds.Contains(eventId))
        {
            var outputKind = resumeState.SuccessOutputKinds.GetValueOrDefault(eventId, EventOutputKind.Unknown);
            var canSkip = outputKind switch
            {
                EventOutputKind.Main => resumeOutput,
                EventOutputKind.TitledTuesday => resumeTitledTuesdayOutput,
                _ => resumeOutput && resumeTitledTuesdayOutput
            };

            if (canSkip)
            {
                result = new EventFetchResult(
                    EventFetchStatus.Success,
                    null,
                    string.Empty,
                    0,
                    0,
                    "Skipped from status CSV.",
                    outputKind);
                message = outputKind == EventOutputKind.TitledTuesday
                    ? $"Skipped event {eventId:N0}; already saved in resumed Titled Tuesday output."
                    : $"Skipped event {eventId:N0}; already saved in resumed output.";
                return true;
            }
        }

        if (resumeState.MissingIds.Contains(eventId))
        {
            result = new EventFetchResult(EventFetchStatus.Missing, null, string.Empty, 0, 0, "Skipped known missing event from status CSV.");
            message = $"Skipped event {eventId:N0}; status CSV says missing.";
            return true;
        }

        if (resumeState.GoneIds.Contains(eventId))
        {
            result = new EventFetchResult(EventFetchStatus.Gone, null, string.Empty, 0, 0, "Skipped known gone event from status CSV.");
            message = $"Skipped event {eventId:N0}; status CSV says gone.";
            return true;
        }

        if (resumeState.NonPgnIds.Contains(eventId))
        {
            result = new EventFetchResult(EventFetchStatus.NonPgn, null, string.Empty, 0, 0, "Skipped known non-PGN event from status CSV.");
            message = $"Skipped event {eventId:N0}; status CSV says non-PGN.";
            return true;
        }

        if (resumeState.InferredDeadResidues.Contains(Mod(eventId, 5)) &&
            !resumeState.SuccessIds.Contains(eventId) &&
            !resumeState.ExactDeadIds.Contains(eventId))
        {
            result = new EventFetchResult(
                EventFetchStatus.SkippedByPattern,
                null,
                string.Empty,
                0,
                0,
                $"Skipped because prior status CSV rows indicate IDs with eventId % 5 = {Mod(eventId, 5)} are dead links.");
            message = $"Skipped event {eventId:N0}; inferred every-fifth dead-link pattern.";
            return true;
        }

        result = default!;
        message = string.Empty;
        return false;
    }

    private static HashSet<int> InferDeadResidues(IReadOnlyList<DeadModuloStats> moduloStats)
    {
        const int minimumDeadRows = 20;
        var inferred = new HashSet<int>();

        for (var residue = 0; residue < moduloStats.Count; residue++)
        {
            var stats = moduloStats[residue];
            if (stats.Dead >= minimumDeadRows && stats.Success == 0)
            {
                inferred.Add(residue);
            }
        }

        return inferred;
    }

    private static EventFetchStatus? NormalizeStatus(string status)
    {
        var normalized = status.Trim().Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (normalized.Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            return EventFetchStatus.Success;
        }

        if (normalized.Equals("Missing", StringComparison.OrdinalIgnoreCase))
        {
            return EventFetchStatus.Missing;
        }

        if (normalized.Equals("Gone", StringComparison.OrdinalIgnoreCase))
        {
            return EventFetchStatus.Gone;
        }

        if (normalized.Equals("NonPgn", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("NonPgnFile", StringComparison.OrdinalIgnoreCase))
        {
            return EventFetchStatus.NonPgn;
        }

        if (normalized.Equals("SkippedByPattern", StringComparison.OrdinalIgnoreCase))
        {
            return EventFetchStatus.SkippedByPattern;
        }

        return null;
    }

    private static string ToOutputKindCsv(EventOutputKind outputKind) =>
        outputKind switch
        {
            EventOutputKind.Main => "Main",
            EventOutputKind.TitledTuesday => "TitledTuesday",
            _ => string.Empty
        };

    private static EventOutputKind NormalizeOutputKind(string outputKind)
    {
        var normalized = outputKind.Trim().Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (normalized.Equals("Main", StringComparison.OrdinalIgnoreCase))
        {
            return EventOutputKind.Main;
        }

        if (normalized.Equals("TitledTuesday", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("TitledTuesdays", StringComparison.OrdinalIgnoreCase))
        {
            return EventOutputKind.TitledTuesday;
        }

        return EventOutputKind.Unknown;
    }

    private static int Mod(int value, int divisor) => ((value % divisor) + divisor) % divisor;

    private static List<string> SplitCsvLine(string line, int maxFields)
    {
        var fields = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes && fields.Count + 1 < maxFields)
            {
                fields.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(c);
        }

        fields.Add(builder.ToString());
        return fields;
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void CreateParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        return client;
    }

    private sealed record EventFetchResult(
        EventFetchStatus Status,
        HttpStatusCode? HttpStatus,
        string Pgn,
        int Games,
        long Bytes,
        string Message,
        EventOutputKind OutputKind = EventOutputKind.Unknown,
        int ThrottleHits = 0);

    private sealed record ResumeState(
        HashSet<int> SuccessIds,
        Dictionary<int, EventOutputKind> SuccessOutputKinds,
        HashSet<int> MissingIds,
        HashSet<int> GoneIds,
        HashSet<int> NonPgnIds,
        HashSet<int> ExactDeadIds,
        HashSet<int> InferredDeadResidues,
        int KnownStatusCount)
    {
        public static ResumeState Empty { get; } = new(
            new HashSet<int>(),
            new Dictionary<int, EventOutputKind>(),
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int>(),
            0);
    }

    private sealed class DeadModuloStats
    {
        public int Success { get; set; }
        public int Dead { get; set; }
    }

    private enum EventOutputKind
    {
        Unknown,
        Main,
        TitledTuesday
    }

    private enum EventFetchStatus
    {
        Success,
        Missing,
        Gone,
        AuthRequired,
        NonPgn,
        Failed,
        RateLimited,
        SkippedByPattern
    }

    private sealed class RateLimitException(string message, TimeSpan? retryAfter = null) : Exception(message)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
