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
    private const int RateLimitMinMs = 800;
    private const int RateLimitMaxMs = 1400;

    public async Task<List<string>> GetArchivesAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        var safeUser = Uri.EscapeDataString(username.Trim());
        var url = $"https://api.chess.com/pub/player/{safeUser}/games/archives";
        await ApplyRateLimitAsync(ct).ConfigureAwait(false);

        using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ArchiveResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        return payload?.Archives ?? new List<string>();
    }

    public async Task<string> DownloadPlayerGamesPgnAsync(string username, int year, int month, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12.");
        }

        var safeUser = Uri.EscapeDataString(username.Trim());
        var url = $"https://api.chess.com/pub/player/{safeUser}/games/{year}/{month:D2}/pgn";
        await ApplyRateLimitAsync(ct).ConfigureAwait(false);

        using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static Task ApplyRateLimitAsync(CancellationToken ct)
    {
        var delayMs = Random.Shared.Next(RateLimitMinMs, RateLimitMaxMs);
        return Task.Delay(delayMs, ct);
    }

    private sealed class ArchiveResponse
    {
        [JsonPropertyName("archives")]
        public List<string> Archives { get; set; } = new();
    }
}
