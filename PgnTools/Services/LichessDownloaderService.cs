namespace PgnTools.Services;

public interface ILichessDownloaderService
{
    Task DownloadUserGamesAsync(string username, string outputFile, int? max, CancellationToken ct = default);
}

public class LichessDownloaderService : ILichessDownloaderService
{
    private const int BufferSize = 65536;
    private static readonly HttpClient HttpClient = CreateClient();

    public async Task DownloadUserGamesAsync(string username, string outputFile, int? max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        if (string.IsNullOrWhiteSpace(outputFile))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFile));
        }

        if (max.HasValue && max.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), "Max games must be positive.");
        }

        var outputFullPath = Path.GetFullPath(outputFile);
        var tempOutputPath = outputFullPath + ".tmp";

        if (Path.GetDirectoryName(outputFullPath) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var url = $"https://lichess.org/api/games/user/{username}";
        if (max.HasValue)
        {
            url += $"?max={max.Value}";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/x-chess-pgn");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            await using var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            await responseStream.CopyToAsync(outputStream, ct);
        }
        catch
        {
            if (File.Exists(tempOutputPath))
            {
                try
                {
                    File.Delete(tempOutputPath);
                }
                catch
                {
                }
            }
            throw;
        }

        if (File.Exists(outputFullPath))
        {
            File.Delete(outputFullPath);
        }

        File.Move(tempOutputPath, outputFullPath);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }
}
