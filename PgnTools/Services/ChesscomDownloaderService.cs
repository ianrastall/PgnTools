using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace PgnTools.Services;

public sealed record ChesscomUserGameFilters(
    bool OnlyUserWins = false,
    bool OnlyCheckmates = false,
    bool ExcludeBullet = false,
    bool ExcludeNonStandard = false)
{
    public static ChesscomUserGameFilters None { get; } = new();

    public bool IsActive =>
        OnlyUserWins ||
        OnlyCheckmates ||
        ExcludeBullet ||
        ExcludeNonStandard;
}

public interface IChesscomDownloaderService
{
    Task<List<string>> GetArchivesAsync(string username, CancellationToken ct = default);
    Task<string> DownloadPlayerGamesPgnAsync(string username, int year, int month, CancellationToken ct = default);
    Task<string> DownloadPlayerGamesPgnAsync(
        string username,
        int year,
        int month,
        ChesscomUserGameFilters filters,
        CancellationToken ct = default);
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

    public Task<string> DownloadPlayerGamesPgnAsync(
        string username,
        int year,
        int month,
        CancellationToken ct = default) =>
        DownloadPlayerGamesPgnAsync(username, year, month, ChesscomUserGameFilters.None, ct);

    public async Task<string> DownloadPlayerGamesPgnAsync(
        string username,
        int year,
        int month,
        ChesscomUserGameFilters filters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        ArgumentNullException.ThrowIfNull(filters);

        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12.");
        }

        return filters.IsActive
            ? await DownloadFilteredPlayerGamesPgnAsync(username, year, month, filters, ct).ConfigureAwait(false)
            : await DownloadUnfilteredPlayerGamesPgnAsync(username, year, month, ct).ConfigureAwait(false);
    }

    private static async Task<string> DownloadUnfilteredPlayerGamesPgnAsync(
        string username,
        int year,
        int month,
        CancellationToken ct)
    {
        var safeUser = Uri.EscapeDataString(username.Trim());
        var url = $"https://api.chess.com/pub/player/{safeUser}/games/{year}/{month:D2}/pgn";
        await ApplyRateLimitAsync(ct).ConfigureAwait(false);

        using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> DownloadFilteredPlayerGamesPgnAsync(
        string username,
        int year,
        int month,
        ChesscomUserGameFilters filters,
        CancellationToken ct)
    {
        var safeUser = Uri.EscapeDataString(username.Trim());
        var url = $"https://api.chess.com/pub/player/{safeUser}/games/{year}/{month:D2}";
        await ApplyRateLimitAsync(ct).ConfigureAwait(false);

        using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<MonthlyGamesResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        if (payload?.Games.Count is not > 0)
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        foreach (var game in payload.Games)
        {
            ct.ThrowIfCancellationRequested();
            if (!ShouldKeepGame(username, game, filters))
            {
                continue;
            }

            if (output.Length > 0)
            {
                output.AppendLine();
                output.AppendLine();
            }

            output.Append(game.Pgn!.TrimEnd());
        }

        return output.ToString();
    }

    private static bool ShouldKeepGame(
        string username,
        MonthlyGame game,
        ChesscomUserGameFilters filters)
    {
        if (string.IsNullOrWhiteSpace(game.Pgn))
        {
            return false;
        }

        if (filters.OnlyUserWins && !DidUserWin(username, game))
        {
            return false;
        }

        if (filters.OnlyCheckmates && !IsCheckmate(game))
        {
            return false;
        }

        if (filters.ExcludeBullet && IsBullet(game))
        {
            return false;
        }

        if (filters.ExcludeNonStandard && !IsStandard(game))
        {
            return false;
        }

        return true;
    }

    private static bool DidUserWin(string username, MonthlyGame game)
    {
        if (string.Equals(username.Trim(), game.White?.Username?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return IsWin(game.White);
        }

        if (string.Equals(username.Trim(), game.Black?.Username?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return IsWin(game.Black);
        }

        return false;
    }

    private static bool IsWin(MonthlyPlayer? player) =>
        string.Equals(player?.Result?.Trim(), "win", StringComparison.OrdinalIgnoreCase);

    private static bool IsCheckmate(MonthlyGame game) =>
        string.Equals(game.White?.Result?.Trim(), "checkmated", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(game.Black?.Result?.Trim(), "checkmated", StringComparison.OrdinalIgnoreCase);

    private static bool IsBullet(MonthlyGame game)
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

    private static bool IsStandard(MonthlyGame game) =>
        string.Equals(game.Rules?.Trim(), "chess", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseLeadingInt(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        var length = 0;
        while (length < text.Length && char.IsDigit(text[length]))
        {
            length++;
        }

        return length > 0 && int.TryParse(text[..length], out value);
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

    private sealed class MonthlyGamesResponse
    {
        [JsonPropertyName("games")]
        public List<MonthlyGame> Games { get; set; } = new();
    }

    private sealed class MonthlyGame
    {
        [JsonPropertyName("pgn")]
        public string? Pgn { get; set; }

        [JsonPropertyName("time_control")]
        public string? TimeControl { get; set; }

        [JsonPropertyName("time_class")]
        public string? TimeClass { get; set; }

        [JsonPropertyName("rules")]
        public string? Rules { get; set; }

        [JsonPropertyName("white")]
        public MonthlyPlayer? White { get; set; }

        [JsonPropertyName("black")]
        public MonthlyPlayer? Black { get; set; }
    }

    private sealed class MonthlyPlayer
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("result")]
        public string? Result { get; set; }
    }
}
