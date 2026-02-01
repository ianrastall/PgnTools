using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public sealed record CheckmateFilterResult(long Processed, long Kept);

public interface ICheckmateFilterService
{
    Task<CheckmateFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public class CheckmateFilterService : ICheckmateFilterService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public CheckmateFilterService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task<CheckmateFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
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

        var processed = 0L;
        var kept = 0L;
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

                progress?.Report(0);

                await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;

                    if (ContainsCheckmate(game.MoveText))
                    {
                        if (!firstOutput)
                        {
                            await writer.WriteLineAsync();
                        }

                        await _pgnWriter.WriteGameAsync(writer, game, cancellationToken);
                        firstOutput = false;
                        kept++;
                    }

                    if (ShouldReportProgress(processed, ref lastProgressReport))
                    {
                        progress?.Report(GetProgressPercent(inputStream));
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

                progress?.Report(100);
                return new CheckmateFilterResult(0, 0);
            }

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
            progress?.Report(100);

            return new CheckmateFilterResult(processed, kept);
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
