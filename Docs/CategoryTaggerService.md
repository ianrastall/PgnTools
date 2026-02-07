# CategoryTaggerService.md

## Service Implementation: CategoryTaggerService

**Version:** Current implementation (updated 2026-02-07)  
**Layer:** Service Layer (Domain Logic)  
**Dependencies:** `PgnReader`, `PgnWriter`, `FileReplacementHelper`  
**Thread Safety:** Safe for concurrent calls with separate output paths.

## 1. Objective

Compute FIDE event categories per tournament and tag games with `EventCategory` based on average player Elo.

## 2. Public API (Actual)

```csharp
public interface ICategoryTaggerService
{
    Task TagCategoriesAsync(
        string inputFilePath,
        string outputFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
```

## 3. High-Level Pipeline (Actual)

1. **Validate paths** and create a temp output file.
2. **Pass 1 (stats):** stream games with `readMoveText: false` to build per‑tournament stats.
3. **Compute categories** for tournaments that meet thresholds.
4. **Pass 2 (rewrite):** stream games again, insert `EventCategory` when a tournament has a category, and write to temp.
5. **Replace output** via `FileReplacementHelper.ReplaceFileAsync`.

## 4. Tournament Key & Category Rules

### 4.1 Tournament Key
The key is built from headers:
- `Event` (required)
- `Site`
- `EventDate` (falls back to `Date`)
- `Section`
- `Stage`

If `Event` is missing or empty, the game is ignored for category tagging.

### 4.2 Category Calculation
Thresholds:
- Minimum games: **6**
- Minimum avg games per player: **0.6**
- Average Elo must be **>= 2251**

Average Elo uses **one best rating per player** (max of any seen rating for that player).  
Category formula:
```
category = 1 + floor((avgElo - 2251) / 25)
```

## 5. Progress Reporting

Progress is reported as percent of total games (from pass 1). Reports are throttled by:
- Every 500 games, or
- At least every 200 ms.

## 6. Output Behavior

- Adds `EventCategory` after the `Event` header if present.
- Preserves all existing headers and move text.
- Writes UTF‑8 (no BOM).

## 7. Limitations

- Requires `Event` header to identify a tournament.
- Category rules are fixed in code (no external config).
- Two full passes are required (stats + rewrite).
