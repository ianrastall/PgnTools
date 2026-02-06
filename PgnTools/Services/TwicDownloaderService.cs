using System.IO.Compression;
using System.Net;
using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public class TwicDownloaderService : ITwicDownloaderService
{
    private const int BufferSize = 65536;
    private const int MinimumIssue = 920;

    // Anchor for estimation to reduce long-term calendar drift.
    // TWIC 1628 was published on 2026-01-19.
    private const int AnchorIssue = 1628;
    private static readonly DateTime AnchorIssueDate = new(2026, 1, 19, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Uri BaseUri = new("https://www.theweekinchess.com/zips/");

    // Use a shared client to respect socket pooling, but configure timeouts
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
        // Use a unique temp folder to avoid collisions
        var tempFolder = Path.Combine(Path.GetTempPath(), $"PgnTools_Twic_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempFolder);
        if (Path.GetDirectoryName(outputFullPath) is { } dir) Directory.CreateDirectory(dir);

        var issuesWritten = 0;
        var isFirstGlobalEntry = true;

        try
        {
            await using (var outputStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize))
            {
                for (var issue = start; issue <= end; issue++)
                {
                    ct.ThrowIfCancellationRequested();

                    // RATE LIMITING:
                    // Wait 2 to 4 seconds between requests to be respectful.
                    // If this is the very first request, we don't need to wait.
                    if (issuesWritten > 0 || issue > start)
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
                        status.Report($"Failed to download #{issue}: {ex.Message}");
                        continue;
                    }
                    catch (HttpRequestException ex)
                    {
                        status.Report($"Failed to download #{issue}: {ex.Message}");
                        continue;
                    }
                    catch (IOException ex)
                    {
                        status.Report($"Failed to download #{issue}: {ex.Message}");
                        continue;
                    }

                    if (zipPath == null)
                    {
                        status.Report($"Issue #{issue} not found (404).");
                        continue;
                    }

                    status.Report($"Processing TWIC #{issue}...");
                    var appended = await AppendIssueFromZipAsync(writer, zipPath, issue, isFirstGlobalEntry, ct);

                    // Cleanup ZIP immediately to save disk space
                    try { File.Delete(zipPath); } catch { /* Ignore file lock issues on temp files */ }

                    if (appended)
                    {
                        isFirstGlobalEntry = false;
                        issuesWritten++;
                    }
                    else
                    {
                        status.Report($"No PGN content found in #{issue}.");
                    }
                }

                await writer.FlushAsync(ct);
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
            status.Report($"Error: {ex.Message}");
            CleanupTemp(tempOutputPath);
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

    private static void CleanupTemp(string path)
    {
        if (File.Exists(path)) try { File.Delete(path); } catch { }
    }

    public static int CalculateEstimatedLatestIssue(DateTime? today = null)
    {
        var current = (today ?? DateTime.UtcNow).Date;
        var daysPassed = (current - AnchorIssueDate).TotalDays;
        var weeksPassed = (int)Math.Floor(daysPassed / 7.0);
        var estimated = AnchorIssue + weeksPassed;
        return Math.Max(MinimumIssue, estimated);
    }

    public static async Task<int> ProbeLatestIssueAsync(int estimatedIssue, IProgress<string> status, CancellationToken ct)
    {
        // Start probing slightly before the estimate in case of calculation errors or holidays
        var probe = Math.Max(MinimumIssue, estimatedIssue - 3);
        var lastSuccess = 0;
        var hadSuccess = false;
        var failures = 0;

        while (failures < 3) // Stop after 3 consecutive 404s
        {
            ct.ThrowIfCancellationRequested();
            var url = new Uri(BaseUri, $"twic{probe}g.zip");

            status.Report($"Probing TWIC #{probe}...");

            try
            {
                using var response = await SendWithRedirectsAsync(HttpMethod.Head, url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.IsSuccessStatusCode)
                {
                    lastSuccess = probe;
                    hadSuccess = true;
                    probe++;
                    failures = 0; // Reset failures on success
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    failures++;
                    probe++;
                }
                else
                {
                    failures++;
                    probe++;
                }
            }
            catch (HttpRequestException)
            {
                // Network error counts as a failure to find
                failures++;
                probe++;
            }

            // Minimal delay during probing, but still some
            await Task.Delay(400, ct);
        }

        if (!hadSuccess)
        {
            var fallback = Math.Max(MinimumIssue, estimatedIssue);
            status.Report($"Could not confirm latest issue; using estimate {fallback}.");
            return fallback;
        }

        status.Report($"Latest confirmed issue: {lastSuccess}");
        return lastSuccess;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        // Polite User Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static async Task<string?> DownloadIssueZipWithRetryAsync(int issue, string tempFolder, CancellationToken ct)
    {
        var fileName = $"twic{issue}g.zip";
        var url = new Uri(BaseUri, fileName);
        var outputPath = Path.Combine(tempFolder, fileName);

        int attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                using var response = await SendWithRedirectsAsync(HttpMethod.Get, url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;

                response.EnsureSuccessStatusCode();

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await contentStream.CopyToAsync(fileStream, ct);

                return outputPath;
            }
            catch (Exception) when (attempts < 3)
            {
                // Exponential backoff: 1s, 2s, 4s
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

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new HttpRequestException($"Too many redirects when requesting {url}.");
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.Moved ||
        statusCode == HttpStatusCode.Redirect ||
        statusCode == HttpStatusCode.RedirectMethod ||
        statusCode == HttpStatusCode.TemporaryRedirect ||
        statusCode == HttpStatusCode.PermanentRedirect;

    private static async Task<bool> AppendIssueFromZipAsync(StreamWriter writer, string zipPath, int issue, bool isFirstGlobalEntry, CancellationToken ct)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            // TWIC sometimes nests files or names them differently.
            // Priority: "twicX.pgn", then any ".pgn".
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals($"twic{issue}.pgn", StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase));

            if (entry == null) return false;

            await using var entryStream = entry.Open();
            // Detect encoding (often Windows-1252 or UTF8)
            using var reader = new StreamReader(entryStream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: true);

            string? line;
            var hasContent = false;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!hasContent)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue; // Skip leading empty lines
                    }

                    if (!isFirstGlobalEntry)
                    {
                        await writer.WriteLineAsync();
                        await writer.WriteLineAsync(); // Double newline between files
                    }

                    hasContent = true;
                }

                await writer.WriteLineAsync(line);
            }

            return hasContent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
