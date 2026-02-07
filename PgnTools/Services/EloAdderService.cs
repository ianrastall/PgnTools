using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public interface IRatingDatabase
{
    int? Lookup(string name, int year, int month);
}

public sealed class EmptyRatingDatabase : IRatingDatabase
{
    public int? Lookup(string name, int year, int month) => null;
}

public interface IEloAdderService
{
    Task AddElosAsync(
        string inputFilePath,
        string outputFilePath,
        IRatingDatabase db,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

public class EloAdderService : IEloAdderService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public EloAdderService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    public async Task AddElosAsync(
        string inputFilePath,
        string outputFilePath,
        IRatingDatabase db,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new ArgumentException("Input file path is required.", nameof(inputFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFilePath));
        }

        if (db is null)
        {
            throw new ArgumentNullException(nameof(db));
        }

        var inputFullPath = Path.GetFullPath(inputFilePath);
        var outputFullPath = Path.GetFullPath(outputFilePath);
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

        if (!File.Exists(inputFullPath))
        {
            throw new FileNotFoundException("Input PGN file not found.", inputFullPath);
        }

        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Input and output files must be different.");
        }

        if (Path.GetDirectoryName(outputFullPath) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var processed = 0L;
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

                await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;

                    AddRatings(game, db);

                    if (!firstOutput)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await _pgnWriter.WriteGameAsync(writer, game, ct).ConfigureAwait(false);
                    firstOutput = false;

                    if (ShouldReportProgress(processed, ref lastProgressReport))
                    {
                        progress?.Report(GetProgressPercent(inputStream));
                    }
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
                return;
            }

            ct.ThrowIfCancellationRequested();
            await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, ct).ConfigureAwait(false);
            progress?.Report(100);
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

    private static int AddRatings(PgnGame game, IRatingDatabase db)
    {
        if (!TryParseDate(game.Headers.GetHeaderValueOrDefault("Date"), out var year, out var month))
        {
            return 0;
        }

        var added = 0;

        if (game.Headers.TryGetHeaderValue("White", out var white))
        {
            if (!game.Headers.TryGetHeaderValue("WhiteElo", out _))
            {
                var elo = db.Lookup(white, year, month);
                if (elo.HasValue)
                {
                    game.Headers["WhiteElo"] = elo.Value.ToString(CultureInfo.InvariantCulture);
                    added++;
                }
            }
        }

        if (game.Headers.TryGetHeaderValue("Black", out var black))
        {
            if (!game.Headers.TryGetHeaderValue("BlackElo", out _))
            {
                var elo = db.Lookup(black, year, month);
                if (elo.HasValue)
                {
                    game.Headers["BlackElo"] = elo.Value.ToString(CultureInfo.InvariantCulture);
                    added++;
                }
            }
        }

        return added;
    }

    private static bool TryParseDate(string? date, out int year, out int month)
    {
        year = 0;
        month = 0;

        if (string.IsNullOrWhiteSpace(date))
        {
            return false;
        }

        var normalized = date.Replace('/', '.').Replace('-', '.');
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out year) || year < 1)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out month) || month < 1 || month > 12)
        {
            return false;
        }

        return true;
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

        var now = DateTime.UtcNow;
        var gameIntervalMet = games == 1 || games % ProgressGameInterval == 0;
        var timeIntervalMet = now - lastReportUtc >= ProgressTimeInterval;
        if (!gameIntervalMet && !timeIntervalMet)
        {
            return false;
        }

        lastReportUtc = now;
        return true;
    }
}
