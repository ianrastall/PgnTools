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
/// Service that removes comments, variations, and NAGs from PGN move text.
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
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task UnannotateAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputFilePath);
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Input and output files must be different. To modify in-place, use a temporary output file first.");
        }

        long games = 0;
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
                await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var cleanedMoveText = StripAnnotations(game.MoveText);
                    var cleanedGame = new PgnGame(game.Headers, cleanedMoveText);

                    if (games > 0)
                    {
                        await writer.WriteLineAsync();
                    }

                    await _pgnWriter.WriteGameAsync(writer, cleanedGame, cancellationToken);
                    games++;

                    if (ShouldReportProgress(games, ref lastProgressReport))
                    {
                        progress?.Report((games, $"Processing Game {games:N0}..."));
                    }
                }

                await writer.FlushAsync();
            }

            if (games == 0)
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
                progress?.Report((0, "No games found."));
                return;
            }

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
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

    private static string StripAnnotations(string moveText)
    {
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

            if (inLineComment)
            {
                if (c == '\n' || c == '\r')
                {
                    inLineComment = false;
                    Emit(builder, c, ref lastWasSpace);
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

            if (c == '{')
            {
                inBraceComment = true;
                continue;
            }

            if (c == ';')
            {
                inLineComment = true;
                continue;
            }

            if (c == '(')
            {
                variationDepth++;
                continue;
            }

            if (c == ')')
            {
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

            if (c == '$')
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
}
