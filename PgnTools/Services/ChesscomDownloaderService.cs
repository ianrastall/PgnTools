using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PgnTools.Services;

public interface IChesscomDownloaderService
{
    Task<List<string>> GetArchivesAsync(string username, CancellationToken ct = default);
    Task<string> DownloadPlayerGamesPgnAsync(string username, int year, int month, CancellationToken ct = default);
}

public class ChesscomDownloaderService : IChesscomDownloaderService
{
    private static readonly HttpClient HttpClient = CreateClient();
    private static readonly Random RandomJitter = new();

    public async Task<List<string>> GetArchivesAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        var url = $"https://api.chess.com/pub/player/{username}/games/archives";
        await ApplyRateLimitAsync(ct);

        var response = await HttpClient.GetFromJsonAsync<ArchiveResponse>(url, cancellationToken: ct);
        return response?.Archives ?? new List<string>();
    }

    public async Task<string> DownloadPlayerGamesPgnAsync(string username, int year, int month, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        var url = $"https://api.chess.com/pub/player/{username}/games/{year}/{month:D2}/pgn";
        await ApplyRateLimitAsync(ct);
        return await HttpClient.GetStringAsync(url, ct);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static Task ApplyRateLimitAsync(CancellationToken ct)
    {
        var delayMs = RandomJitter.Next(800, 1400);
        return Task.Delay(delayMs, ct);
    }

    private sealed class ArchiveResponse
    {
        [JsonPropertyName("archives")]
        public List<string> Archives { get; set; } = new();
    }
}
