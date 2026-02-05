using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace PgnTools.Services;

/// <summary>
/// Streamed PGN reader that yields games one at a time.
/// </summary>
public partial class PgnReader
{
    private const int BufferSize = 262144;
    private const int MaxLineLength = 262144;

    public PgnReader()
    {
    }

    public async IAsyncEnumerable<PgnGame> ReadGamesAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var game in ReadGamesAsync(filePath, readMoveText: true, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return game;
        }
    }

    public async IAsyncEnumerable<PgnGame> ReadGamesAsync(
        string filePath,
        bool readMoveText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        await foreach (var game in ReadGamesAsync(stream, readMoveText, cancellationToken).ConfigureAwait(false))
        {
            yield return game;
        }
    }

    public async IAsyncEnumerable<PgnGame> ReadGamesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var game in ReadGamesAsync(stream, readMoveText: true, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return game;
        }
    }

    public async IAsyncEnumerable<PgnGame> ReadGamesAsync(
        Stream stream,
        bool readMoveText,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: BufferSize,
            leaveOpen: true);

        var currentGame = (PgnGame?)null;
        var moveText = readMoveText ? new StringBuilder(2048) : null;
        var inMoveSection = false;
        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;
        var lineBuffer = ArrayPool<char>.Shared.Rent(256);
        var lineLength = 0;
        var pendingCarriageReturn = false;
        var buffer = new char[BufferSize];

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];

                    if (pendingCarriageReturn)
                    {
                        pendingCarriageReturn = false;
                        if (c == '\n')
                        {
                            continue;
                        }
                    }

                    if (c == '\r')
                    {
                        if (TryReadLine(
                            lineBuffer.AsSpan(0, lineLength),
                            ref currentGame,
                            moveText,
                            readMoveText,
                            ref inMoveSection,
                            ref inBraceComment,
                            ref inLineComment,
                            ref variationDepth,
                            out var completed))
                        {
                            if (completed != null)
                            {
                                yield return completed;
                            }
                        }

                        lineLength = 0;
                        pendingCarriageReturn = true;
                        continue;
                    }

                    if (c == '\n')
                    {
                        if (TryReadLine(
                            lineBuffer.AsSpan(0, lineLength),
                            ref currentGame,
                            moveText,
                            readMoveText,
                            ref inMoveSection,
                            ref inBraceComment,
                            ref inLineComment,
                            ref variationDepth,
                            out var completed))
                        {
                            if (completed != null)
                            {
                                yield return completed;
                            }
                        }

                        lineLength = 0;
                        continue;
                    }

                    if (lineLength >= MaxLineLength)
                    {
                        throw new InvalidDataException($"PGN line exceeds {MaxLineLength} characters.");
                    }

                    if (lineLength == lineBuffer.Length)
                    {
                        var nextSize = Math.Max(lineBuffer.Length * 2, lineLength + 1);
                        var nextBuffer = ArrayPool<char>.Shared.Rent(nextSize);
                        lineBuffer.AsSpan(0, lineLength).CopyTo(nextBuffer);
                        ArrayPool<char>.Shared.Return(lineBuffer);
                        lineBuffer = nextBuffer;
                    }

                    lineBuffer[lineLength++] = c;
                }
            }

            if (lineLength > 0)
            {
                if (TryReadLine(
                    lineBuffer.AsSpan(0, lineLength),
                    ref currentGame,
                    moveText,
                    readMoveText,
                    ref inMoveSection,
                    ref inBraceComment,
                    ref inLineComment,
                    ref variationDepth,
                    out var completed))
                {
                    if (completed != null)
                    {
                        yield return completed;
                    }
                }
            }

            if (currentGame != null &&
                (currentGame.Headers.Count > 0 || (moveText?.Length ?? 0) > 0))
            {
                if (moveText != null)
                {
                    currentGame.MoveText = moveText.ToString().Trim();
                }
                yield return currentGame;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(lineBuffer);
        }
    }

    private static bool TryReadLine(
        ReadOnlySpan<char> lineSpan,
        ref PgnGame? currentGame,
        StringBuilder? moveText,
        bool readMoveText,
        ref bool inMoveSection,
        ref bool inBraceComment,
        ref bool inLineComment,
        ref int variationDepth,
        out PgnGame? completedGame)
    {
        completedGame = null;
        if (lineSpan.Length == 0)
        {
            if (inMoveSection)
            {
                if (readMoveText && moveText != null)
                {
                    moveText.AppendLine();
                }
            }

            return false;
        }

        var trimmedSpan = TrimSpan(lineSpan);

        if (trimmedSpan.Length == 0)
        {
            if (inMoveSection)
            {
                if (readMoveText && moveText != null)
                {
                    moveText.AppendLine();
                }
            }

            return false;
        }

        // Only treat as tag line when we're not deep in comments/variations.
        var isTagLine = trimmedSpan[0] == '[' && (!inMoveSection || (!inBraceComment && variationDepth == 0));
        if (isTagLine)
        {
            if (TryParseHeaderLine(trimmedSpan, out var tagKey, out var rawValue))
            {
                if (inMoveSection && currentGame != null)
                {
                    if (readMoveText && moveText != null)
                    {
                        currentGame.MoveText = moveText.ToString().Trim();
                    }
                    completedGame = currentGame;
                    currentGame = new PgnGame();
                    if (readMoveText && moveText != null)
                    {
                        moveText.Clear();
                    }
                    inMoveSection = false;
                    inBraceComment = false;
                    inLineComment = false;
                    variationDepth = 0;
                }
                else
                {
                    currentGame ??= new PgnGame();
                }

                currentGame.Headers[tagKey] = UnescapePgnString(rawValue);
                if (!ContainsHeaderKey(currentGame.HeaderOrder, tagKey))
                {
                    currentGame.HeaderOrder.Add(tagKey);
                }
                return completedGame != null;
            }
        }

        inMoveSection = true;
        currentGame ??= new PgnGame();

        if (readMoveText && moveText != null && moveText.Length > 0)
        {
            moveText.AppendLine();
        }

        if (readMoveText && moveText != null)
        {
            moveText.Append(TrimEndSpan(lineSpan));
        }
        UpdateMoveState(lineSpan, ref inBraceComment, ref inLineComment, ref variationDepth);
        UpdateMoveState(LineBreak, ref inBraceComment, ref inLineComment, ref variationDepth);
        return false;
    }

    private static string UnescapePgnString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var length = 0;
        var escaping = false;

        foreach (var c in value)
        {
            if (!escaping && c == '\\')
            {
                escaping = true;
                continue;
            }

            length++;
            escaping = false;
        }

        if (escaping)
        {
            length++;
        }

        return string.Create(length, value, (span, state) =>
        {
            var idx = 0;
            var isEscaping = false;

            foreach (var c in state)
            {
                if (isEscaping)
                {
                    span[idx++] = c;
                    isEscaping = false;
                    continue;
                }

                if (c == '\\')
                {
                    isEscaping = true;
                    continue;
                }

                span[idx++] = c;
            }

            if (isEscaping && idx < span.Length)
            {
                span[idx] = '\\';
            }
        });
    }

    private static void UpdateMoveState(
        ReadOnlySpan<char> text,
        ref bool inBraceComment,
        ref bool inLineComment,
        ref int variationDepth)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inLineComment)
            {
                if (c == '\n' || c == '\r')
                {
                    inLineComment = false;
                    continue;
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
                    break;
                case ';':
                    inLineComment = true;
                    break;
                case '(':
                    variationDepth++;
                    break;
                case ')':
                    if (variationDepth > 0)
                    {
                        variationDepth--;
                    }

                    break;
            }
        }
    }

    private static void ResetState(
        ref PgnGame? currentGame,
        StringBuilder? moveText,
        bool readMoveText,
        ref bool inMoveSection,
        ref bool inBraceComment,
        ref bool inLineComment,
        ref int variationDepth)
    {
        currentGame = null;
        if (readMoveText && moveText != null)
        {
            moveText.Clear();
        }
        inMoveSection = false;
        inBraceComment = false;
        inLineComment = false;
        variationDepth = 0;
    }

    private static bool ContainsHeaderKey(List<string> order, string key)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> TrimSpan(ReadOnlySpan<char> span)
    {
        var start = 0;
        while (start < span.Length && char.IsWhiteSpace(span[start]))
        {
            start++;
        }

        var end = span.Length - 1;
        while (end >= start && char.IsWhiteSpace(span[end]))
        {
            end--;
        }

        return start > end ? ReadOnlySpan<char>.Empty : span.Slice(start, end - start + 1);
    }

    private static ReadOnlySpan<char> TrimEndSpan(ReadOnlySpan<char> span)
    {
        var end = span.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(span[end]))
        {
            end--;
        }

        return end < 0 ? ReadOnlySpan<char>.Empty : span[..(end + 1)];
    }

    private static bool TryParseHeaderLine(ReadOnlySpan<char> line, out string key, out string rawValue)
    {
        return TryParseHeaderFallback(line, out key, out rawValue);
    }

    private static bool TryParseHeaderFallback(ReadOnlySpan<char> line, out string key, out string rawValue)
    {
        key = string.Empty;
        rawValue = string.Empty;

        var span = line.Trim();
        if (span.Length < 2 || span[0] != '[')
        {
            return false;
        }

        span = span[1..].TrimStart();
        if (span.Length == 0)
        {
            return false;
        }

        var nameLength = 0;
        while (nameLength < span.Length && !char.IsWhiteSpace(span[nameLength]) && span[nameLength] != ']')
        {
            nameLength++;
        }

        if (nameLength == 0)
        {
            return false;
        }

        var nameSpan = span[..nameLength];
        span = span[nameLength..].TrimStart();
        if (span.Length == 0)
        {
            key = nameSpan.ToString();
            rawValue = string.Empty;
            return true;
        }

        if (span[0] == '"' || span[0] == '\'')
        {
            var quote = span[0];
            var builder = new StringBuilder();
            var escaping = false;

            for (var i = 1; i < span.Length; i++)
            {
                var c = span[i];

                if (escaping)
                {
                    builder.Append(c);
                    escaping = false;
                    continue;
                }

                if (c == '\\')
                {
                    builder.Append(c);
                    escaping = true;
                    continue;
                }

                if (c == quote)
                {
                    key = nameSpan.ToString();
                    rawValue = builder.ToString();
                    return true;
                }

                builder.Append(c);
            }

            key = nameSpan.ToString();
            rawValue = builder.ToString();
            return true;
        }

        var closingIndex = span.IndexOf(']');
        if (closingIndex < 0)
        {
            closingIndex = span.Length;
        }

        key = nameSpan.ToString();
        rawValue = span[..closingIndex].ToString().TrimEnd();
        return true;
    }

    private static readonly char[] LineBreak = ['\n'];
}
