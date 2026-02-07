# ChessAnalyzerService.md

## Service Implementation: ChessAnalyzerService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, UCI engine executable, `Chess` (Gera.Chess)  
**Thread Safety:** Safe for concurrent calls with separate output paths and engine instances.

## 1. Objective

Analyze PGN games with a UCI engine and annotate each move with an evaluation and NAG (blunder/mistake/inaccuracy). Optionally compute and add Elegance tags.

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
```

## 3. High-Level Pipeline (Actual)

1. **Validate** input/output/engine paths and depth.
2. **Start UCI engine** and optionally set `SyzygyPath`.
3. **Stream PGN games**, analyze each game move‑by‑move:
   - Parse SAN and generate FEN positions.
   - Query engine at the requested depth for each ply.
   - Insert NAGs and `[%eval ...]` comments into move text.
4. **Write output** to a temp file and replace the destination file.
5. **On per‑game failure**, preserve original movetext and continue.

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

## 5. Elegance Tags (Optional)

If `addEleganceTags = true`, the analyzer computes and injects:
- `Elegance`
- `EleganceDetails`

These are derived from engine evaluations, swing metrics, forcing/quiet move ratios, and blunder/mistake counts.

## 6. Progress Reporting

Progress is reported as `AnalyzerProgress`:
- `ProcessedGames` increments per game.
- `Percent` is based on input stream position (bytes).

## 7. Error Handling

- If the engine process dies mid‑run, it is restarted and processing continues.
- If **all** games fail analysis, the method throws with the first failure message.
- On cancellation, the exception propagates and temp output is cleaned up.

## 8. Related Tool Integration

The **Chess Analyzer** UI also integrates the Stockfish downloader (`StockfishDownloaderService`)
to fetch the latest engine build and locate a suitable executable automatically.

## 9. Limitations

- Uses synchronous `ReplaceFile` at the end (blocking).
- Requires a valid UCI engine executable path.
- Move annotation depends on SAN parsing; malformed move text can skip annotations for that game.
