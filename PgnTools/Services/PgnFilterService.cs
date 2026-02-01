using System.Text;

namespace PgnTools.Services;

public sealed record PgnFilterOptions(
    int? MinElo,
    int? MaxElo,
    bool RequireBothElos,
    bool OnlyCheckmates,
    bool RemoveComments,
    bool RemoveNags,
    bool RemoveVariations,
    bool RemoveNonStandard,
    int? MinPlyCount,
    int? MaxPlyCount);

public sealed record PgnFilterResult(long Processed, long Kept, long Modified);

public interface IPgnFilterService
{
    Task<PgnFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
        PgnFilterOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies filtering and cleanup rules to PGN files.
/// </summary>
public sealed class PgnFilterService : IPgnFilterService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly char[] AnnotationTrimChars = ['+', '#', '!', '?'];

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public PgnFilterService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task<PgnFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
        PgnFilterOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new ArgumentException("Input file path is required.", nameof(inputFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFilePath));
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputFilePath);
        var tempOutputPath = outputFullPath + ".tmp";

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Input and output files must be different. To modify in-place, use a temporary output file first.");
        }

        if (options.MinElo.HasValue && options.MaxElo.HasValue && options.MaxElo.Value < options.MinElo.Value)
        {
            throw new ArgumentException("Max Elo must be greater than or equal to Min Elo.", nameof(options));
        }

        if (options.MinPlyCount.HasValue && options.MaxPlyCount.HasValue && options.MaxPlyCount.Value < options.MinPlyCount.Value)
        {
            throw new ArgumentException("Max ply count must be greater than or equal to Min ply count.", nameof(options));
        }

        var processed = 0L;
        var kept = 0L;
        var modified = 0L;
        var lastProgressReport = DateTime.MinValue;

        try
        {
            await using var inputStream = new FileStream(
                inputFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            progress?.Report(0);

            var firstOutput = true;

            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
            {
                await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;

                    if (options.RemoveNonStandard && !IsStandardGame(game.Headers))
                    {
                        ReportProgress(processed, inputStream, progress, ref lastProgressReport);
                        continue;
                    }

                    if (options.OnlyCheckmates && !ContainsCheckmate(game.MoveText))
                    {
                        ReportProgress(processed, inputStream, progress, ref lastProgressReport);
                        continue;
                    }

                    if (!PassesEloFilter(game.Headers, options.MinElo, options.MaxElo, options.RequireBothElos))
                    {
                        ReportProgress(processed, inputStream, progress, ref lastProgressReport);
                        continue;
                    }

                    if (!PassesPlyFilter(game.MoveText, options.MinPlyCount, options.MaxPlyCount))
                    {
                        ReportProgress(processed, inputStream, progress, ref lastProgressReport);
                        continue;
                    }

                    var outputGame = game;
                    if (options.RemoveComments || options.RemoveNags || options.RemoveVariations)
                    {
                        var cleanedMoveText = StripAnnotations(
                            game.MoveText,
                            options.RemoveComments,
                            options.RemoveNags,
                            options.RemoveVariations);

                        if (!string.Equals(cleanedMoveText, game.MoveText, StringComparison.Ordinal))
                        {
                            modified++;
                        }

                        outputGame = new PgnGame(game.Headers, cleanedMoveText);
                    }

                    if (!firstOutput)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await _pgnWriter.WriteGameAsync(writer, outputGame, cancellationToken).ConfigureAwait(false);
                    firstOutput = false;
                    kept++;

                    ReportProgress(processed, inputStream, progress, ref lastProgressReport);
                }

                await writer.FlushAsync().ConfigureAwait(false);
            }

            if (processed == 0)
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }

                progress?.Report(100);
                return new PgnFilterResult(0, 0, 0);
            }

            File.Move(tempOutputPath, outputFullPath, overwrite: true);
            progress?.Report(100);

            return new PgnFilterResult(processed, kept, modified);
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
    }

    private static void ReportProgress(
        long processed,
        Stream inputStream,
        IProgress<double>? progress,
        ref DateTime lastProgressReport)
    {
        if (!ShouldReportProgress(processed, ref lastProgressReport))
        {
            return;
        }

        progress?.Report(GetProgressPercent(inputStream));
    }

    private static bool PassesEloFilter(
        IReadOnlyDictionary<string, string> headers,
        int? minElo,
        int? maxElo,
        bool requireBoth)
    {
        if (!minElo.HasValue && !maxElo.HasValue)
        {
            return true;
        }

        var white = ParseElo(headers, "WhiteElo");
        var black = ParseElo(headers, "BlackElo");

        if (requireBoth)
        {
            if (!white.HasValue || !black.HasValue)
            {
                return false;
            }

            if (minElo.HasValue && (white.Value < minElo.Value || black.Value < minElo.Value))
            {
                return false;
            }

            if (maxElo.HasValue && (white.Value > maxElo.Value || black.Value > maxElo.Value))
            {
                return false;
            }

            return true;
        }

        if (!white.HasValue && !black.HasValue)
        {
            return false;
        }

        var maxAvailable = int.MinValue;
        if (white.HasValue)
        {
            maxAvailable = Math.Max(maxAvailable, white.Value);
        }

        if (black.HasValue)
        {
            maxAvailable = Math.Max(maxAvailable, black.Value);
        }

        if (minElo.HasValue && maxAvailable < minElo.Value)
        {
            return false;
        }

        if (maxElo.HasValue)
        {
            if (white.HasValue && white.Value > maxElo.Value)
            {
                return false;
            }

            if (black.HasValue && black.Value > maxElo.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static int? ParseElo(IReadOnlyDictionary<string, string> headers, string key)
    {
        if (headers.TryGetHeaderValue(key, out var value) &&
            int.TryParse(value, out var rating) &&
            rating > 0)
        {
            return rating;
        }

        return null;
    }

    private static bool PassesPlyFilter(string moveText, int? minPly, int? maxPly)
    {
        if (!minPly.HasValue && !maxPly.HasValue)
        {
            return true;
        }

        var count = CountPlies(moveText);
        if (minPly.HasValue && count < minPly.Value)
        {
            return false;
        }

        if (maxPly.HasValue && count > maxPly.Value)
        {
            return false;
        }

        return true;
    }

    private static int CountPlies(string moveText)
    {
        var count = 0;
        foreach (var _ in TokenizeMoves(moveText))
        {
            count++;
        }

        return count;
    }

    private static bool IsStandardGame(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetHeaderValue("Variant", out var variant))
        {
            var trimmed = variant.Trim();
            if (trimmed.Length == 0 ||
                trimmed.Equals("?", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var lower = trimmed.ToLowerInvariant();
            if (lower is "standard" or "normal" or "classical")
            {
                return true;
            }

            if (lower.Contains("standard") || lower.Contains("classical") || lower.Contains("normal"))
            {
                return true;
            }

            return false;
        }

        if (headers.TryGetHeaderValue("SetUp", out var setup) &&
            setup.Trim().Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (headers.TryGetHeaderValue("FEN", out var fen) && !string.IsNullOrWhiteSpace(fen))
        {
            return false;
        }

        return true;
    }

    private static string StripAnnotations(string moveText, bool removeComments, bool removeNags, bool removeVariations)
    {
        if (!removeComments && !removeNags && !removeVariations)
        {
            return moveText ?? string.Empty;
        }

        if (string.IsNullOrEmpty(moveText))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(moveText.Length);
        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;
        var lastWasSpace = false;

        for (var i = 0; i < moveText.Length; i++)
        {
            var c = moveText[i];

            if (removeComments && inLineComment)
            {
                if (c == '\n' || c == '\r')
                {
                    inLineComment = false;
                    Emit(builder, c, ref lastWasSpace);
                }
                continue;
            }

            if (removeComments && inBraceComment)
            {
                if (c == '}')
                {
                    inBraceComment = false;
                }
                continue;
            }

            if (removeComments && c == '{')
            {
                inBraceComment = true;
                continue;
            }

            if (removeComments && c == ';')
            {
                inLineComment = true;
                continue;
            }

            if (removeVariations && c == '(')
            {
                variationDepth++;
                continue;
            }

            if (removeVariations && c == ')')
            {
                if (variationDepth > 0)
                {
                    variationDepth--;
                }
                continue;
            }

            if (removeVariations && variationDepth > 0)
            {
                continue;
            }

            if (removeNags && c == '$')
            {
                while (i + 1 < moveText.Length && char.IsDigit(moveText[i + 1]))
                {
                    i++;
                }
                continue;
            }

            Emit(builder, c, ref lastWasSpace);
        }

        return builder.ToString().Trim();
    }

    private static void Emit(StringBuilder builder, char c, ref bool lastWasSpace)
    {
        if (char.IsWhiteSpace(c) && c != '\n' && c != '\r')
        {
            if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
            return;
        }

        builder.Append(c);
        lastWasSpace = false;
    }

    private static bool ContainsCheckmate(string moveText)
    {
        if (string.IsNullOrEmpty(moveText))
        {
            return false;
        }

        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;

        for (var i = 0; i < moveText.Length; i++)
        {
            var c = moveText[i];

            if (inLineComment)
            {
                if (c == '\n' || c == '\r')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBraceComment)
            {
                if (c == '}')
                {
                    inBraceComment = false;
                }
                continue;
            }

            switch (c)
            {
                case '{':
                    inBraceComment = true;
                    continue;
                case ';':
                    if (variationDepth == 0)
                    {
                        inLineComment = true;
                        continue;
                    }
                    break;
                case '(':
                    variationDepth++;
                    continue;
                case ')':
                    if (variationDepth > 0)
                    {
                        variationDepth--;
                    }
                    continue;
            }

            if (variationDepth > 0)
            {
                continue;
            }

            if (c == '#')
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> TokenizeMoves(string moveText)
    {
        if (string.IsNullOrWhiteSpace(moveText))
        {
            yield break;
        }

        var token = new StringBuilder();
        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;

        for (var i = 0; i < moveText.Length; i++)
        {
            var c = moveText[i];

            if (inLineComment)
            {
                if (c is '\n' or '\r')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBraceComment)
            {
                if (c == '}')
                {
                    inBraceComment = false;
                }

                continue;
            }

            if (variationDepth > 0)
            {
                if (c == '(')
                {
                    variationDepth++;
                }
                else if (c == ')')
                {
                    variationDepth--;
                }

                continue;
            }

            if (c == '{')
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                inBraceComment = true;
                continue;
            }

            if (c == ';')
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                inLineComment = true;
                continue;
            }

            if (c == '(')
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                variationDepth = 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    var raw = token.ToString();
                    token.Clear();
                    if (TryNormalizeMoveToken(raw, out var move, out var isResult))
                    {
                        yield return move!;
                    }
                    else if (isResult)
                    {
                        yield break;
                    }
                }

                continue;
            }

            token.Append(c);
        }

        if (token.Length > 0)
        {
            var raw = token.ToString();
            if (TryNormalizeMoveToken(raw, out var move, out var isResult))
            {
                yield return move!;
            }
        }
    }

    private static bool TryNormalizeMoveToken(string raw, out string? move, out bool isResult)
    {
        move = null;
        isResult = false;

        var token = raw.Trim();
        if (token.Length == 0)
        {
            return false;
        }

        if (token[0] == '$' && token.Skip(1).All(char.IsDigit))
        {
            return false;
        }

        if (token.All(c => c == '.'))
        {
            return false;
        }

        if (TryStripMoveNumberPrefix(token, out var stripped))
        {
            token = stripped;
        }

        token = token.TrimStart('.');
        if (token.Length == 0)
        {
            return false;
        }

        if (token.All(c => c is '!' or '?' or '+' or '#'))
        {
            return false;
        }

        if (IsResultToken(token))
        {
            isResult = true;
            return false;
        }

        token = token.TrimEnd(AnnotationTrimChars);
        if (token.Length == 0)
        {
            return false;
        }

        token = StripTrailingNag(token);
        if (token.Length == 0)
        {
            return false;
        }

        if (IsResultToken(token))
        {
            isResult = true;
            return false;
        }

        if (token.Equals("e.p.", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("ep", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = NormalizeCastling(token);
        move = token;
        return true;
    }

    private static string StripTrailingNag(string token)
    {
        var index = token.LastIndexOf('$');
        if (index < 0 || index == token.Length - 1)
        {
            return token;
        }

        var digits = token[(index + 1)..];
        return digits.All(char.IsDigit) ? token[..index] : token;
    }

    private static bool TryStripMoveNumberPrefix(string token, out string stripped)
    {
        stripped = token;
        var firstDot = token.IndexOf('.');
        if (firstDot < 0)
        {
            return false;
        }

        var prefix = token[..firstDot];
        if (prefix.Length == 0 || !prefix.All(char.IsDigit))
        {
            return false;
        }

        var lastDot = token.LastIndexOf('.');
        if (lastDot >= token.Length - 1)
        {
            stripped = string.Empty;
            return true;
        }

        stripped = token[(lastDot + 1)..];
        return true;
    }

    private static bool IsResultToken(string token)
    {
        return token is "1-0" or "0-1" or "1/2-1/2" or "*";
    }

    private static string NormalizeCastling(string token)
    {
        if (token.Equals("0-0", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("o-o", StringComparison.OrdinalIgnoreCase))
        {
            return "O-O";
        }

        if (token.Equals("0-0-0", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("o-o-o", StringComparison.OrdinalIgnoreCase))
        {
            return "O-O-O";
        }

        return token;
    }

    private static double GetProgressPercent(Stream stream)
    {
        if (!stream.CanSeek || stream.Length == 0)
        {
            return 0;
        }

        var percent = stream.Position / (double)stream.Length * 100;
        if (percent < 0)
        {
            return 0;
        }

        return percent > 100 ? 100 : percent;
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
}
