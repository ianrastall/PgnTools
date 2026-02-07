# EloAdderService.md

## Service Implementation: EloAdderService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`, `IRatingDatabase`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Add missing `WhiteElo` and `BlackElo` tags by looking up historical ratings in a ratings database.

## 2. Public API (Actual)

```csharp
public interface IEloAdderService
{
    Task AddElosAsync(
        string inputFilePath,
        string outputFilePath,
        IRatingDatabase db,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

public interface IRatingDatabase
{
    int? Lookup(string name, int year, int month);
}
```

## 3. High-Level Pipeline (Actual)

1. **Validate paths** and ensure the input exists.
2. **Stream games** from input with `PgnReader`.
3. **Parse `Date`** (`YYYY.MM.DD`) and lookup ratings.
4. **Add Elo tags** only if missing.
5. **Write to temp** and replace output file.

## 4. Lookup Rules

- Date parsing uses **year + month** from the `Date` header.
- Ratings are only added when `WhiteElo`/`BlackElo` is absent.
- Player names are looked up verbatim from headers.

## 5. Progress Reporting

Progress is reported by input stream position (percent), throttled by:
- Every 200 games, or
- At least every 100 ms.

## 6. Limitations

- Requires a ratings database implementation.
- Games without a parsable `Date` are skipped.
