# TourBreakerService.md

## Service Implementation: TourBreakerService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`  
**Thread Safety:** Safe for concurrent calls with separate output directories.

## 1. Objective

Split a PGN database into tournament files based on event/site/year with Elo and game count thresholds.

## 2. Public API (Actual)

```csharp
public interface ITourBreakerService
{
    Task<int> BreakTournamentsAsync(
        string inputFilePath,
        string outputDirectory,
        int minElo,
        int minGames,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Pass 1:** Scan all games and build tournament metadata.
2. **Filter tournaments** by:
   - All players meet `minElo`
   - `Games >= minGames`
   - `Games >= Players/2`
3. **Pass 2:** Write games into per‑tournament files.

## 4. Output Naming

Files are named:
```
<minDate>-<maxDate>-<event-name>.pgn
```
where dates are `YYYYMMDD` (unknown digits replaced with `0`), and event name is sanitized to lowercase hyphenated text.

## 5. Progress Reporting

Reports percent based on games processed in pass 2.

## 6. Limitations

- Tournament key uses `Event|Site|Year`.
- Writes are append‑based (no temp file per tournament).
