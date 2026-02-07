# PgnSorterService.md

## Service Implementation: PgnSorterService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Sort PGN games by header fields (Event, Site, Date, Round, White, Black, ECO) using an external merge sort for large files.

## 2. Public API (Actual)

```csharp
public enum SortCriterion { Event, Site, Date, Round, White, Black, Eco }

public interface IPgnSorterService
{
    Task SortPgnAsync(
        string inputFilePath,
        string outputFilePath,
        IReadOnlyList<SortCriterion> criteria,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Validate paths** and create a temp directory.
2. **Chunk sorting**:
   - Read up to 100,000 games into memory.
   - Compute sort keys and sort in‑memory.
   - Write each chunk to a temp file.
3. **Merge sorted chunks** using a priority queue.
4. **Replace output** via `FileReplacementHelper.ReplaceFile`.

If `criteria` is empty, the service copies input to output without sorting.

## 4. Progress Reporting

Progress is approximate:
- 0–60% during chunk creation (by input stream position)
- 60–100% during merge (by games processed)

## 5. Limitations

- Sorting is case‑insensitive but uses natural string comparison.
- Uses synchronous `ReplaceFile` at the end.
