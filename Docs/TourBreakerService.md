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
<minDate>-<maxDate>-<event>-<site>-<year>-<keyhash>.pgn
```
where dates are `YYYYMMDD` (unknown or missing dates become `00000000`). Event/site are sanitized to lowercase hyphenated text and trimmed to safe lengths. A short key hash is appended to avoid collisions.

## 5. Progress Reporting

Reports percent based on games processed in pass 2 (denominator is total games from pass 1). Updates are throttled (every ~200 games or 100ms) to avoid UI flooding.

## 6. Limitations

- Tournament key uses `Event|Site|Year`.
- Only full dates (`yyyy.MM.dd`, `yyyy-MM-dd`, `yyyy/MM/dd`) are used for min/max; unknown/partial dates are ignored and yield `00000000` in filenames.
- Elo filter treats missing/invalid Elo as unknown (does not fail the threshold); only parsed Elos below `minElo` fail the tournament.
- Writes are direct to final output files (overwriting on first write per run); no temp‑file replacement per tournament, so cancelling mid‑run can leave partial outputs.
