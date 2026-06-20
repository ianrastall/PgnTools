using PgnTools.Services;

var runner = new SmokeRunner();
return await runner.RunAsync();

internal sealed class SmokeRunner
{
    private readonly List<(string Name, Func<Task> Test)> _tests = [];
    private readonly PgnReader _reader = new();
    private readonly PgnWriter _writer = new();
    private string _root = string.Empty;

    public SmokeRunner()
    {
        _tests.Add(("PGN Info", TestPgnInfoAsync));
        _tests.Add(("Checkmate Filter", TestCheckmateFilterAsync));
        _tests.Add(("General Filter", TestGeneralFilterAsync));
        _tests.Add(("Sorter", TestSorterAsync));
        _tests.Add(("Splitter", TestSplitterAsync));
        _tests.Add(("Joiner", TestJoinerAsync));
        _tests.Add(("Deduplicator", TestDeduplicatorAsync));
        _tests.Add(("Tour Breaker", TestTourBreakerAsync));
        _tests.Add(("ECO Tagger", TestEcoTaggerAsync));
        _tests.Add(("Elo Adder", TestEloAdderAsync));
        _tests.Add(("Category Tagger", TestCategoryTaggerAsync));
        _tests.Add(("Ply Count Adder", TestPlyCountAdderAsync));
        _tests.Add(("Stockfish Normalizer", TestStockfishNormalizerAsync));
        _tests.Add(("Chess Unannotator", TestChessUnannotatorAsync));
        _tests.Add(("Chess Analyzer", TestChessAnalyzerAsync));
        _tests.Add(("Elegance", TestEleganceAsync));
    }

    public async Task<int> RunAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "PgnToolsSmoke", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(_root);
        Console.WriteLine($"Smoke root: {_root}");

        var passed = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var (name, test) in _tests)
        {
            try
            {
                await test();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (SkipSmokeTestException ex)
            {
                skipped++;
                Console.WriteLine($"SKIP {name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Result: {passed} passed, {failed} failed, {skipped} skipped");
        return failed == 0 ? 0 : 1;
    }

    private async Task TestPgnInfoAsync()
    {
        var input = await WriteSamplePgnAsync("info.pgn");
        var stats = await new PgnInfoService(_reader).AnalyzeFileAsync(input);

        AssertEqual(2L, stats.Games, "PGN Info should count both games.");
        AssertEqual(4, stats.PlayerCount, "PGN Info should count unique players.");
        Assert(stats.AveragePlies.HasValue && stats.AveragePlies.Value > 0, "PGN Info should count move text.");
    }

    private async Task TestCheckmateFilterAsync()
    {
        var input = await WriteSamplePgnAsync("checkmate-input.pgn");
        var output = PathFor("checkmate-output.pgn");
        var result = await new CheckmateFilterService(_reader, _writer).FilterAsync(input, output);
        var text = await File.ReadAllTextAsync(output);

        AssertEqual(2L, result.Processed, "Checkmate filter should process both games.");
        AssertEqual(1L, result.Kept, "Checkmate filter should keep one game.");
        AssertContains(text, "Qxf7#", "Checkmate output should contain the mating game.");
        AssertDoesNotContain(text, "Beta Open", "Checkmate output should drop the non-mating game.");
    }

    private async Task TestGeneralFilterAsync()
    {
        var input = await WriteAnnotatedPgnAsync("filter-input.pgn");
        var output = PathFor("filter-output.pgn");
        var service = new PgnFilterService(_reader, _writer);
        var result = await service.FilterAsync(
            input,
            output,
            new PgnFilterOptions(
                MinElo: 2400,
                MaxElo: null,
                RequireBothElos: true,
                OnlyCheckmates: true,
                RemoveComments: true,
                RemoveNags: true,
                RemoveVariations: true,
                RemoveNonStandard: true,
                MinPlyCount: null,
                MaxPlyCount: null));
        var text = await File.ReadAllTextAsync(output);

        AssertEqual(2L, result.Processed, "Filter should process both games.");
        AssertEqual(1L, result.Kept, "Filter should keep one game.");
        AssertDoesNotContain(text, "{", "Filter should remove comments.");
        AssertDoesNotContain(text, "(", "Filter should remove variations.");
        AssertDoesNotContain(text, "$", "Filter should remove NAGs.");
    }

    private async Task TestSorterAsync()
    {
        var input = await WriteSamplePgnAsync("sort-input.pgn", betaFirst: true);
        var output = PathFor("sort-output.pgn");
        await new PgnSorterService(_reader, _writer).SortPgnAsync(input, output, [SortCriterion.White]);
        var text = await File.ReadAllTextAsync(output);

        Assert(text.IndexOf("[White \"Alice\"]", StringComparison.Ordinal) <
               text.IndexOf("[White \"Carol\"]", StringComparison.Ordinal),
            "Sorter should place Alice before Carol by White.");
    }

    private async Task TestSplitterAsync()
    {
        var input = await WriteSamplePgnAsync("split-input.pgn");
        var outputDir = DirFor("split-output");
        var result = await new PgnSplitterService(_reader, _writer)
            .SplitAsync(input, outputDir, PgnSplitStrategy.Event);

        AssertEqual(2L, result.Games, "Splitter should process both games.");
        AssertEqual(2, result.FilesCreated, "Splitter should create one file per event.");
        Assert(File.Exists(Path.Combine(outputDir, "Alpha Open.pgn")), "Splitter should create Alpha event file.");
        Assert(File.Exists(Path.Combine(outputDir, "Beta Open.pgn")), "Splitter should create Beta event file.");
    }

    private async Task TestJoinerAsync()
    {
        var input = await WriteSamplePgnAsync("join-source.pgn");
        var splitDir = DirFor("join-split");
        await new PgnSplitterService(_reader, _writer).SplitAsync(input, splitDir, PgnSplitStrategy.Event);

        var output = PathFor("joined.pgn");
        await new PgnJoinerService().JoinFilesAsync(Directory.EnumerateFiles(splitDir, "*.pgn"), output);
        var text = await File.ReadAllTextAsync(output);

        AssertContains(text, "Alpha Open", "Joiner should include first source.");
        AssertContains(text, "Beta Open", "Joiner should include second source.");
    }

    private async Task TestDeduplicatorAsync()
    {
        var input = PathFor("duplicates.pgn");
        await File.WriteAllTextAsync(input, $"{AlphaGame()}{Environment.NewLine}{Environment.NewLine}{AlphaGame()}");
        var output = PathFor("deduped.pgn");
        var result = await new RemoveDoublesService(_reader, _writer).DeduplicateAsync(input, output);

        AssertEqual(2L, result.Processed, "Deduplicator should process both games.");
        AssertEqual(1L, result.Kept, "Deduplicator should keep one unique game.");
        AssertEqual(1L, result.Removed, "Deduplicator should remove one duplicate.");
    }

    private async Task TestTourBreakerAsync()
    {
        var input = await WriteTournamentPgnAsync("tour-input.pgn");
        var outputDir = DirFor("tour-output");
        var created = await new TourBreakerService(_reader, _writer)
            .BreakTournamentsAsync(input, outputDir, minElo: 2400, minGames: 3);

        AssertEqual(1, created, "Tour breaker should create one tournament output.");
        AssertEqual(1, Directory.EnumerateFiles(outputDir, "*.pgn").Count(), "Tour breaker should leave one PGN file.");
    }

    private async Task TestEcoTaggerAsync()
    {
        var input = await WriteEcoTargetPgnAsync("eco-target.pgn");
        var eco = await WriteEcoReferencePgnAsync("mini-eco.pgn");
        var output = PathFor("eco-output.pgn");
        await new EcoTaggerService(_reader, _writer).TagEcoAsync(input, output, eco);
        var text = await File.ReadAllTextAsync(output);

        AssertContains(text, "[ECO \"C20\"]", "ECO tagger should add ECO.");
        AssertContains(text, "[Opening \"King's Pawn Game\"]", "ECO tagger should add opening.");
    }

    private async Task TestEloAdderAsync()
    {
        var input = await WriteNoEloPgnAsync("elo-input.pgn");
        var output = PathFor("elo-output.pgn");
        var ratings = new StubRatingDatabase(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alice"] = 2525,
            ["Bob"] = 2475
        });

        await new EloAdderService(_reader, _writer).AddElosAsync(input, output, ratings);
        var text = await File.ReadAllTextAsync(output);

        AssertContains(text, "[WhiteElo \"2525\"]", "Elo adder should fill WhiteElo.");
        AssertContains(text, "[BlackElo \"2475\"]", "Elo adder should fill BlackElo.");
    }

    private async Task TestCategoryTaggerAsync()
    {
        var input = await WriteTournamentPgnAsync("category-input.pgn");
        var output = PathFor("category-output.pgn");
        await new CategoryTaggerService(_reader, _writer).TagCategoriesAsync(input, output);
        var text = await File.ReadAllTextAsync(output);

        AssertContains(text, "[EventCategory \"", "Category tagger should add EventCategory.");
    }

    private async Task TestPlyCountAdderAsync()
    {
        var input = await WriteSamplePgnAsync("ply-input.pgn");
        var output = PathFor("ply-output.pgn");
        await new PlycountAdderService().AddPlyCountAsync(input, output, new Progress<double>(), CancellationToken.None);
        var text = await File.ReadAllTextAsync(output);

        AssertContains(text, "[PlyCount \"", "Ply count adder should write PlyCount tags.");
    }

    private async Task TestStockfishNormalizerAsync()
    {
        var input = PathFor("stockfish-normalizer-input.pgn");
        await File.WriteAllTextAsync(input,
            """
            [Event "Engine Match"]
            [Site "Local"]
            [Date "2024.09.01"]
            [White "Stockfish dev 240901"]
            [Black "Opponent"]
            [Result "1-0"]

            1. e4 e5 1-0
            """);
        var output = PathFor("stockfish-normalizer-output.pgn");
        await new StockfishNormalizerService(_reader, _writer).NormalizeAsync(input, output);
        var text = await File.ReadAllTextAsync(output);

        AssertContains(text, "[White \"Stockfish 16.1\"]", "Stockfish normalizer should map build date to release.");
    }

    private async Task TestChessUnannotatorAsync()
    {
        var input = await WriteAnnotatedPgnAsync("unannotator-input.pgn");
        var output = PathFor("unannotator-output.pgn");
        await new ChessUnannotatorService(_reader, _writer).UnannotateAsync(input, output);
        var text = await File.ReadAllTextAsync(output);

        AssertDoesNotContain(text, "{", "Unannotator should remove comments.");
        AssertDoesNotContain(text, "(", "Unannotator should remove variations.");
        AssertDoesNotContain(text, "$", "Unannotator should remove NAGs.");
    }

    private async Task TestChessAnalyzerAsync()
    {
        var engine = FindStockfishOrSkip();
        var input = await WriteOneShortGameAsync("analyzer-input.pgn");
        var output = PathFor("analyzer-output.pgn");
        await new ChessAnalyzerService(_reader, _writer)
            .AnalyzePgnAsync(input, output, engine, depth: 1, tablebasePath: null);
        var text = await File.ReadAllTextAsync(output);

        AssertContains(text, "[Annotator \"PgnTools\"]", "Analyzer should mark the annotator.");
        AssertContains(text, "[AnalysisDepth \"1\"]", "Analyzer should write the analysis depth.");
    }

    private async Task TestEleganceAsync()
    {
        var engine = FindStockfishOrSkip();
        var input = await WriteOneShortGameAsync("elegance-input.pgn");
        var output = PathFor("elegance-output.pgn");
        var analyzer = new ChessAnalyzerService(_reader, _writer);
        var result = await new EleganceService(analyzer, _reader, _writer)
            .TagEleganceAsync(input, output, engine, depth: 1);
        var text = await File.ReadAllTextAsync(output);

        Assert(result.ProcessedGames == 1, "Elegance should process one game.");
        AssertContains(text, "[Elegance \"", "Elegance should write the score tag.");
        AssertContains(text, "[EleganceDetails \"", "Elegance should write detail tags.");
    }

    private async Task<string> WriteSamplePgnAsync(string name, bool betaFirst = false)
    {
        var path = PathFor(name);
        var text = betaFirst
            ? $"{BetaGame()}{Environment.NewLine}{Environment.NewLine}{AlphaGame()}"
            : $"{AlphaGame()}{Environment.NewLine}{Environment.NewLine}{BetaGame()}";
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private async Task<string> WriteAnnotatedPgnAsync(string name)
    {
        var path = PathFor(name);
        await File.WriteAllTextAsync(path,
            """
            [Event "Annotated"]
            [Site "Local"]
            [Date "2024.01.02"]
            [White "Alice"]
            [Black "Bob"]
            [WhiteElo "2500"]
            [BlackElo "2450"]
            [Result "1-0"]

            1. e4 {comment} e5 (1... c5) 2. Qh5 $1 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0

            [Event "Quiet"]
            [Site "Local"]
            [Date "2024.01.03"]
            [White "Carol"]
            [Black "Dave"]
            [WhiteElo "2100"]
            [BlackElo "2080"]
            [Result "0-1"]

            1. d4 d5 2. c4 e6 0-1
            """);
        return path;
    }

    private async Task<string> WriteEcoTargetPgnAsync(string name)
    {
        var path = PathFor(name);
        await File.WriteAllTextAsync(path,
            """
            [Event "Needs ECO"]
            [Site "Local"]
            [Date "2024.01.02"]
            [White "Alice"]
            [Black "Bob"]
            [Result "1-0"]

            1. e4 e5 2. Nf3 Nc6 1-0
            """);
        return path;
    }

    private async Task<string> WriteEcoReferencePgnAsync(string name)
    {
        var path = PathFor(name);
        await File.WriteAllTextAsync(path,
            """
            [Event "ECO Reference"]
            [Site "?"]
            [Date "????.??.??"]
            [White "?"]
            [Black "?"]
            [Result "*"]
            [ECO "C20"]
            [Opening "King's Pawn Game"]

            1. e4 e5 *
            """);
        return path;
    }

    private async Task<string> WriteNoEloPgnAsync(string name)
    {
        var path = PathFor(name);
        await File.WriteAllTextAsync(path,
            """
            [Event "No Elo"]
            [Site "Local"]
            [Date "2024.01.02"]
            [White "Alice"]
            [Black "Bob"]
            [Result "1-0"]

            1. e4 e5 1-0
            """);
        return path;
    }

    private async Task<string> WriteTournamentPgnAsync(string name)
    {
        var path = PathFor(name);
        var games = Enumerable.Range(1, 6)
            .Select(round =>
                $$"""
                [Event "Category Masters"]
                [Site "Testville"]
                [Date "2024.02.0{{round}}"]
                [EventDate "2024.02.01"]
                [Round "{{round}}"]
                [White "{{(round % 2 == 0 ? "Alice" : "Bob")}}"]
                [Black "{{(round % 2 == 0 ? "Carol" : "Dave")}}"]
                [WhiteElo "2520"]
                [BlackElo "2480"]
                [Result "1-0"]

                1. e4 e5 2. Nf3 Nc6 1-0
                """);

        await File.WriteAllTextAsync(path, string.Join($"{Environment.NewLine}{Environment.NewLine}", games));
        return path;
    }

    private async Task<string> WriteOneShortGameAsync(string name)
    {
        var path = PathFor(name);
        await File.WriteAllTextAsync(path,
            """
            [Event "Short"]
            [Site "Local"]
            [Date "2024.01.02"]
            [White "Alice"]
            [Black "Bob"]
            [Result "1-0"]

            1. e4 e5 2. Nf3 Nc6 1-0
            """);
        return path;
    }

    private string AlphaGame() =>
        """
        [Event "Alpha Open"]
        [Site "London"]
        [Date "2024.01.02"]
        [Round "1"]
        [White "Alice"]
        [Black "Bob"]
        [WhiteElo "2500"]
        [BlackElo "2450"]
        [Result "1-0"]
        [ECO "C20"]

        1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0
        """;

    private string BetaGame() =>
        """
        [Event "Beta Open"]
        [Site "Paris"]
        [Date "2023.12.05"]
        [Round "1"]
        [White "Carol"]
        [Black "Dave"]
        [WhiteElo "2100"]
        [BlackElo "2080"]
        [Result "0-1"]
        [ECO "B01"]

        1. e4 d5 2. exd5 Qxd5 0-1
        """;

    private string FindStockfishOrSkip()
    {
        var environmentPath = Environment.GetEnvironmentVariable("PGNTOOLS_STOCKFISH");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return environmentPath;
        }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PgnTools", "Stockfish"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "stockfish")
        };

        var engine = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*stockfish*.exe", SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return engine ?? throw new SkipSmokeTestException("No Stockfish executable found. Set PGNTOOLS_STOCKFISH to run this test.");
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    private string DirFor(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
        }
    }

    private static void AssertContains(string text, string value, string message)
    {
        if (!text.Contains(value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertDoesNotContain(string text, string value, string message)
    {
        if (text.Contains(value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubRatingDatabase(Dictionary<string, int> ratings) : IRatingDatabase
    {
        public int? Lookup(string name, int year, int month) =>
            ratings.TryGetValue(name, out var rating) ? rating : null;
    }

    private sealed class SkipSmokeTestException(string message) : Exception(message);
}
