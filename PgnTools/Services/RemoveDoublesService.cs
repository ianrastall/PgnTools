using System.Security.Cryptography;
using System.Text;

namespace PgnTools.Services;

public sealed record RemoveDoublesResult(long Processed, long Kept, long Removed);

public interface IRemoveDoublesService
{
    Task<RemoveDoublesResult> DeduplicateAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}

public class RemoveDoublesService : IRemoveDoublesService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly char[] AnnotationTrimChars = ['+', '#', '!', '?'];
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public RemoveDoublesService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task<RemoveDoublesResult> DeduplicateAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
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

        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var processed = 0L;
        var kept = 0L;
        var removed = 0L;
        var lastProgressReport = DateTime.MinValue;

        try
        {
            await using (var inputStream = new FileStream(
                inputFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true))
            {
                var firstOutput = true;

                await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;

                    var hash = ComputeGameHash(game);
                    if (seenHashes.Add(hash))
                    {
                        if (!firstOutput)
                        {
                            await writer.WriteLineAsync();
                        }

                        await _pgnWriter.WriteGameAsync(writer, game, cancellationToken);
                        firstOutput = false;
                        kept++;
                    }
                    else
                    {
                        removed++;
                    }

                    if (ShouldReportProgress(processed, ref lastProgressReport))
                    {
                        progress?.Report((processed, $"Processing Game {processed:N0}... (kept {kept:N0}, removed {removed:N0})"));
                    }
                }

                await writer.FlushAsync();
            }

            if (processed == 0)
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }

                progress?.Report((0, "No games found."));
                return new RemoveDoublesResult(0, 0, 0);
            }

            File.Move(tempOutputPath, outputFullPath, overwrite: true);
            progress?.Report((processed, $"Saved {kept:N0} unique games ({removed:N0} removed)."));

            return new RemoveDoublesResult(processed, kept, removed);
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

    private static string ComputeGameHash(PgnGame game)
    {
        var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in game.Headers)
        {
            headerMap[header.Key] = header.Value;
        }

        var sortedHeaders = headerMap
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}:{kvp.Value}");

        var normalizedMoves = NormalizeMoveText(game.MoveText);
        var blob = string.Concat(string.Join("|", sortedHeaders), "||", normalizedMoves);

        var bytes = Encoding.UTF8.GetBytes(blob);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes);
    }

    private static string NormalizeMoveText(string moveText)
    {
        if (string.IsNullOrWhiteSpace(moveText))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(moveText.Length);
        foreach (var move in TokenizeMoves(moveText))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(move);
        }

        return builder.ToString();
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
