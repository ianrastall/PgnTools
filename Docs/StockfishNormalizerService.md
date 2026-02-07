# StockfishNormalizerService.md

## Service Implementation: StockfishNormalizerService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Normalize Stockfish engine names in player headers (`White`, `Black`) to canonical versions based on build date.

## 2. Public API (Actual)

```csharp
public sealed record StockfishNormalizeResult(long Processed, long TagsUpdated);

public interface IStockfishNormalizerService
{
    Task<StockfishNormalizeResult> NormalizeAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. Stream games from input.
2. For each `White`/`Black` header containing “Stockfish”, extract a build date (YYYYMMDD or YYMMDD).
3. Map date to a canonical Stockfish version name using a fixed date range table.
4. Write output to a temp file and replace destination.

## 4. Progress Reporting

Reports every ~200 games with count of tags updated.

## 5. Limitations

- Only normalizes `White` and `Black` headers.
- Date mapping table is static in code.
- Uses synchronous `ReplaceFile` at the end.
