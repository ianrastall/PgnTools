# RemoveDoublesService.md

## Service Implementation: RemoveDoublesService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Deduplicate games by hashing normalized headers and move text.

## 2. Public API (Actual)

```csharp
public sealed record RemoveDoublesResult(long Processed, long Kept, long Removed);

public interface IRemoveDoublesService
{
    Task<RemoveDoublesResult> DeduplicateAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<(long games, string message)>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. Stream games from input.
2. Compute a SHA‑256 hash:
   - Headers are case‑insensitively sorted.
   - Move text is normalized (comments/variations/NAGs removed).
3. Write only the first occurrence of each hash.
4. Replace output file.

## 4. Progress Reporting

Reports every ~200 games with kept/removed counts.

## 5. Limitations

- Hashing is text‑based; no semantic normalization beyond token filtering.
- Uses synchronous `ReplaceFile` at the end.
