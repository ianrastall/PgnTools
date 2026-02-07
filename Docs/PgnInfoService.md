# PgnInfoService.md

## Service Implementation: PgnInfoService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`  
**Thread Safety:** Safe for concurrent calls.

## 1. Objective

Analyze a PGN file and produce summary statistics (games, players, Elo, ECO, dates, plies, results).

## 2. Public API (Actual)

```csharp
public interface IPgnInfoService
{
    Task<PgnStatistics> AnalyzeFileAsync(
        string filePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. Stream games with `PgnReader`.
2. Count plies using SAN‑like token detection (ignoring comments/variations).
3. Aggregate statistics into `PgnStatistics`.
4. Report progress every ~200 games.

## 4. Statistics Collected

- Game counts and result breakdown.
- Player count and tournament count.
- Elo stats (avg/min/max, games with Elo).
- Country count (based on federation headers).
- ECO counts (letters and top codes).
- Date range and missing dates.
- Ply stats (min/max/avg, games with moves).

## 5. Limitations

- Ply counting is heuristic (text‑based).
- Does not validate move legality.
