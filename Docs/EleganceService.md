# EleganceService.md

## Service Implementation: EleganceService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `IChessAnalyzerService`, `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Compute and tag **Elegance** scores for each game using engine evaluations and statistical distributions.

## 2. Public API (Actual)

```csharp
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

1. **Analyze games** with `IChessAnalyzerService` into a temp analyzed PGN.
2. **Parse analyzed move text** and evaluation comments.
3. **Compute component scores** (Soundness, Coherence, Tactical, Quiet) and total Elegance.
4. **Write output** with `Elegance` and `EleganceDetails` headers.
5. **Replace** destination file and clean temp files.

## 4. Scoring Inputs

The scorer uses:
- `[%eval ...]` comments from the analyzer,
- NAGs and annotations,
- Material/forcing/quiet move heuristics,
- Distribution normalization from `Assets/elegance-distributions.json` (fallback defaults if missing).

## 5. Progress Reporting

Progress combines:
1. Analyzer progress (weighted 70%)
2. Scoring progress (remaining 30%, based on bytes read)

## 6. Output Tags

Each game receives:
- `Elegance` (0–100)
- `EleganceDetails` (component breakdown)

## 7. Limitations

- Requires a UCI engine and valid path.
- Depends on analyzer output (moves must be parseable and evaluated).
- Uses a temporary analyzed PGN file in the system temp folder.
