# ChessAnalyzerService.md

## Service Implementation: ChessAnalyzerService
**Version:** Current implementation (updated 2026-02-05)  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Not implemented in current code  
**Thread Safety:** Not safe for concurrent `AnalyzePgnAsync` calls on the same instance (create separate instances for parallel runs)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`, `Gera.Chess` (`ChessBoard`), local UCI engine binaries (Stockfish, etc.)

## 1. Objective
Analyze PGN games with a local UCI engine, annotate each mainline move with an evaluation comment and (optionally) add Elegance tags in headers. The implementation is streaming (no full-file load) and processes the input file in a single pass.

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

### 2.1 Parameter Semantics
- `inputFilePath`: Source PGN file. Must exist.
- `outputFilePath`: Destination PGN file. Must be different from input.
- `enginePath`: Path to a UCI engine executable (Stockfish, etc.). Must exist.
- `depth`: Fixed depth for all positions. Must be > 0.
- `tablebasePath`: If provided, passed as `SyzygyPath` UCI option at engine start.
- `progress`: Reports per-game progress only (game count + percent).
- `addEleganceTags`: When true, adds `Elegance` and `EleganceDetails` headers based on computed metrics.

## 3. High-Level Pipeline (Actual)

1. **Validate inputs**: input/output/engine paths, depth, input != output.
2. **Create temp output path**: `FileReplacementHelper.CreateTempFilePath(outputFullPath)`.
3. **Analyze and write**:
   - Start `UciEngine` once.
   - For each game:
     - `ucinewgame`, then analyze all mainline moves.
     - Write updated game (or original if analysis fails) to temp output.
4. **Replace final output**: `FileReplacementHelper.ReplaceFile(temp, output)`.
5. **Cleanup**: On failure/cancel, temp output is deleted.

## 4. Output Behavior (Actual)

### 4.1 PGN Header Updates
For each successfully analyzed game:
- `Annotator` = `PgnTools`
- `AnalysisDepth` = `{depth}` (string)
- If `addEleganceTags`:
  - `Elegance` = numeric score
  - `EleganceDetails` = formatted string from `EleganceScoreCalculator.FormatDetails(...)`

### 4.2 Move Text Annotation Style
Moves are rewritten to include:
- NAG codes based on evaluation delta:
  - `$4` for blunder (`<= -300 cp`)
  - `$2` for mistake (`<= -150 cp`)
  - `$6` for inaccuracy (`<= -60 cp`)
- A trailing comment with evaluation:

```
<original SAN> $2 { [%eval -1.35] }
```

### 4.3 Mainline Only
Moves inside variations are preserved verbatim but **not analyzed**. Only tokens at variation depth 0 are analyzed and annotated.

## 5. Under-the-Hood: Move Text Tokenization
The analyzer uses `TokenizeMoveTextPreserving(...)` to split the original movetext into tokens while preserving formatting. Key behaviors:

- **Comments**
  - `{ ... }` blocks are preserved as-is.
  - `;` line comments are preserved as-is.
- **Whitespace** is preserved as its own tokens.
- **Variations**
  - `(` increments `variationDepth`, `)` decrements it.
  - Tokens inside variations are *not* considered moves.
- **Result tokens** (`1-0`, `0-1`, `1/2-1/2`, `*`) are preserved; if missing, the header `Result` is appended at the end.
- **Inline NAGs** like `$1` are stripped only for analysis parsing, not necessarily removed from output.

Only tokens that pass `TryParseMoveToken` at variation depth 0 become analyzed moves.

## 6. Under-the-Hood: Move Parsing & Resolution
Each move token is analyzed in this order:
1. **Direct SAN**: `board.IsValidMove(token)`.
2. **Sanitized SAN**:
   - Normalize castling: `0-0` -> `O-O`, `0-0-0` -> `O-O-O`.
   - Strip trailing annotation `!`/`?`.
   - Strip trailing `+`/`#` for analysis resolution.
   - Remove inline `e.p.` / `ep` suffixes.
3. **Coordinate notation** (e.g., `e2e4`, `Nb1c3`, `e7e8Q`):
   - Regex: `^(?<piece>[KQRBNP])?(?<from>[a-h][1-8])(?:[-x:]?)(?<to>[a-h][1-8])(?:=?(?<promo>[QRBN]))?$`
   - Matches against all legal moves to resolve SAN.

If a move cannot be resolved or applied, analysis for that game fails and the original movetext is preserved.

## 7. Under-the-Hood: Evaluation Algorithm

### 7.1 Per-Game Flow
- Start from `FEN` header if present; otherwise standard initial position.
- Analyze **initial position** once (`scoreBefore`).
- For each move:
  - Apply the move to the board (SAN).
  - Analyze the resulting position (`scoreAfter`).
  - Compute **delta** for the mover:
    ```csharp
    deltaCp = (-scoreAfter.Centipawns) - scoreBefore.Centipawns;
    ```
  - Assign NAG based on delta threshold.
  - Convert evaluation to **white perspective** for reporting.
  - Emit annotation `{ [%eval ...] }`.
  - Set `scoreBefore = scoreAfter` for next ply.

### 7.2 Evaluation Formatting
- Mate scores are rendered as `#N` or `#-N` (and `#0` if mate-in-0).
- Centipawn scores are rendered as pawn units with two decimals (InvariantCulture), e.g. `0.45`.

## 8. Under-the-Hood: UCI Engine Wrapper (`UciEngine`)

### 8.1 Process Startup
- Launches `enginePath` with redirected stdin/stdout/stderr, no shell, no window.
- Sends `uci`, waits for `uciok` (timeout: 10 seconds).
- Sends `isready`, waits for `readyok` (timeout: 10 seconds).
- If `tablebasePath` provided, sends:
  ```
  setoption name SyzygyPath value <path>
  ```
  followed by `isready` / `readyok`.

### 8.2 Per-Game Engine Reset
For each game:
- Sends `ucinewgame`. A `readyok` handshake is only used after engine start to reduce per-game overhead.

### 8.3 Per-Position Search
- Sends:
  ```
  position fen <fen>
  go depth <depth>
  ```
- Reads `info` lines until `bestmove` or timeout.
- Keeps the deepest score seen (`depth` parsed from UCI output).
- Timeouts are depth-based: `ceil(depth * 2.5)` seconds, clamped to `[15, 120]`.
- If timeout occurs, sends `stop`, waits up to 500ms for `bestmove`, then returns the best score seen so far.

### 8.4 Score Parsing
- `score cp <value>` => centipawn score.
- `score mate <value>` => mate score; also converted to a large CP equivalent:
  ```csharp
  cpEquivalent = sign * (100_000 - distance * 100)
  ```
  where `distance = min(999, abs(mateIn))`.

### 8.5 Cancellation and Abort
- A `CancellationToken` registers `engine.RequestAbort()`.
- `RequestAbort` attempts to send `stop` and then kills the process tree if needed.

### 8.6 Disposal
- Attempts graceful shutdown (`quit`), waits 2 seconds, then hard-kills if necessary.

## 9. Elegance Tagging (Optional)
When `addEleganceTags == true`, the analyzer computes a set of metrics and writes headers:
- `Elegance`: final score
- `EleganceDetails`: formatted breakdown

### 9.1 Metrics Collected
- **PlyCount / EvaluatedPlyCount**: all analyzed plies.
- **ForcingMoveCount**: captures, checks, mates, promotions.
- **QuietImprovementCount**: quiet moves with `deltaCp >= +150`.
- **Blunder/Mistake/Dubious counts**: based on delta thresholds.
- **Swing metrics**: average absolute eval swing.
- **Trend breaks**: detects sign changes in eval delta (>= 50 cp shift).
- **Sacrifice metrics**:
  - Material values in centipawns: P=100, N=320, B=330, R=500, Q=900.
  - A sacrifice is “sound” if evaluation does not drop past thresholds:
    - Material loss >= 500: max drop -140, absolute floor -220
    - Material loss >= 300: max drop -120, absolute floor -180
    - Material loss < 300: max drop -90, absolute floor -140

The final score is computed by `EleganceScoreCalculator.Calculate(...)` and formatted by `FormatDetails(...)`.

## 10. Error Handling & Recovery (Actual)

| Scenario | Behavior |
| --- | --- |
| Input file missing | Throws `FileNotFoundException` before work begins. |
| Engine missing | Throws `FileNotFoundException` before work begins. |
| Input == output | Throws `InvalidOperationException`. |
| Per-game analysis failure | Preserves original movetext; continues to next game. |
| Engine exits during analysis | Disposes and restarts engine, continues. |
| All games fail | Throws `InvalidOperationException` with the first failure reason. |
| Cancellation | Aborts engine, deletes temp output, propagates `OperationCanceledException`. |

## 11. Progress Reporting (Actual)
- `AnalyzerProgress` is reported **per processed game**.
- `TotalGames` is `0` when unknown.
- `Percent` is estimated from input bytes processed (clamped to 0..100).

## 12. File I/O Details
- Output is written to a temp file: `.{filename}.{guid}.tmp` (same directory if possible).
- Output uses `StreamWriter` with UTF-8 **without BOM**.
- Output stream uses `FileOptions.SequentialScan | FileOptions.Asynchronous`.
- Final output replacement uses `File.Replace` or `File.Move` via `FileReplacementHelper`.

## 13. Important Limitations (Current Implementation)
- No binary index integration or flags are written.
- Depth-only search. No `nodes`, `time`, or `multiPV` options.
- No filtering by game index, tags, or scope. **All moves of all games** are analyzed.
- No skipping of existing engine annotations.
- Variations are preserved but never analyzed.
- Evaluation comments use standardized `[%eval ...]` tags.
- No configurable engine options beyond optional `SyzygyPath`.
- No parallel analysis. Single engine, sequential games.
- No explicit Lc0 network/weights support (must be a UCI engine that accepts `go depth`).

## 14. References in Code
- Service: `PgnTools/Services/ChessAnalyzerService.cs`
- ViewModel: `PgnTools/ViewModels/Tools/ChessAnalyzerViewModel.cs`
- Helpers: `PgnTools/Helpers/FileReplacementHelper.cs`, `PgnTools/Helpers/PgnHeaderExtensions.cs`
