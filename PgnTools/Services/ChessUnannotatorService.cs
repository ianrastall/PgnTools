using System.Runtime.CompilerServices;
using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

/// <summary>
/// Interface for stripping annotations from PGN files.
/// </summary>
public interface IChessUnannotatorService
{
    Task UnannotateAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service that removes comments, variations, NAGs, and symbolic annotations from PGN move text.
/// </summary>
public class ChessUnannotatorService : IChessUnannotatorService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public ChessUnannotatorService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader ?? throw new ArgumentNullException(nameof(pgnReader));
        _pgnWriter = pgnWriter ?? throw new ArgumentNullException(nameof(pgnWriter));
    }

    public async Task UnannotateAsync(
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

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Input and output files must be different. To modify in-place, use a temporary output file first.");
        }

        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

        long games = 0;
        var lastProgressReportUtc = DateTime.MinValue;
        var lastReportedGame = 0L;

        try
        {
            await using var inputStream = new FileStream(
                inputFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);
            await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cleanedMoveText = StripAnnotations(game.MoveText);
                var cleanedGame = new PgnGame(game.Headers, cleanedMoveText);

                if (games > 0)
                {
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }

                await _pgnWriter.WriteGameAsync(writer, cleanedGame, cancellationToken).ConfigureAwait(false);
                games++;

                if (ShouldReportProgress(games, ref lastProgressReportUtc, ref lastReportedGame))
                {
                    progress?.Report((games, $"Processing Game {games:N0}..."));
                }
            }

            await writer.FlushAsync().ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (games == 0)
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
                progress?.Report((0, "No games found."));
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report((games, $"Saved {games:N0} games."));
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

    private static bool ShouldReportProgress(long games, ref DateTime lastReportUtc, ref long lastReportedGame)
    {
        if (games <= 0)
        {
            return false;
        }

        if (games == lastReportedGame)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var dueByCount = games == 1 || games % ProgressGameInterval == 0;
        var dueByTime = now - lastReportUtc >= ProgressTimeInterval;
        if (!dueByCount && !dueByTime)
        {
            return false;
        }

        lastReportUtc = now;
        lastReportedGame = games;
        return true;
    }

    private static string StripAnnotations(string moveText)
    {
        if (string.IsNullOrWhiteSpace(moveText))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(moveText.Length);
        var inBraceComment = false;
        var inLineComment = false;
        var variationDepth = 0;
        var lastWasSpace = true;
        var pendingSpace = false;

        for (var i = 0; i < moveText.Length; i++)
        {
            var c = moveText[i];

            if (inLineComment)
            {
                if (c == '\n' || c == '\r')
                {
                    inLineComment = false;
                    pendingSpace = true;
                }
                continue;
            }

            if (inBraceComment)
            {
                if (c == '}')
                {
                    inBraceComment = false;
                    pendingSpace = true;
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

                if (variationDepth == 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (c == '{')
            {
                inBraceComment = true;
                pendingSpace = true;
                continue;
            }

            if (c == ';')
            {
                inLineComment = true;
                pendingSpace = true;
                continue;
            }

            if (c == '(')
            {
                variationDepth = 1;
                pendingSpace = true;
                continue;
            }

            if (c == ')')
            {
                pendingSpace = true;
                continue;
            }

            if (c == '$')
            {
                if (i + 1 < moveText.Length && char.IsDigit(moveText[i + 1]))
                {
                    while (i + 1 < moveText.Length && char.IsDigit(moveText[i + 1]))
                    {
                        i++;
                    }

                    pendingSpace = true;
                    continue;
                }
            }

            if (c is '!' or '?')
            {
                while (i + 1 < moveText.Length && moveText[i + 1] is '!' or '?')
                {
                    i++;
                }

                pendingSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && !lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }

            pendingSpace = false;
            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}
