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
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            var remaining = line.TrimEnd();
            if (remaining.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            while (remaining.Length > maxLineLength)
            {
                var breakIndex = remaining.LastIndexOf(' ', maxLineLength);
                if (breakIndex <= 0)
                {
                    yield return remaining[..maxLineLength];
                    remaining = remaining[maxLineLength..].TrimStart();
                    continue;
                }

                yield return remaining[..breakIndex];
                remaining = remaining[(breakIndex + 1)..].TrimStart();
            }

            if (remaining.Length > 0)
            {
                yield return remaining;
            }
        }
    }
}
