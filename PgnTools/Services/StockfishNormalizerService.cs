using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PgnTools.Services;

public sealed record StockfishNormalizeResult(long Processed, long TagsUpdated);

public interface IStockfishNormalizerService
{
    Task<StockfishNormalizeResult> NormalizeAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}

public partial class StockfishNormalizerService : IStockfishNormalizerService
{
    private const int BufferSize = 65536;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);

    private static readonly IReadOnlyList<VersionRange> Mappings =
    [
        new("Stockfish 1.0", new DateOnly(2008, 11, 2), new DateOnly(2008, 11, 2)),
        new("Stockfish 1.01", new DateOnly(2008, 11, 3), new DateOnly(2008, 12, 5)),
        new("Stockfish 1.1", new DateOnly(2008, 12, 6), new DateOnly(2008, 12, 7)),
        new("Stockfish 1.1a", new DateOnly(2008, 12, 8), new DateOnly(2008, 12, 28)),
        new("Stockfish 1.2", new DateOnly(2008, 12, 29), new DateOnly(2009, 5, 1)),
        new("Stockfish 1.3", new DateOnly(2009, 5, 2), new DateOnly(2009, 5, 2)),
        new("Stockfish 1.3.1", new DateOnly(2009, 5, 3), new DateOnly(2009, 7, 4)),
        new("Stockfish 1.4", new DateOnly(2009, 7, 5), new DateOnly(2009, 10, 3)),
        new("Stockfish 1.5", new DateOnly(2009, 10, 4), new DateOnly(2009, 10, 10)),
        new("Stockfish 1.5.1", new DateOnly(2009, 10, 11), new DateOnly(2009, 12, 23)),
        new("Stockfish 1.6", new DateOnly(2009, 12, 24), new DateOnly(2009, 12, 24)),
        new("Stockfish 1.6.1", new DateOnly(2009, 12, 25), new DateOnly(2009, 12, 30)),
        new("Stockfish 1.6.2", new DateOnly(2009, 12, 31), new DateOnly(2010, 2, 1)),
        new("Stockfish 1.6.3", new DateOnly(2010, 2, 2), new DateOnly(2010, 4, 7)),
        new("Stockfish 1.7", new DateOnly(2010, 4, 8), new DateOnly(2010, 4, 9)),
        new("Stockfish 1.7.1", new DateOnly(2010, 4, 10), new DateOnly(2010, 7, 1)),
        new("Stockfish 1.8", new DateOnly(2010, 7, 2), new DateOnly(2010, 10, 1)),
        new("Stockfish 1.9", new DateOnly(2010, 10, 2), new DateOnly(2010, 10, 4)),
        new("Stockfish 1.9.1", new DateOnly(2010, 10, 5), new DateOnly(2010, 12, 31)),
        new("Stockfish 2.0", new DateOnly(2011, 1, 1), new DateOnly(2011, 1, 3)),
        new("Stockfish 2.0.1", new DateOnly(2011, 1, 4), new DateOnly(2011, 5, 3)),
        new("Stockfish 2.1", new DateOnly(2011, 5, 4), new DateOnly(2011, 5, 7)),
        new("Stockfish 2.1.1", new DateOnly(2011, 5, 8), new DateOnly(2011, 12, 28)),
        new("Stockfish 2.2", new DateOnly(2011, 12, 29), new DateOnly(2012, 1, 5)),
        new("Stockfish 2.2.1", new DateOnly(2012, 1, 6), new DateOnly(2012, 1, 13)),
        new("Stockfish 2.2.2", new DateOnly(2012, 1, 14), new DateOnly(2012, 9, 14)),
        new("Stockfish 2.3", new DateOnly(2012, 9, 15), new DateOnly(2012, 9, 21)),
        new("Stockfish 2.3.1", new DateOnly(2012, 9, 22), new DateOnly(2013, 4, 29)),
        new("Stockfish 3", new DateOnly(2013, 4, 30), new DateOnly(2013, 8, 19)),
        new("Stockfish 4", new DateOnly(2013, 8, 20), new DateOnly(2013, 11, 28)),
        new("Stockfish DD", new DateOnly(2013, 11, 29), new DateOnly(2014, 5, 30)),
        new("Stockfish 5", new DateOnly(2014, 5, 31), new DateOnly(2015, 1, 26)),
        new("Stockfish 6", new DateOnly(2015, 1, 27), new DateOnly(2016, 1, 1)),
        new("Stockfish 7", new DateOnly(2016, 1, 2), new DateOnly(2016, 10, 31)),
        new("Stockfish 8", new DateOnly(2016, 11, 1), new DateOnly(2018, 1, 31)),
        new("Stockfish 9", new DateOnly(2018, 2, 1), new DateOnly(2018, 11, 28)),
        new("Stockfish 10", new DateOnly(2018, 11, 29), new DateOnly(2020, 1, 14)),
        new("Stockfish 11", new DateOnly(2020, 1, 15), new DateOnly(2020, 9, 1)),
        new("Stockfish 12", new DateOnly(2020, 9, 2), new DateOnly(2021, 2, 18)),
        new("Stockfish 13", new DateOnly(2021, 2, 19), new DateOnly(2021, 7, 1)),
        new("Stockfish 14", new DateOnly(2021, 7, 2), new DateOnly(2021, 10, 27)),
        new("Stockfish 14.1", new DateOnly(2021, 10, 28), new DateOnly(2022, 4, 17)),
        new("Stockfish 15", new DateOnly(2022, 4, 18), new DateOnly(2022, 12, 3)),
        new("Stockfish 15.1", new DateOnly(2022, 12, 4), new DateOnly(2023, 6, 29)),
        new("Stockfish 16", new DateOnly(2023, 6, 30), new DateOnly(2024, 2, 23)),
        new("Stockfish 16.1", new DateOnly(2024, 2, 24), new DateOnly(2024, 9, 5)),
        new("Stockfish 17", new DateOnly(2024, 9, 6), new DateOnly(2025, 3, 29)),
        new("Stockfish 17.1", new DateOnly(2025, 3, 30), new DateOnly(2026, 1, 30)),
        new("Stockfish 18", new DateOnly(2026, 1, 31), new DateOnly(9999, 12, 31)),
    ];

    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public StockfishNormalizerService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    [GeneratedRegex(@"(?i)Stockfish.*?(\b\d{6}\b|\b\d{8}\b)")]
    private static partial Regex StockfishVersionRegex();

    public async Task<StockfishNormalizeResult> NormalizeAsync(
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

        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

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

        var processed = 0L;
        var tagsUpdated = 0L;
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

                await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;

                    var updates = new List<KeyValuePair<string, string>>();

                    foreach (var header in game.Headers)
                    {
                        var headerValue = header.Value ?? string.Empty;
                        if (IsPlayerHeader(header.Key) &&
                            headerValue.Contains("Stockfish", StringComparison.OrdinalIgnoreCase))
                        {
                            var normalized = NormalizeStockfishName(headerValue);
                            if (!string.Equals(headerValue, normalized, StringComparison.Ordinal))
                            {
                                tagsUpdated++;
                                updates.Add(new KeyValuePair<string, string>(header.Key, normalized));
                            }
                        }
                        else if (!string.Equals(headerValue, header.Value, StringComparison.Ordinal))
                        {
                            updates.Add(new KeyValuePair<string, string>(header.Key, headerValue));
                        }
                    }

                    foreach (var update in updates)
                    {
                        game.Headers[update.Key] = update.Value;
                    }

                    if (!firstOutput)
                    {
                        await writer.WriteLineAsync().ConfigureAwait(false);
                    }

                    await _pgnWriter.WriteGameAsync(writer, game, cancellationToken).ConfigureAwait(false);
                    firstOutput = false;

                    if (ShouldReportProgress(processed, ref lastProgressReport))
                    {
                        progress?.Report((processed, $"Processing Game {processed:N0}... ({tagsUpdated:N0} tags updated)"));
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

                progress?.Report((0, "No games found."));
                return new StockfishNormalizeResult(0, 0);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await FileReplacementHelper.ReplaceFileAsync(tempOutputPath, outputFullPath, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report((processed, $"Normalized {tagsUpdated:N0} tag(s) across {processed:N0} games."));

            return new StockfishNormalizeResult(processed, tagsUpdated);
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

    private static bool IsPlayerHeader(string headerKey) =>
        string.Equals(headerKey, "White", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerKey, "Black", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeStockfishName(string originalName)
    {
        var match = StockfishVersionRegex().Match(originalName);
        if (!match.Success)
        {
            return originalName;
        }

        var dateStr = match.Groups[1].Value;
        if (!TryParseDate(dateStr, out var date))
        {
            return originalName;
        }

        foreach (var mapping in Mappings)
        {
            if (date >= mapping.Start && date <= mapping.End)
            {
                return mapping.Name;
            }
        }

        return originalName;
    }

    private static bool TryParseDate(string digits, out DateOnly date)
    {
        date = default;
        var minYear = 2008;
        var maxYear = DateTime.UtcNow.Year + 1;

        if (digits.Length == 8)
        {
            if (!DateOnly.TryParseExact(
                digits,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            {
                return false;
            }

            if (parsed.Year < minYear || parsed.Year > maxYear)
            {
                return false;
            }

            date = parsed;
            return true;
        }

        if (digits.Length == 6)
        {
            if (!int.TryParse(digits[..2], out var yearPart) ||
                !int.TryParse(digits.Substring(2, 2), out var month) ||
                !int.TryParse(digits.Substring(4, 2), out var day))
            {
                return false;
            }

            var year = 2000 + yearPart;
            if (year < minYear || year > maxYear)
            {
                return false;
            }

            try
            {
                date = new DateOnly(year, month, day);
                return true;
            }
            catch
            {
                return false;
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

    private sealed record VersionRange(string Name, DateOnly Start, DateOnly End);
}
