# PgnFilterService.md

## Service Implementation: PgnFilterService (Filter Tool)

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Filter PGN games by Elo, ply count, checkmates, and standard‑variant rules, and optionally strip annotations.

## 2. Public API (Actual)

```csharp
public sealed record PgnFilterOptions(
    int? MinElo,
    int? MaxElo,
    bool RequireBothElos,
    bool OnlyCheckmates,
    bool RemoveComments,
    bool RemoveNags,
    bool RemoveVariations,
    bool RemoveNonStandard,
    int? MinPlyCount,
    int? MaxPlyCount);

public sealed record PgnFilterResult(long Processed, long Kept, long Modified);

public interface IPgnFilterService
{
    Task<PgnFilterResult> FilterAsync(
        string inputFilePath,
        string outputFilePath,
        PgnFilterOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Validate** input/output paths and option ranges.
2. **Stream games** from input.
3. **Apply filters** (variant, checkmate, Elo, ply count).
4. **Optionally strip** comments, NAGs, and variations.
5. **Write kept games** to temp output and replace destination.

## 4. Filter Rules (Actual)

- **Standard variant**:
  - Rejects `SetUp=1`, `FEN` present, or `Variant` not containing standard/classical/normal.
- **Checkmates**:
  - Detects `#` in mainline (ignores comments/variations).
- **Elo**:
  - If `RequireBothElos`, both ratings must be present and within range.
  - Otherwise, at least one rating must be present and within range.
- **Ply count**:
  - Counts SAN‑like tokens outside comments/variations.

## 5. Annotation Stripping

If enabled, the service removes:
- `{...}` comments
- `;` line comments
- NAGs like `$6`
- `(...)` variations

Whitespace is normalized to avoid repeated spaces.

## 6. Progress Reporting

Progress is reported by input stream position (percent), throttled by:
- Every 200 games, or
- At least every 100 ms.

## 7. Limitations

- Uses synchronous `ReplaceFile` at the end.
- Filter decisions are header‑ and text‑based; no legality validation.
