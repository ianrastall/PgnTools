# CheckmateFilterService.md

## Service Implementation: CheckmateFilterService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Filter a PGN file to keep only games that contain a checkmate marker (`#`) in the mainline move text (ignores comments and variations).

## 2. Public API (Actual)

```csharp
public sealed record CheckmateFilterResult(long Processed, long Kept);

public interface ICheckmateFilterService
{
    Task<CheckmateFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Validate paths** and create a temp output file.
2. Stream games from input with `PgnReader`.
3. Keep a game only if `ContainsCheckmate(moveText)` is true.
4. Write kept games to temp output (UTF‑8, no BOM).
5. Replace destination file via `FileReplacementHelper.ReplaceFile`.

## 4. Checkmate Detection Rules

`ContainsCheckmate` scans the move text and:
- Ignores `{}` comments and `;` line comments.
- Ignores text inside `(...)` variations.
- Treats any `#` outside those regions as checkmate.

## 5. Progress Reporting

Progress is reported by **input byte position** (percent of input stream length), throttled by:
- Every 200 games, and
- At least every 100 ms.

## 6. Output Behavior

- If zero games are processed, no output file is produced.
- Games are separated by a single blank line.

## 7. Limitations

- Only detects `#` in mainline; does not parse SAN to verify legal checkmate.
- Uses synchronous `ReplaceFile` at the end (blocking).
