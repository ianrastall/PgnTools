# ChessAnalyzerService.md

## Service Implementations: ChessAnalyzerService and EleganceService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, UCI engine executable, `Chess` (Gera.Chess), `EleganceScoreCalculator`, `FileReplacementHelper`, `StockfishDownloaderService`  
**Thread Safety:** Safe for concurrent calls with separate output paths and engine instances.

## 1. Objective

The Chess Analyzer tool has two closely related workflows:

- **ChessAnalyzerService**: analyze PGN games with a UCI engine, annotate each move with evaluations and NAGs, and optionally add Elegance tags.
- **EleganceService**: run the analyzer first, then compute Elegance component scores and add `Elegance` / `EleganceDetails` headers.

## 2. Public API (Actual)

```csharp
public interface IChessAnalyzerService
{
    Task AnalyzePgnAsync(
        string inputFilePath,
        string outputFilePath,
        string enginePath,
        int depth,
        string? tablebasePath,
        IProgress<AnalyzerProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool addEleganceTags = false);
}

public readonly record struct AnalyzerProgress(long ProcessedGames, long TotalGames, double Percent);

public interface IEleganceService
{
    Task<EleganceTaggerResult> TagEleganceAsync(
        string inputFilePath,
        string outputFilePath,
        string enginePath,
        int depth,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record EleganceTaggerResult(
    long ProcessedGames,
    double AverageScore,
    double AverageSoundness,
    double AverageCoherence,
    double AverageTactical,
    double AverageQuiet);
```

## 3. High-Level Pipeline (Actual)

### ChessAnalyzerService

1. **Validate** input/output/engine paths and depth.
2. **Start UCI engine** and optionally set `SyzygyPath`.
3. **Stream PGN games**, analyze each move:
   - Parse SAN and generate FEN positions.
   - Query engine at the requested depth for each ply.
   - Insert NAGs and `[%eval ...]` comments into move text.
4. **Write output** to a temp file and replace the destination file.
5. **On per-game failure**, preserve original movetext and continue.

### EleganceService (Standalone Tagger)

1. **Analyze games** with `IChessAnalyzerService` into a temp analyzed PGN.
2. **Parse analyzed move text** and evaluation comments.
3. **Compute component scores** (Soundness, Coherence, Tactical, Quiet) and total Elegance.
4. **Write output** with `Elegance` and `EleganceDetails` headers.
5. **Replace** destination file and clean temp files.

## 4. Annotation Rules

- Adds NAGs based on centipawn delta for the mover:
  - `<= -300` → `$4` (blunder)
  - `<= -150` → `$2` (mistake)
  - `<= -60` → `$6` (inaccuracy/dubious)
- Adds an evaluation comment after each move:
  - `{ [%eval <score>] }` where `<score>` is centipawns or `#<mate>` for mate.
- Adds headers:
  - `Annotator = "PgnTools"`
  - `AnalysisDepth = <depth>`

## 5. Elegance Tags

When enabled or when using the Elegance tool, games receive:

- `Elegance` (0–100)
- `EleganceDetails` (component breakdown)

The scorer uses:

- `[%eval ...]` comments from the analyzer,
- NAGs and annotations,
- Material/forcing/quiet move heuristics,
- Distribution normalization from `Assets/elegance-distributions.json` (fallback defaults if missing).

## 6. Progress Reporting

- **ChessAnalyzerService** reports `AnalyzerProgress`:
  - `ProcessedGames` increments per game.
  - `Percent` is based on input stream position (bytes).
- **EleganceService** combines progress:
  - Analyzer progress weighted 70%.
  - Scoring progress weighted 30% (based on bytes read from analyzed PGN).

## 7. Related Tool Integration

The Chess Analyzer UI integrates the **Stockfish downloader** (`StockfishDownloaderService`) to fetch a UCI engine and locate a suitable executable automatically.

## 8. Limitations

- Requires a valid UCI engine executable path.
- Uses synchronous `ReplaceFile` at the end of the analyzer and elegance pipelines.
- Move annotation depends on SAN parsing; malformed move text can skip annotations for that game.
- Elegance scoring depends on analyzer output and temp-file processing.
