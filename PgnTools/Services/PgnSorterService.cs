using System.Text;

namespace PgnTools.Services;

public enum SortCriterion
{
    Event,
    Site,
    Date,
    Round,
    White,
    Black,
    Eco
}

public interface IPgnSorterService
{
    Task SortPgnAsync(
        string inputFilePath,
        string outputFilePath,
        IReadOnlyList<SortCriterion> criteria,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

public class PgnSorterService : IPgnSorterService
{
    private const int BufferSize = 65536;
    private const int SortChunkGameLimit = 100_000;
    private const int ProgressGameInterval = 200;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);
    private readonly PgnReader _pgnReader;
    private readonly PgnWriter _pgnWriter;

    public PgnSorterService(PgnReader pgnReader, PgnWriter pgnWriter)
    {
        _pgnReader = pgnReader;
        _pgnWriter = pgnWriter;
    }

    // Cache per-game sort keys to avoid repeated dictionary lookups during sorting.
    private readonly struct SortableGame
    {
        public PgnGame Game { get; }
        public string[] SortKeys { get; }

        public SortableGame(PgnGame game, IReadOnlyList<SortCriterion> criteria)
        {
            Game = game;
            SortKeys = new string[criteria.Count];

            for (var i = 0; i < criteria.Count; i++)
            {
                SortKeys[i] = GetHeaderValue(game, criteria[i]);
            }
        }
    }

    public async Task SortPgnAsync(
        string inputFilePath,
        string outputFilePath,
        IReadOnlyList<SortCriterion> criteria,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new ArgumentException("Input file path is required.", nameof(inputFilePath));
        }

        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFilePath));
        }

        if (criteria is null)
        {
            throw new ArgumentNullException(nameof(criteria));
        }

        await Task.Run(
            async () =>
            {
                var inputFullPath = Path.GetFullPath(inputFilePath);
                var outputFullPath = Path.GetFullPath(outputFilePath);
                var tempOutputPath = outputFullPath + ".tmp";
                var tempDirectory = Path.Combine(
                    Path.GetDirectoryName(outputFullPath) ?? Path.GetTempPath(),
                    $"pgnsort_{Guid.NewGuid():N}");

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

                var lastProgressReport = DateTime.MinValue;
                var chunkFiles = new List<string>();
                var totalGames = 0L;

                try
                {
                    await using (var inputStream = new FileStream(
                        inputFullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.SequentialScan | FileOptions.Asynchronous))
                    {
                        progress?.Report(0);

                        if (criteria.Count == 0)
                        {
                            var copied = await CopyWithoutSortAsync(inputStream, tempOutputPath, progress, ct)
                                .ConfigureAwait(false);

                            if (copied == 0)
                            {
                                if (File.Exists(tempOutputPath))
                                {
                                    File.Delete(tempOutputPath);
                                }

                                progress?.Report(100);
                                return;
                            }

                            File.Move(tempOutputPath, outputFullPath, overwrite: true);
                            progress?.Report(100);
                            return;
                        }

                        Directory.CreateDirectory(tempDirectory);
                        var games = new List<PgnGame>(SortChunkGameLimit);

                        await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, ct).ConfigureAwait(false))
                        {
                            ct.ThrowIfCancellationRequested();
                            games.Add(game);
                            totalGames++;

                            if (games.Count >= SortChunkGameLimit)
                            {
                                await WriteSortedChunkAsync(games, criteria, tempDirectory, chunkFiles, ct)
                                    .ConfigureAwait(false);
                                games.Clear();
                            }

                            if (ShouldReportProgress(totalGames, ref lastProgressReport))
                            {
                                progress?.Report(GetProgressPercent(inputStream) * 0.6);
                            }
                        }

                        if (games.Count > 0)
                        {
                            await WriteSortedChunkAsync(games, criteria, tempDirectory, chunkFiles, ct)
                                .ConfigureAwait(false);
                            games.Clear();
                        }
                    }

                    if (totalGames == 0)
                    {
                        progress?.Report(100);
                        return;
                    }

                    progress?.Report(60);
                    ct.ThrowIfCancellationRequested();

                    if (chunkFiles.Count == 1)
                    {
                        File.Move(chunkFiles[0], tempOutputPath, overwrite: true);
                    }
                    else
                    {
                        await MergeSortedChunksAsync(chunkFiles, tempOutputPath, criteria, totalGames, progress, ct)
                            .ConfigureAwait(false);
                    }

                    File.Move(tempOutputPath, outputFullPath, overwrite: true);
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
                finally
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        try
                        {
                            Directory.Delete(tempDirectory, recursive: true);
                        }
                        catch
                        {
                        }
                    }
                }
            },
            ct).ConfigureAwait(false);
    }

    private async Task<long> CopyWithoutSortAsync(
        Stream inputStream,
        string tempOutputPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var games = 0L;
        var lastProgressReport = DateTime.MinValue;

        await using var outputStream = new FileStream(
            tempOutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);
        var firstOutput = true;

        await foreach (var game in _pgnReader.ReadGamesAsync(inputStream, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (!firstOutput)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await _pgnWriter.WriteGameAsync(writer, game, ct).ConfigureAwait(false);
            firstOutput = false;
            games++;

            if (ShouldReportProgress(games, ref lastProgressReport))
            {
                progress?.Report(GetProgressPercent(inputStream));
            }
        }

        await writer.FlushAsync().ConfigureAwait(false);
        return games;
    }

    private async Task WriteSortedChunkAsync(
        List<PgnGame> games,
        IReadOnlyList<SortCriterion> criteria,
        string tempDirectory,
        List<string> chunkFiles,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var sortableGames = new SortableGame[games.Count];
        for (var i = 0; i < games.Count; i++)
        {
            sortableGames[i] = new SortableGame(games[i], criteria);
        }

        Array.Sort(sortableGames, CompareSortableGames);

        var chunkPath = Path.Combine(tempDirectory, $"chunk_{chunkFiles.Count + 1:0000}.pgn");
        await using var outputStream = new FileStream(
            chunkPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

        var firstOutput = true;
        foreach (var sortableGame in sortableGames)
        {
            ct.ThrowIfCancellationRequested();

            if (!firstOutput)
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            await _pgnWriter.WriteGameAsync(writer, sortableGame.Game, ct).ConfigureAwait(false);
            firstOutput = false;
        }

        await writer.FlushAsync().ConfigureAwait(false);
        chunkFiles.Add(chunkPath);
    }

    private async Task MergeSortedChunksAsync(
        IReadOnlyList<string> chunkFiles,
        string tempOutputPath,
        IReadOnlyList<SortCriterion> criteria,
        long totalGames,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var lastProgressReport = DateTime.MinValue;
        var readers = new List<ChunkReader>(chunkFiles.Count);
        var queue = new PriorityQueue<MergeState, MergeState>(new MergeStateComparer());
        var sequence = 0L;

        try
        {
            for (var i = 0; i < chunkFiles.Count; i++)
            {
                var reader = new ChunkReader(i, chunkFiles[i], _pgnReader, ct);
                readers.Add(reader);

                if (await reader.Enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var state = new MergeState(reader, reader.Enumerator.Current, criteria, sequence++);
                    queue.Enqueue(state, state);
                }
            }

            await using var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize, leaveOpen: true);

            var firstOutput = true;
            var processed = 0L;

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var state = queue.Dequeue();

                if (!firstOutput)
                {
                    await writer.WriteLineAsync().ConfigureAwait(false);
                }

                await _pgnWriter.WriteGameAsync(writer, state.Game, ct).ConfigureAwait(false);
                firstOutput = false;
                processed++;

                if (await state.Reader.Enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var nextState = new MergeState(state.Reader, state.Reader.Enumerator.Current, criteria, sequence++);
                    queue.Enqueue(nextState, nextState);
                }

                if (ShouldReportProgress(processed, ref lastProgressReport))
                {
                    var percent = 60 + (processed / (double)totalGames * 40.0);
                    progress?.Report(percent);
                }
            }

            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            foreach (var reader in readers)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string GetHeaderValue(PgnGame game, SortCriterion criterion)
    {
        var key = criterion switch
        {
            SortCriterion.Event => "Event",
            SortCriterion.Site => "Site",
            SortCriterion.Date => "Date",
            SortCriterion.Round => "Round",
            SortCriterion.White => "White",
            SortCriterion.Black => "Black",
            SortCriterion.Eco => "ECO",
            _ => string.Empty
        };

        return game.Headers.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static int CompareNatural(string a, string b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        var ia = 0;
        var ib = 0;

        while (ia < a.Length || ib < b.Length)
        {
            if (ia >= a.Length)
            {
                return -1;
            }
            if (ib >= b.Length)
            {
                return 1;
            }

            var ca = a[ia];
            var cb = b[ib];

            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                var za = ia;
                while (za < a.Length && a[za] == '0') za++;
                var zb = ib;
                while (zb < b.Length && b[zb] == '0') zb++;

                var enda = za;
                while (enda < a.Length && char.IsDigit(a[enda])) enda++;
                var endb = zb;
                while (endb < b.Length && char.IsDigit(b[endb])) endb++;

                var lenA = enda - za;
                var lenB = endb - zb;

                if (lenA != lenB)
                {
                    return lenA.CompareTo(lenB);
                }

                for (var i = 0; i < lenA; i++)
                {
                    var diff = a[za + i].CompareTo(b[zb + i]);
                    if (diff != 0)
                    {
                        return diff;
                    }
                }

                var totalLenA = enda - ia;
                var totalLenB = endb - ib;
                if (totalLenA != totalLenB)
                {
                    return totalLenA.CompareTo(totalLenB);
                }

                ia = enda;
                ib = endb;
                continue;
            }

            var upperA = char.ToUpperInvariant(ca);
            var upperB = char.ToUpperInvariant(cb);
            if (upperA != upperB)
            {
                return upperA.CompareTo(upperB);
            }

            ia++;
            ib++;
        }

        return 0;
    }

    private static int CompareSortableGames(SortableGame a, SortableGame b)
    {
        for (var i = 0; i < a.SortKeys.Length; i++)
        {
            var diff = CompareNatural(a.SortKeys[i], b.SortKeys[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return 0;
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

    private sealed class ChunkReader : IAsyncDisposable
    {
        public ChunkReader(int index, string filePath, PgnReader reader, CancellationToken ct)
        {
            Index = index;
            Stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            Enumerator = reader.ReadGamesAsync(Stream, ct).GetAsyncEnumerator(ct);
        }

        public int Index { get; }
        public FileStream Stream { get; }
        public IAsyncEnumerator<PgnGame> Enumerator { get; }

        public async ValueTask DisposeAsync()
        {
            await Enumerator.DisposeAsync().ConfigureAwait(false);
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class MergeState
    {
        public MergeState(ChunkReader reader, PgnGame game, IReadOnlyList<SortCriterion> criteria, long sequence)
        {
            Reader = reader;
            Game = game;
            SortKeys = new string[criteria.Count];

            for (var i = 0; i < criteria.Count; i++)
            {
                SortKeys[i] = GetHeaderValue(game, criteria[i]);
            }

            Sequence = sequence;
        }

        public ChunkReader Reader { get; }
        public PgnGame Game { get; }
        public string[] SortKeys { get; }
        public long Sequence { get; }
    }

    private sealed class MergeStateComparer : IComparer<MergeState>
    {
        public int Compare(MergeState? x, MergeState? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var length = Math.Min(x.SortKeys.Length, y.SortKeys.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = CompareNatural(x.SortKeys[i], y.SortKeys[i]);
                if (diff != 0)
                {
                    return diff;
                }
            }

            if (x.SortKeys.Length != y.SortKeys.Length)
            {
                return x.SortKeys.Length.CompareTo(y.SortKeys.Length);
            }

            return x.Sequence.CompareTo(y.Sequence);
        }
    }
}
