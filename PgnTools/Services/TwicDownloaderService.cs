using System.IO.Compression;
using System.Net;
using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public sealed record TwicLatestIssueProbeResult(int Issue, bool IsConfirmed, string Source);

public class TwicDownloaderService : ITwicDownloaderService
{
    private const int BufferSize = 65536;
    private const int MinimumIssue = 920;
    private const int MaxDownloadAttempts = 3;
    private const int ConsecutiveProbeFailures = 3;
    private const int ProbeLookBehind = 10;
    private const int ProbeLookAhead = 10;
    private const long MaxZipBytes = 100L * 1024 * 1024;
    private const long MaxPgnEntryBytes = 250L * 1024 * 1024;

    // Anchor for estimation to reduce long-term calendar drift.
    // TWIC 1628 was published on 2026-01-19.
    private const int AnchorIssue = 1628;
    private static readonly DateTime AnchorIssueDate = new(2026, 1, 19, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Uri BaseUri = new("https://theweekinchess.com/zips/");
    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "theweekinchess.com",
        "www.theweekinchess.com"
    };

    private static readonly HttpClient HttpClient = CreateClient();
    private static readonly Random RandomJitter = Random.Shared;

    static TwicDownloaderService()
    {
        // Ensure legacy code pages (e.g., Windows-1252) are available.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<int> DownloadIssuesAsync(
        int start,
        int end,
        string outputFile,
        IProgress<string> status,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputFile)) throw new ArgumentException("Output file path is required.", nameof(outputFile));
        if (start < 1 || end < 1) throw new ArgumentOutOfRangeException(nameof(start), "Issue numbers must be positive.");
        if (end < start) throw new ArgumentException("End issue must be greater than or equal to start issue.");

        var outputFullPath = Path.GetFullPath(outputFile);
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);
        var tempFolder = Path.Combine(Path.GetTempPath(), $"PgnTools_Twic_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempFolder);
        if (Path.GetDirectoryName(outputFullPath) is { } dir) Directory.CreateDirectory(dir);

        var issuesWritten = 0;
        var isFirstGlobalEntry = true;
        var failures = new List<TwicIssueFailure>();

        try
        {
            await using (var outputStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize))
            {
                for (var issue = start; issue <= end; issue++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (issue > start)
                    {
                        var delayMs = RandomJitter.Next(2000, 4000);
                        await Task.Delay(delayMs, ct);
                    }

                    status.Report($"Downloading TWIC #{issue}...");

                    string? zipPath = null;
                    try
                    {
                        zipPath = await DownloadIssueZipWithRetryAsync(issue, tempFolder, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        AddFailure(failures, status, issue, "Download timed out.", ex);
                        continue;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                    {
                        AddFailure(failures, status, issue, ex.Message, ex);
                        continue;
                    }

                    if (zipPath == null)
                    {
                        AddFailure(failures, status, issue, "Issue not found (404).");
                        continue;
                    }

                    try
                    {
                        status.Report($"Processing TWIC #{issue}...");
                        var appended = await AppendIssueFromZipAsync(writer, zipPath, issue, isFirstGlobalEntry, ct);

                        if (appended)
                        {
                            isFirstGlobalEntry = false;
                            issuesWritten++;
                        }
                        else
                        {
                            AddFailure(failures, status, issue, "No PGN content found.");
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (InvalidDataException ex)
                    {
                        AddFailure(failures, status, issue, "Invalid ZIP archive or PGN entry.", ex);
                    }
                    catch (InvalidOperationException ex)
                    {
                        AddFailure(failures, status, issue, ex.Message, ex);
                    }
                    catch (IOException ex)
                    {
                        throw new IOException($"Output write or ZIP stream failure while processing TWIC #{issue}.", ex);
                    }
                    finally
                    {
                        TryDelete(zipPath);
                    }
                }

                await writer.FlushAsync(ct);
            }

            if (failures.Count > 0)
            {
                status.Report($"Download incomplete. Refusing to replace output. Succeeded: {issuesWritten:N0}; failed: {failures.Count:N0}.");
                CleanupTemp(tempOutputPath);
                throw new InvalidOperationException(BuildIncompleteDownloadMessage(issuesWritten, failures));
            }

            if (issuesWritten == 0)
            {
                status.Report("No issues were successfully downloaded.");
                CleanupTemp(tempOutputPath);
                return 0;
            }

            await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, ct);
            status.Report($"Done. {issuesWritten:N0} issues saved to {Path.GetFileName(outputFullPath)}");
            return issuesWritten;
        }
        catch (OperationCanceledException)
        {
            status.Report("Download cancelled.");
            CleanupTemp(tempOutputPath);
            throw;
        }
        catch (Exception ex)
        {
            ReportErrorAndPreserveTempIfUseful(status, ex, tempOutputPath);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
            }
            catch
            {
            }
        }
    }

    public static int CalculateEstimatedLatestIssue(DateTime? today = null)
    {
        var current = today ?? DateTime.UtcNow;
        var currentUtcDate = current.Kind switch
        {
            DateTimeKind.Local => current.ToUniversalTime().Date,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(current, DateTimeKind.Utc).Date,
            _ => current.Date
        };

        var daysPassed = (currentUtcDate - AnchorIssueDate.Date).TotalDays;
        var weeksPassed = (int)Math.Floor(daysPassed / 7.0);
        var estimated = AnchorIssue + weeksPassed;
        return Math.Max(MinimumIssue, estimated);
    }

    public static async Task<TwicLatestIssueProbeResult> ProbeLatestIssueAsync(int estimatedIssue, IProgress<string> status, CancellationToken ct)
    {
        var estimate = Math.Max(MinimumIssue, estimatedIssue);
        var probe = Math.Max(MinimumIssue, estimate - ProbeLookBehind);
        var maxProbe = estimate + ProbeLookAhead;
        var lastSuccess = 0;
        var hadSuccess = false;
        var failures = 0;

        while (failures < ConsecutiveProbeFailures && probe <= maxProbe)
        {
            ct.ThrowIfCancellationRequested();
            var url = new Uri(BaseUri, $"twic{probe}g.zip");

            status.Report($"Probing TWIC #{probe}...");

            try
            {
                using var response = await SendWithRedirectsAsync(HttpMethod.Get, url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.IsSuccessStatusCode)
                {
                    lastSuccess = probe;
                    hadSuccess = true;
                    failures = 0;
                }
                else
                {
                    failures++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                failures++;
            }

            probe++;

            if (failures < ConsecutiveProbeFailures && probe <= maxProbe)
            {
                await Task.Delay(400, ct);
            }
        }

        if (!hadSuccess)
        {
            status.Report($"Could not confirm latest issue; using estimate {estimate}.");
            return new TwicLatestIssueProbeResult(estimate, IsConfirmed: false, Source: "estimate");
        }

        status.Report($"Latest confirmed issue: {lastSuccess}");
        return new TwicLatestIssueProbeResult(lastSuccess, IsConfirmed: true, Source: "probe");
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static async Task<string?> DownloadIssueZipWithRetryAsync(int issue, string tempFolder, CancellationToken ct)
    {
        var fileName = $"twic{issue}g.zip";
        var url = new Uri(BaseUri, fileName);
        var outputPath = Path.Combine(tempFolder, fileName);

        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                using var response = await SendWithRedirectsAsync(HttpMethod.Get, url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;

                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaxZipBytes)
                {
                    throw new InvalidOperationException($"Download exceeded maximum allowed ZIP size of {MaxZipBytes:N0} bytes.");
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await CopyToAsyncLimited(contentStream, fileStream, MaxZipBytes, ct);

                return outputPath;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception) when (attempts < MaxDownloadAttempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempts - 1)), ct);
            }
        }
    }

    private static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        HttpMethod method,
        Uri url,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        const int maxRedirects = 5;
        var current = url;

        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(method, current);
            var response = await HttpClient.SendAsync(request, completionOption, ct);

            if (!IsRedirectStatusCode(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();

            if (location == null)
            {
                throw new HttpRequestException($"Redirect response from {current} did not include a Location header.");
            }

            current = ValidateRedirect(current, location);
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

    private static async Task<bool> AppendIssueFromZipAsync(StreamWriter writer, string zipPath, int issue, bool isFirstGlobalEntry, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals($"twic{issue}.pgn", StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase));

        if (entry == null) return false;

        if (entry.Length > MaxPgnEntryBytes)
        {
            throw new InvalidOperationException($"PGN entry exceeded maximum allowed size of {MaxPgnEntryBytes:N0} bytes.");
        }

        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!isFirstGlobalEntry)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync();
                await writer.WriteLineAsync();
            }

            await writer.WriteLineAsync(line.AsMemory(), ct);
            await CopyRemainingPgnTextAsync(reader, writer, line.Length + Environment.NewLine.Length, ct);
            return true;
        }

        return false;
    }

    private static async Task CopyToAsyncLimited(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long totalBytes = 0;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }

            totalBytes += bytesRead;
            if (totalBytes > maxBytes)
            {
                throw new InvalidOperationException($"Download exceeded maximum allowed size of {maxBytes:N0} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
        }
    }

    private static async Task CopyRemainingPgnTextAsync(StreamReader reader, StreamWriter writer, long charactersWritten, CancellationToken ct)
    {
        var buffer = new char[BufferSize];
        var totalCharacters = charactersWritten;

        while (true)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (charsRead == 0)
            {
                return;
            }

            totalCharacters += charsRead;
            if (totalCharacters > MaxPgnEntryBytes)
            {
                throw new InvalidOperationException($"PGN content exceeded maximum allowed size of {MaxPgnEntryBytes:N0} characters.");
            }

            await writer.WriteAsync(buffer.AsMemory(0, charsRead), ct).ConfigureAwait(false);
        }
    }

    private static void AddFailure(List<TwicIssueFailure> failures, IProgress<string> status, int issue, string reason, Exception? exception = null)
    {
        failures.Add(new TwicIssueFailure(issue, reason, exception));
        status.Report($"Failed TWIC #{issue}: {reason}");
    }

    private static string BuildIncompleteDownloadMessage(int issuesWritten, IReadOnlyList<TwicIssueFailure> failures)
    {
        var examples = string.Join("; ", failures.Take(5).Select(f => $"#{f.Issue}: {f.Reason}"));
        var suffix = failures.Count > 5 ? $" (+{failures.Count - 5:N0} more)" : string.Empty;

        return $"TWIC download incomplete. Refusing to replace the existing output file. " +
               $"Succeeded: {issuesWritten:N0}; failed: {failures.Count:N0}. {examples}{suffix}";
    }

    private static void ReportErrorAndPreserveTempIfUseful(IProgress<string> status, Exception ex, string tempOutputPath)
    {
        if (TryGetFileLength(tempOutputPath) > 0)
        {
            status.Report($"Error: {ex.Message} Partial output preserved at {tempOutputPath}");
            return;
        }

        CleanupTemp(tempOutputPath);
        status.Report($"Error: {ex.Message}");
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void CleanupTemp(string path)
    {
        if (File.Exists(path)) TryDelete(path);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record TwicIssueFailure(int Issue, string Reason, Exception? Exception);
}
