using System.Runtime.CompilerServices;
using System.Text;

namespace PgnTools.Services;

/// <summary>
/// Streamed PGN writer that serializes games to a stream.
/// </summary>
public class PgnWriter
{
    private const int BufferSize = 65536;

    public async Task WriteGamesAsync(
        string filePath,
        IAsyncEnumerable<PgnGame> games,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        await WriteGamesAsync(stream, games, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteGamesAsync(
        Stream stream,
        IAsyncEnumerable<PgnGame> games,
        CancellationToken cancellationToken = default)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), BufferSize, leaveOpen: true);
        var firstGame = true;

        await foreach (var game in games.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!firstGame)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await WriteGameAsync(writer, game, cancellationToken).ConfigureAwait(false);
            firstGame = false;
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    public async Task WriteGameAsync(
        StreamWriter writer,
        PgnGame game,
        CancellationToken cancellationToken = default)
    {
        foreach (var header in game.Headers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // PGN requires escaping backslashes and quotes within tag values.
            var escapedValue = header.Value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            await writer.WriteLineAsync($"[{header.Key} \"{escapedValue}\"]").ConfigureAwait(false);
        }

        if (game.Headers.Count > 0)
        {
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(game.MoveText))
        {
            var normalized = NormalizeLineEndings(game.MoveText);
            foreach (var line in WordWrap(normalized, 80))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static IEnumerable<string> WordWrap(string text, int maxLineLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var span = text.AsSpan();
        var lineStart = 0;

        for (var i = 0; i <= span.Length; i++)
        {
            if (i < span.Length && span[i] != '\n')
            {
                continue;
            }

            var line = span.Slice(lineStart, i - lineStart);
            line = TrimEndSpan(line);

            if (line.Length == 0)
            {
                results.Add(string.Empty);
            }
            else
            {
                var remaining = line;
                while (remaining.Length > maxLineLength)
                {
                    var window = remaining[..maxLineLength];
                    var breakIndex = window.LastIndexOf(' ');
                    if (breakIndex <= 0)
                    {
                        results.Add(window.ToString());
                        remaining = TrimStartSpan(remaining[maxLineLength..]);
                        continue;
                    }

                    results.Add(remaining[..breakIndex].ToString());
                    remaining = TrimStartSpan(remaining[(breakIndex + 1)..]);
                }

                if (remaining.Length > 0)
                {
                    results.Add(remaining.ToString());
                }
            }

            lineStart = i + 1;
        }

        return results;
    }

    private static ReadOnlySpan<char> TrimStartSpan(ReadOnlySpan<char> span)
    {
        var start = 0;
        while (start < span.Length && char.IsWhiteSpace(span[start]))
        {
            start++;
        }

        return start == 0 ? span : span[start..];
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
}
