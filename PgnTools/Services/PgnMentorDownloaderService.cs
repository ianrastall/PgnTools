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
    private static readonly Random RandomJitter = new();

    public async Task DownloadAndCombineAsync(string outputFile, IProgress<string> status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputFile)) throw new ArgumentException("Output file path is required.");

        var outputFullPath = Path.GetFullPath(outputFile);
        var tempOutputPath = outputFullPath + ".tmp";
        var tempFolder = Path.Combine(Path.GetTempPath(), $"PgnTools_Mentor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);

        try
        {
            // 1. Fetch the list
            status.Report("Fetching file list...");
            var html = await HttpClient.GetStringAsync(FilesUri, ct);
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
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize))
            {
                int processed = 0;

                for (var i = 0; i < links.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // RATE LIMIT: 2 to 4 seconds delay
                    if (i > 0)
                    {
                        var delay = RandomJitter.Next(2000, 4000);
                        await Task.Delay(delay, ct);
                    }

                    var link = links[i];
                    var fileName = Path.GetFileName(link) ?? $"file_{i}.pgn";
                    var downloadUrl = new Uri(BaseUri, link);
                    var tempDownloadPath = Path.Combine(tempFolder, fileName);

                    status.Report($"Downloading {fileName} ({i + 1}/{links.Count})...");

                    try
                    {
                        await DownloadWithRetryAsync(downloadUrl, tempDownloadPath, ct);

                        bool hasContent = false;

                        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            hasContent = await ExtractAndAppendZipAsync(writer, tempDownloadPath, processed > 0, ct);
                        }
                        else if (fileName.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase))
                        {
                            hasContent = await AppendPgnFileAsync(writer, tempDownloadPath, processed > 0, ct);
                        }

                        if (hasContent) processed++;

                        // Cleanup immediately
                        if (File.Exists(tempDownloadPath)) File.Delete(tempDownloadPath);
                    }
                    catch (Exception ex)
                    {
                        status.Report($"Failed {fileName}: {ex.Message}");
                    }
                }

                await writer.FlushAsync();

                if (processed == 0)
                {
                    status.Report("No valid PGN data collected.");
                    return;
                }
            }

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
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

    private static async Task DownloadWithRetryAsync(Uri uri, string destPath, CancellationToken ct)
    {
        int attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var webStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await webStream.CopyToAsync(fileStream, ct);
                return;
            }
            catch (Exception) when (attempts < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempts), ct);
            }
        }
    }

    private static async Task<bool> ExtractAndAppendZipAsync(StreamWriter writer, string zipPath, bool addSeparator, CancellationToken ct)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            bool foundAny = false;
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase)))
            {
                if (addSeparator || foundAny)
                {
                    await writer.WriteLineAsync();
                    await writer.WriteLineAsync();
                }

                await using var s = entry.Open();
                using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    await writer.WriteLineAsync(line);
                }
                foundAny = true;
            }
            return foundAny;
        }
        catch { return false; }
    }

    private static async Task<bool> AppendPgnFileAsync(StreamWriter writer, string pgnPath, bool addSeparator, CancellationToken ct)
    {
        if (addSeparator)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync();
        }

        using var reader = new StreamReader(pgnPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            await writer.WriteLineAsync(line);
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

    [GeneratedRegex("href\\s*=\\s*[\"'](?<url>[^\"'#]+?\\.(?:zip|pgn))[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LinkRegex();
}
