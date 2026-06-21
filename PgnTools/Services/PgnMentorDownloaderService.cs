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
            if (!FileReplacementHelper.IsDirectoryWritable(outputDirectory))
            {
                throw new UnauthorizedAccessException($"Directory '{outputDirectory}' is not writable.");
            }
        }

        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);
        var tempFolder = Path.Combine(Path.GetTempPath(), $"PgnTools_Mentor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);

        try
        {
            using var httpClient = CreateClient();

            status.Report("Fetching file list...");
            string html = string.Empty;
            try
            {
                html = await FetchHtmlWithRetryAsync(httpClient, FilesUri, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                status.Report($"Failed to fetch file list: {ex.Message}");
                return;
            }

            var links = ExtractLinks(html);

            if (links.Count == 0)
            {
                status.Report("No files found.");
                return;
            }

            status.Report($"Found {links.Count:N0} files.");

            await using (var outputStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
            {
                int processed = 0;
                bool hadFailures = false;
                bool addSeparator = false;

                for (var i = 0; i < links.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (i > 0)
                    {
                        var delay = RandomJitter.Next(2000, 4001);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }

                    var link = links[i];
                    var downloadUrl = new Uri(BaseUri, link);
                    var displayName = Path.GetFileName(downloadUrl.LocalPath);
                    var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(displayName) ? $"file_{i:D4}" : displayName);
                    var tempDownloadPath = Path.Combine(tempFolder, $"{i:D4}_{safeName}");

                    status.Report($"Downloading {displayName ?? safeName} ({i + 1}/{links.Count})...");

                    try
                    {
                        await DownloadWithRetryAsync(httpClient, downloadUrl, tempDownloadPath, ct).ConfigureAwait(false);

                        bool hasContent = false;
                        var extension = Path.GetExtension(downloadUrl.LocalPath);

                        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            hasContent = await ExtractAndAppendZipAsync(writer, tempDownloadPath, addSeparator, ct).ConfigureAwait(false);
                        }
                        else if (extension.Equals(".pgn", StringComparison.OrdinalIgnoreCase))
                        {
                            hasContent = await AppendPgnFileAsync(writer, tempDownloadPath, addSeparator, ct).ConfigureAwait(false);
                        }

                        if (hasContent)
                        {
                            processed++;
                            addSeparator = true;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        hadFailures = true;
                        status.Report($"Failed {displayName ?? safeName}: {ex.Message}");
                    }
                    finally
                    {
                        if (File.Exists(tempDownloadPath)) File.Delete(tempDownloadPath);
                    }
                }

                await writer.FlushAsync(ct).ConfigureAwait(false);

                if (processed == 0)
                {
                    status.Report("No valid PGN data collected.");
                    return;
                }

                if (hadFailures)
                {
                    status.Report("Run failed due to one or more errors. Output file will not be replaced.");
                    return;
                }
            }

            await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, ct).ConfigureAwait(false);
            status.Report("Download complete.");
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
            if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static async Task DownloadWithRetryAsync(HttpClient httpClient, Uri uri, string destPath, CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempts = 1; attempts <= maxAttempts; attempts++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var webStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await webStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempts < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempts < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct).ConfigureAwait(false);
            }
        }
        throw new HttpRequestException($"Failed to download {uri} after {maxAttempts} attempts.");
    }

    private static async Task<string> FetchHtmlWithRetryAsync(HttpClient httpClient, Uri uri, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempts = 1; attempts <= maxAttempts; attempts++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await httpClient.GetStringAsync(uri, ct).ConfigureAwait(false); }
            catch (Exception) when (attempts < maxAttempts) { await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct); }
        }
        throw new HttpRequestException($"Failed to fetch {uri} after {maxAttempts} attempts.");
    }

    private static async Task<bool> ExtractAndAppendZipAsync(StreamWriter writer, string zipPath, bool addSeparator, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        bool foundAny = false;
        long totalUncompressedBytes = 0;
        int entryCount = 0;

        foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase)))
        {
            entryCount++;
            if (entryCount > 1000) throw new InvalidDataException("ZIP archive contains too many entries.");
            totalUncompressedBytes += entry.Length;
            if (totalUncompressedBytes > 10L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIP archive exceeds size limit.");

            await using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
            var appended = await AppendReaderAsync(writer, reader, addSeparator, ct).ConfigureAwait(false);
            if (appended)
            {
                foundAny = true;
                addSeparator = false;
            }
        }
        return foundAny;
    }

    private static async Task<bool> AppendPgnFileAsync(StreamWriter writer, string pgnPath, bool addSeparator, CancellationToken ct)
    {
        using var reader = new StreamReader(pgnPath, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return await AppendReaderAsync(writer, reader, addSeparator, ct).ConfigureAwait(false);
    }

    private static async Task<bool> AppendReaderAsync(StreamWriter writer, StreamReader reader, bool addSeparator, CancellationToken ct)
    {
        string? line;
        var hasNonWhitespace = false;

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                hasNonWhitespace = true;
                if (addSeparator)
                {
                    await writer.WriteLineAsync().ConfigureAwait(false);
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                break;
            }
        }

        if (!hasNonWhitespace) return false;

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
            var rawUrl = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(rawUrl)) continue;

            if (Uri.TryCreate(BaseUri, rawUrl, out var url))
            {
                if (!string.Equals(url.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase) && !string.Equals(url.Host, "pgnmentor.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(url.AbsoluteUri)) links.Add(rawUrl);
            }
        }
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
