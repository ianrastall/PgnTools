using PgnTools.Models;

namespace PgnTools.Services;

/// <summary>
/// Interface for PGN Info service.
/// </summary>
public interface IPgnInfoService
{
    Task<PgnStatistics> AnalyzeFileAsync(string filePath, IProgress<(long games, string message)>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for analyzing PGN files and gathering statistics.
/// Ported from Rust pgn_info.rs
/// </summary>
public partial class PgnInfoService : IPgnInfoService
{
    private static readonly string[] CountryKeys = ["whitecountry", "blackcountry", "whitefederation", "blackfederation", "whitefed", "blackfed"];
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);

    private readonly PgnReader _pgnReader;

    public PgnInfoService(PgnReader pgnReader)
    {
        _pgnReader = pgnReader;
    }

    public async Task<PgnStatistics> AnalyzeFileAsync(
        string filePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stats = new InternalStats();
        var lastProgressReport = DateTime.MinValue;

        await foreach (var game in _pgnReader.ReadGamesAsync(filePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plyCount = CountPlies(game.MoveText);
            ApplyGame(game.Headers, plyCount, stats);

            if (ShouldReportProgress(stats.Games, ref lastProgressReport))
            {
                progress?.Report((stats.Games, $"Processing game {stats.Games}..."));
            }
        }

        return stats.ToStatistics();
    }

    private static int CountPlies(string moveText)
    {
        if (string.IsNullOrEmpty(moveText))
        {
            return 0;
        }

        // Simple ply counting: count SAN moves (words that look like moves)
        var count = 0;
        var inComment = false;
        var inLineComment = false;
        var inBracketAnnotation = false;
        var depth = 0;
        var wordStart = -1;
        var span = moveText.AsSpan();

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];

            if (inLineComment)
            {
                if (c == '\n' || c == '\r')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBracketAnnotation)
            {
                if (c == ']')
                {
                    inBracketAnnotation = false;
                }
                continue;
            }

            switch (c)
            {
                case '{':
                    if (wordStart >= 0)
                    {
                        var word = span.Slice(wordStart, i - wordStart);
                        if (IsSanMove(word))
                        {
                            count++;
                        }
                        wordStart = -1;
                    }
                    inComment = true;
                    continue;
                case '}':
                    inComment = false;
                    continue;
                case '[':
                    if (!inComment && depth == 0)
                    {
                        if (wordStart >= 0)
                        {
                            var word = span.Slice(wordStart, i - wordStart);
                            if (IsSanMove(word))
                            {
                                count++;
                            }
                            wordStart = -1;
                        }
                        inBracketAnnotation = true;
                        continue;
                    }
                    break;
                case '(':
                    if (wordStart >= 0)
                    {
                        var word = span.Slice(wordStart, i - wordStart);
                        if (IsSanMove(word))
                        {
                            count++;
                        }
                        wordStart = -1;
                    }
                    depth++;
                    continue;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    continue;
                case ';':
                    if (!inComment && depth == 0)
                    {
                        if (wordStart >= 0)
                        {
                            var word = span.Slice(wordStart, i - wordStart);
                            if (IsSanMove(word))
                            {
                                count++;
                            }
                            wordStart = -1;
                        }
                        inLineComment = true;
                        continue;
                    }
                    break;
            }

            if (inComment || depth > 0)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c) || c == '-' || c == '+' || c == '#' || c == '=')
            {
                if (wordStart < 0) wordStart = i;
            }
            else if (wordStart >= 0)
            {
                var word = span.Slice(wordStart, i - wordStart);
                if (IsSanMove(word))
                {
                    count++;
                }
                wordStart = -1;
            }
        }

        // Check last word
        if (wordStart >= 0)
        {
            var word = span.Slice(wordStart);
            if (IsSanMove(word))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsSanMove(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty || word.Length < 2)
            return false;

        // Skip move numbers
        var allDigitsOrDots = true;
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (!char.IsDigit(c) && c != '.')
            {
                allDigitsOrDots = false;
                break;
            }
        }
        if (allDigitsOrDots)
            return false;

        // Skip results
        if (word.Length == 1 && word[0] == '*')
            return false;
        if (word.SequenceEqual("1-0".AsSpan()) ||
            word.SequenceEqual("0-1".AsSpan()) ||
            word.SequenceEqual("1/2-1/2".AsSpan()))
            return false;

        // Skip NAGs
        if (word[0] == '$')
            return false;

        // Skip annotations
        if (word.Length == 1 && (word[0] == '!' || word[0] == '?'))
            return false;
        if (word.Length == 2)
        {
            if ((word[0] == '!' && word[1] == '!') ||
                (word[0] == '?' && word[1] == '?') ||
                (word[0] == '!' && word[1] == '?') ||
                (word[0] == '?' && word[1] == '!'))
            {
                return false;
            }
        }

        // Basic SAN check: starts with piece letter or file letter
        var first = word[0];
        return char.IsUpper(first) ||
               (first >= 'a' && first <= 'h') ||
               word.StartsWith("O-O".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
               word.StartsWith("0-0".AsSpan(), StringComparison.Ordinal);
    }

    private static void ApplyGame(IReadOnlyDictionary<string, string> headers, int plyCount, InternalStats stats)
    {
        headers = EnsureCaseInsensitive(headers);
        stats.Games++;

        // Players
        if (headers.TryGetHeaderValue("White", out var white) && NormalizePlayer(white) is { } normalizedWhite)
        {
            stats.Players.Add(normalizedWhite);
        }
        if (headers.TryGetHeaderValue("Black", out var black) && NormalizePlayer(black) is { } normalizedBlack)
        {
            stats.Players.Add(normalizedBlack);
        }

        // Tournament
        var tourKey = GetTournamentKey(headers);
        if (!stats.Tournaments.TryGetValue(tourKey, out var tourStats))
        {
            tourStats = new TournamentStats();
            stats.Tournaments[tourKey] = tourStats;
        }
        tourStats.Games++;

        // Result
        var result = headers.GetHeaderValueOrDefault("Result", "")?.Replace(" ", "") ?? "";
        var incomplete = false;
        switch (result)
        {
            case "1-0":
                stats.WhiteWins++;
                break;
            case "0-1":
                stats.BlackWins++;
                break;
            case "1/2-1/2":
                stats.Draws++;
                break;
            case "*":
                stats.StarResults++;
                incomplete = true;
                break;
            case "":
                stats.MissingResults++;
                incomplete = true;
                break;
            default:
                stats.OtherResults++;
                break;
        }

        if (incomplete)
        {
            stats.IncompleteGames++;
            tourStats.IncompleteGames++;
        }

        // Elo
        var whiteElo = ParseElo(headers, "WhiteElo");
        var blackElo = ParseElo(headers, "BlackElo");
        var anyElo = false;

        if (whiteElo.HasValue)
        {
            stats.AddElo(whiteElo.Value);
            anyElo = true;
        }
        if (blackElo.HasValue)
        {
            stats.AddElo(blackElo.Value);
            anyElo = true;
        }

        if (anyElo) stats.GamesWithAnyElo++;
        if (whiteElo.HasValue && blackElo.HasValue) stats.GamesWithBothElo++;

        // Countries
        foreach (var key in CountryKeys)
        {
            if (headers.TryGetHeaderValue(key, out var country) && NormalizeCountry(country) is { } normalizedCountry)
            {
                stats.Countries.Add(normalizedCountry);
            }
        }

        // ECO
        if (headers.TryGetHeaderValue("ECO", out var eco))
        {
            stats.AddEco(eco);
        }
        else
        {
            stats.EcoMissing++;
        }

        // Date
        var date = ParseDate(headers);
        if (date.HasValue)
        {
            stats.UpdateDateRange(date.Value);
        }
        else
        {
            stats.DateMissing++;
        }

        // Plies
        if (plyCount == 0)
        {
            stats.GamesWithoutMoves++;
        }
        else
        {
            stats.GamesWithMoves++;
            stats.TotalPlies += plyCount;
            stats.MinPlies = stats.MinPlies.HasValue ? Math.Min(stats.MinPlies.Value, plyCount) : plyCount;
            stats.MaxPlies = stats.MaxPlies.HasValue ? Math.Max(stats.MaxPlies.Value, plyCount) : plyCount;
        }
    }

    private static string? NormalizePlayer(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var lower = trimmed.ToLowerInvariant();
        return lower is "?" or "unknown" ? null : trimmed;
    }

    private static string? NormalizeCountry(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var lower = trimmed.ToLowerInvariant();
        return lower is "?" or "unknown" ? null : trimmed.ToUpperInvariant();
    }

    private static int? ParseElo(IReadOnlyDictionary<string, string> headers, string key)
    {
        if (!headers.TryGetHeaderValue(key, out var raw)) return null;
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (!int.TryParse(trimmed, out var value) || value <= 0) return null;
        return value;
    }

    private static string GetTournamentKey(IReadOnlyDictionary<string, string> headers)
    {
        var eventName = headers.GetHeaderValueOrDefault("Event", "?");
        var site = headers.GetHeaderValueOrDefault("Site", "?");
        var date = headers.GetHeaderValueOrDefault("Date") ?? headers.GetHeaderValueOrDefault("EventDate") ?? "????";
        var year = date.Length >= 4 && date[..4].All(char.IsDigit) ? date[..4] : "????";
        return $"{eventName}|{site}|{year}";
    }

    private static DateTime? ParseDate(IReadOnlyDictionary<string, string> headers)
    {
        var raw = headers.GetHeaderValueOrDefault("Date") ?? headers.GetHeaderValueOrDefault("EventDate");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.IndexOf('?', StringComparison.Ordinal) >= 0) return null;

        var normalized = raw.Replace('/', '.').Replace('-', '.');
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;

        if (!int.TryParse(parts[0], out var year) ||
            !int.TryParse(parts[1], out var month) ||
            !int.TryParse(parts[2], out var day))
        {
            return null;
        }

        if (year < 1 || month < 1 || month > 12 || day < 1 || day > 31)
        {
            return null;
        }

        if (day > DateTime.DaysInMonth(year, month)) return null;

        return new DateTime(year, month, day);
    }

    private class TournamentStats
    {
        public long Games { get; set; }
        public long IncompleteGames { get; set; }
    }

    private class InternalStats
    {
        public long Games { get; set; }
        public HashSet<string> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TournamentStats> Tournaments { get; } = new();
        public long IncompleteGames { get; set; }
        public long WhiteWins { get; set; }
        public long BlackWins { get; set; }
        public long Draws { get; set; }
        public long StarResults { get; set; }
        public long MissingResults { get; set; }
        public long OtherResults { get; set; }
        public long EloSum { get; set; }
        public long EloCount { get; set; }
        public int? EloMin { get; set; }
        public int? EloMax { get; set; }
        public long GamesWithAnyElo { get; set; }
        public long GamesWithBothElo { get; set; }
        public HashSet<string> Countries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<char, long> EcoLetterCounts { get; } = new();
        public Dictionary<string, long> EcoCounts { get; } = new();
        public long EcoMissing { get; set; }
        public long EcoInvalid { get; set; }
        public DateTime? MinDate { get; set; }
        public DateTime? MaxDate { get; set; }
        public long DateMissing { get; set; }
        public long TotalPlies { get; set; }
        public int? MinPlies { get; set; }
        public int? MaxPlies { get; set; }
        public long GamesWithMoves { get; set; }
        public long GamesWithoutMoves { get; set; }

        public void AddElo(int elo)
        {
            EloSum += elo;
            EloCount++;
            EloMin = EloMin.HasValue ? Math.Min(EloMin.Value, elo) : elo;
            EloMax = EloMax.HasValue ? Math.Max(EloMax.Value, elo) : elo;
        }

        public void AddEco(string ecoRaw)
        {
            var eco = ecoRaw.Trim();
            if (string.IsNullOrEmpty(eco))
            {
                EcoMissing++;
                return;
            }

            var upper = eco.ToUpperInvariant();
            if (upper.Length < 1)
            {
                EcoInvalid++;
                return;
            }

            var letter = upper[0];
            if (letter < 'A' || letter > 'E')
            {
                EcoInvalid++;
                return;
            }

            EcoLetterCounts.TryGetValue(letter, out var count);
            EcoLetterCounts[letter] = count + 1;

            if (upper.Length >= 3 && char.IsDigit(upper[1]) && char.IsDigit(upper[2]))
            {
                var code = upper[..3];
                EcoCounts.TryGetValue(code, out var codeCount);
                EcoCounts[code] = codeCount + 1;
            }
            else
            {
                EcoInvalid++;
            }
        }

        public void UpdateDateRange(DateTime date)
        {
            if (!MinDate.HasValue || date < MinDate.Value)
                MinDate = date;
            if (!MaxDate.HasValue || date > MaxDate.Value)
                MaxDate = date;
        }

        public PgnStatistics ToStatistics()
        {
            var topEco = EcoCounts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .Take(10)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return new PgnStatistics
            {
                Games = Games,
                PlayerCount = Players.Count,
                TournamentCount = Tournaments.Count,
                TournamentsWithIncompleteGames = Tournaments.Values.Count(t => t.IncompleteGames > 0),
                IncompleteGames = IncompleteGames,
                WhiteWins = WhiteWins,
                BlackWins = BlackWins,
                Draws = Draws,
                StarResults = StarResults,
                MissingResults = MissingResults,
                OtherResults = OtherResults,
                AverageElo = EloCount > 0 ? (double)EloSum / EloCount : null,
                HighestElo = EloMax,
                LowestElo = EloMin,
                GamesWithAnyElo = GamesWithAnyElo,
                GamesWithBothElo = GamesWithBothElo,
                CountryCount = Countries.Count,
                MinDate = MinDate,
                MaxDate = MaxDate,
                DateMissing = DateMissing,
                EcoLetterCounts = new Dictionary<char, long>(EcoLetterCounts),
                TopEcoCodes = topEco,
                EcoMissing = EcoMissing,
                EcoInvalid = EcoInvalid,
                AveragePlies = GamesWithMoves > 0 ? (double)TotalPlies / GamesWithMoves : null,
                MinPlies = MinPlies,
                MaxPlies = MaxPlies,
                GamesWithMoves = GamesWithMoves,
                GamesWithoutMoves = GamesWithoutMoves
            };
        }
    }

    private static bool ShouldReportProgress(long games, ref DateTime lastReportUtc)
    {
        if (games <= 0)
        {
            return false;
        }

        if (games != 1 && games % ProgressGameInterval != 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - lastReportUtc < ProgressTimeInterval)
        {
            return false;
        }

        lastReportUtc = now;
        return true;
    }

    private static IReadOnlyDictionary<string, string> EnsureCaseInsensitive(IReadOnlyDictionary<string, string> headers)
    {
        if (headers is Dictionary<string, string> dict && dict.Comparer == StringComparer.OrdinalIgnoreCase)
        {
            return dict;
        }

        return new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }
}
