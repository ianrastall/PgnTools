using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using PgnTools.Helpers;

namespace PgnTools.Services;

public partial class PgnMentorDownloaderService : IPgnMentorDownloaderService
{
    private const int BufferSize = 65536;
    private static readonly Uri BaseUri = new("https://www.pgnmentor.com/");
    private static readonly Uri FilesUri = new("https://www.pgnmentor.com/files.html");
    private static readonly HttpClient HttpClient = CreateClient();
    private static readonly Random RandomJitter = Random.Shared;

    public async Task DownloadAndCombineAsync(string outputFile, IProgress<string> status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputFile))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFile));
        }

        ArgumentNullException.ThrowIfNull(status);

        var outputFullPath = Path.GetFullPath(outputFile);
        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);
        var tempFolder = Path.Combine(Path.GetTempPath(), $"PgnTools_Mentor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);

        try
        {
            // 1. Fetch the list
            status.Report("Fetching file list...");
            var html = await HttpClient.GetStringAsync(FilesUri, ct).ConfigureAwait(false);
            var links = ExtractLinks(html);

            if (links.Count == 0)
            {
                status.Report("No files found.");
                return;
            }

            status.Report($"Found {links.Count:N0} files.");

            // 2. Open Output Stream ONCE.
            // We append as we download to avoid storing 1000 files on disk or in RAM.
            // Note: This sacrifices Sorting. Sorting 5GB of PGNs requires a database or external merge sort.
            // For a downloader, "As Provided" order is acceptable and much safer.
            await using (var outputStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
            {
                int processed = 0;

                for (var i = 0; i < links.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // RATE LIMIT: 2 to 4 seconds delay
                    if (i > 0)
                    {
                        var delay = RandomJitter.Next(2000, 4001);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }

                    var link = links[i];
                    var downloadUrl = new Uri(BaseUri, link);
                    var displayName = Path.GetFileName(downloadUrl.LocalPath);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = $"file_{i:D4}";
                    }

                    var safeName = SanitizeFileName(displayName);
                    if (string.IsNullOrWhiteSpace(safeName))
                    {
                        safeName = $"file_{i:D4}";
                    }

                    var tempDownloadPath = Path.Combine(tempFolder, $"{i:D4}_{safeName}");

                    status.Report($"Downloading {displayName} ({i + 1}/{links.Count})...");

                    try
                    {
                        await DownloadWithRetryAsync(downloadUrl, tempDownloadPath, ct).ConfigureAwait(false);

                        bool hasContent = false;
                        var extension = Path.GetExtension(downloadUrl.LocalPath);

                        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            hasContent = await ExtractAndAppendZipAsync(writer, tempDownloadPath, processed > 0, ct)
                                .ConfigureAwait(false);
                        }
                        else if (extension.Equals(".pgn", StringComparison.OrdinalIgnoreCase))
                        {
                            hasContent = await AppendPgnFileAsync(writer, tempDownloadPath, processed > 0, ct)
                                .ConfigureAwait(false);
                        }

                        if (hasContent) processed++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        status.Report($"Failed {displayName}: {ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(tempDownloadPath))
                            {
                                File.Delete(tempDownloadPath);
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                await writer.FlushAsync().ConfigureAwait(false);

                if (processed == 0)
                {
                    status.Report("No valid PGN data collected.");
                    return;
                }
            }

            await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, ct).ConfigureAwait(false);
            status.Report("Download complete.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempFolder))
                {
                    Directory.Delete(tempFolder, true);
                }
            }
            catch
            {
            }

            try
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
            }
            catch
            {
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static async Task DownloadWithRetryAsync(Uri uri, string destPath, CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempts = 1; attempts <= maxAttempts; attempts++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var webStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await webStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempts < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempts < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempts < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private static async Task<bool> ExtractAndAppendZipAsync(StreamWriter writer, string zipPath, bool addSeparator, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        bool foundAny = false;
        foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase)))
        {
            await using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var appended = await AppendReaderAsync(writer, reader, addSeparator || foundAny, ct).ConfigureAwait(false);
            if (appended)
            {
                foundAny = true;
            }
        }

        return foundAny;
    }

    private static async Task<bool> AppendPgnFileAsync(StreamWriter writer, string pgnPath, bool addSeparator, CancellationToken ct)
    {
        using var reader = new StreamReader(pgnPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await AppendReaderAsync(writer, reader, addSeparator, ct).ConfigureAwait(false);
    }

    private static async Task<bool> AppendReaderAsync(StreamWriter writer, StreamReader reader, bool addSeparator, CancellationToken ct)
    {
        var bufferedLines = new List<string>();
        string? line;
        var hasNonWhitespace = false;

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            bufferedLines.Add(line);
            if (!string.IsNullOrWhiteSpace(line))
            {
                hasNonWhitespace = true;
                break;
            }
        }

        if (!hasNonWhitespace)
        {
            return false;
        }

        if (addSeparator)
        {
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        foreach (var buffered in bufferedLines)
        {
            await writer.WriteLineAsync(buffered).ConfigureAwait(false);
        }

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }

        return true;
    }

    private static List<string> ExtractLinks(string html)
    {
        var matches = LinkRegex().Matches(html);
        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var url = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (seen.Add(url))
            {
                links.Add(url);
            }
        }
        // Return as-is (site order), usually chronological
        return links;
    }

    [GeneratedRegex("href\\s*=\\s*[\"'](?<url>[^\"'#\\s]+?\\.(?:zip|pgn)(?:\\?[^\"'#\\s]*)?)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LinkRegex();

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            if (Array.IndexOf(invalidChars, c) >= 0)
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
