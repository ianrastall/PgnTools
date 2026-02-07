# PgnSplitterService.md

## Service Implementation: PgnSplitterService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`  
**Thread Safety:** Safe for concurrent calls with separate output directories.

## 1. Objective

Split a PGN file into multiple outputs by chunk size or header‑based grouping.

## 2. Public API (Actual)

```csharp
public enum PgnSplitStrategy { Chunk, Event, Site, Eco, Date }
public enum PgnDatePrecision { Century, Decade, Year, Month, Day }

public interface IPgnSplitterService
{
    Task<PgnSplitResult> SplitAsync(
        string inputFilePath,
        string outputDirectory,
        PgnSplitStrategy strategy,
        int chunkSize = 1000,
        PgnDatePrecision datePrecision = PgnDatePrecision.Year,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

### Strategy: `Chunk`
Writes `games_chunk_0001.pgn`, `games_chunk_0002.pgn`, … with a fixed number of games.

### Strategy: `Event` / `Site` / `Eco`
Writes to `{HeaderValue}.pgn` using a bounded channel and an LRU writer cache
to limit open file handles.

### Strategy: `Date`
Uses `Date` header and `PgnDatePrecision` to build filenames:
- Century: `YYYYxx.pgn`
- Decade: `YYYx.pgn`
- Year: `YYYY.pgn`
- Month: `YYYY-MM.pgn`
- Day: `YYYY-MM-DD.pgn`

## 4. Progress Reporting

Progress is reported every ~200 games with `"Processing Game N..."`.

## 5. Limitations

- File names are sanitized (invalid characters replaced).
- No merge or post‑processing; outputs are written as‑is.
