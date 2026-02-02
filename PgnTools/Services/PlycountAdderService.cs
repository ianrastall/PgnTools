using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public class PlycountAdderService : IPlycountAdderService
{
    private const int BufferSize = 65536;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public async Task AddPlyCountAsync(string inputFile, string outputFile, IProgress<double> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputFile) || string.IsNullOrWhiteSpace(outputFile))
            throw new ArgumentException("File paths required.");

        var inputFullPath = Path.GetFullPath(inputFile);
        var outputFullPath = Path.GetFullPath(outputFile);

        if (inputFullPath.Equals(outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Input and output cannot be the same file.");

        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

        long totalBytes = new FileInfo(inputFullPath).Length;
        long bytesRead = 0;
        DateTime lastReport = DateTime.MinValue;

        var headerBuffer = new StringBuilder();
        var moveBuffer = new StringBuilder();

        bool inHeaderSection = true;
        bool hasHeaders = false;

        try
        {
            await using var inStream = new FileStream(inputFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            await using var outStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);

            using var reader = new StreamReader(inStream, Encoding.UTF8, false, BufferSize);
            using var writer = new StreamWriter(outStream, new UTF8Encoding(false), BufferSize);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                bytesRead += Encoding.UTF8.GetByteCount(line) + 2; // Rough estimate including newline

                string trimmed = line.Trim();

                // 1. Empty Line Logic
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // If we were reading moves and hit a blank line, the game might be over.
                    // Or it's just a blank line inside moves.
                    // Standard PGN requires blank line after headers.
                    if (inHeaderSection && hasHeaders)
                    {
                        // Transition Header -> Moves (or just wait for next line)
                        inHeaderSection = false;
                    }
                    else if (!inHeaderSection && moveBuffer.Length > 0)
                    {
                        // Likely end of game. Flush.
                        await FlushGameAsync(writer, headerBuffer, moveBuffer);
                        inHeaderSection = true;
                        hasHeaders = false;
                    }
                    continue;
                }

                // 2. Header Logic
                if (trimmed.StartsWith('['))
                {
                    if (!inHeaderSection && moveBuffer.Length > 0)
                    {
                        // We hit a new header but haven't flushed previous moves? Flush now.
                        await FlushGameAsync(writer, headerBuffer, moveBuffer);
                        hasHeaders = false;
                    }

                    inHeaderSection = true;
                    hasHeaders = true;

                    // Skip existing PlyCount
                    if (!trimmed.StartsWith("[PlyCount", StringComparison.OrdinalIgnoreCase))
                    {
                        headerBuffer.AppendLine(trimmed);
                    }
                }
                // 3. Move Logic
                else
                {
                    inHeaderSection = false;
                    moveBuffer.AppendLine(line);
                }

                // Progress Reporting
                if (DateTime.UtcNow - lastReport > ProgressInterval)
                {
                    progress.Report((double)bytesRead / totalBytes * 100);
                    lastReport = DateTime.UtcNow;
                }
            }

            // Flush final game
            if (headerBuffer.Length > 0 || moveBuffer.Length > 0)
            {
                await FlushGameAsync(writer, headerBuffer, moveBuffer);
            }

            progress.Report(100);
        }
        catch
        {
            try { File.Delete(tempOutputPath); } catch { }
            throw;
        }

        FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
    }

    private async Task FlushGameAsync(StreamWriter writer, StringBuilder headers, StringBuilder moves)
    {
        string moveText = moves.ToString();
        int plies = CalculatePlies(moveText);

        if (headers.Length > 0)
        {
            await writer.WriteAsync(headers);
            await writer.WriteLineAsync($"[PlyCount \"{plies}\"]");
            await writer.WriteLineAsync();
        }

        if (moveText.Length > 0)
        {
            await writer.WriteLineAsync(moveText.TrimEnd());
            await writer.WriteLineAsync();
            await writer.WriteLineAsync();
        }

        headers.Clear();
        moves.Clear();
    }

    private int CalculatePlies(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        var token = new StringBuilder(16);
        var inBrace = false;
        var inLineComment = false;
        var variationDepth = 0;

        void FlushToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            var raw = token.ToString();
            token.Clear();
            if (IsMove(raw))
            {
                count++;
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inLineComment)
            {
                if (c is '\n' or '\r')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBrace)
            {
                if (c == '}')
                {
                    inBrace = false;
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
                FlushToken();
                inBrace = true;
                continue;
            }

            if (c == ';')
            {
                FlushToken();
                inLineComment = true;
                continue;
            }

            if (c == '(')
            {
                FlushToken();
                variationDepth = 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                FlushToken();
                continue;
            }

            token.Append(c);
        }

        FlushToken();
        return count;
    }

    private static bool IsMove(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var span = token.AsSpan().Trim();
        span = StripMoveNumberPrefix(span);
        span = TrimLeadingDots(span);

        if (span.Length == 0 || IsOnlyDots(span))
        {
            return false;
        }

        // Ignore results
        if (IsResultToken(span))
        {
            return false;
        }

        // Ignore NAGs
        if (span[0] == '$' && span.Length > 1 && IsAllDigits(span[1..]))
        {
            return false;
        }

        span = StripTrailingNag(span);
        span = StripTrailingAnnotations(span);
        if (span.Length == 0)
        {
            return false;
        }

        if (IsResultToken(span))
        {
            return false;
        }

        // Basic sanity check: Must contain letters or O-O
        if (span.Equals("e.p.", StringComparison.OrdinalIgnoreCase) ||
            span.Equals("ep", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsCastling(span))
        {
            return true;
        }

        // e4, Nf3, exd5, R1a3, etc.
        // Should start with a letter (K, Q, R, B, N, a-h)
        var first = span[0];
        return char.IsLetter(first);
    }

    private static ReadOnlySpan<char> StripMoveNumberPrefix(ReadOnlySpan<char> token)
    {
        var index = 0;
        while (index < token.Length && char.IsDigit(token[index]))
        {
            index++;
        }

        if (index == 0)
        {
            return token;
        }

        if (index >= token.Length || token[index] != '.')
        {
            return token;
        }

        while (index < token.Length && token[index] == '.')
        {
            index++;
        }

        return index >= token.Length ? ReadOnlySpan<char>.Empty : token[index..].TrimStart();
    }

    private static ReadOnlySpan<char> TrimLeadingDots(ReadOnlySpan<char> token)
    {
        var index = 0;
        while (index < token.Length && token[index] == '.')
        {
            index++;
        }

        return index == 0 ? token : token[index..];
    }

    private static bool IsOnlyDots(ReadOnlySpan<char> token)
    {
        foreach (var c in token)
        {
            if (c != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsResultToken(ReadOnlySpan<char> token)
    {
        return token.Equals("1-0", StringComparison.Ordinal) ||
               token.Equals("0-1", StringComparison.Ordinal) ||
               token.Equals("1/2-1/2", StringComparison.Ordinal) ||
               token.Equals("*", StringComparison.Ordinal);
    }

    private static ReadOnlySpan<char> StripTrailingNag(ReadOnlySpan<char> token)
    {
        var index = token.LastIndexOf('$');
        if (index < 0 || index == token.Length - 1)
        {
            return token;
        }

        var digits = token[(index + 1)..];
        foreach (var c in digits)
        {
            if (!char.IsDigit(c))
            {
                return token;
            }
        }

        return token[..index];
    }

    private static ReadOnlySpan<char> StripTrailingAnnotations(ReadOnlySpan<char> token)
    {
        var span = token;
        while (span.Length > 0)
        {
            var last = span[^1];
            if (last is '!' or '?' or '+' or '#')
            {
                span = span[..^1];
                continue;
            }

            break;
        }

        return span;
    }

    private static bool IsCastling(ReadOnlySpan<char> token)
    {
        return token.Equals("O-O", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("O-O-O", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("0-0", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("0-0-0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        if (span.Length == 0)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
