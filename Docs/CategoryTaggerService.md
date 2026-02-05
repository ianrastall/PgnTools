# CategoryTaggerService.md

## Service Implementation: CategoryTaggerService
**Version:** Current implementation (updated 2026-02-05)  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Not implemented  
**Thread Safety:** Safe for concurrent use when each call uses its own service instance.

## 1. Objective
Tag tournaments with a computed FIDE event category by analyzing game headers in a streaming, two-pass workflow.

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

### 2.1 Parameter Semantics
- `inputFilePath`: Source PGN file. Must exist.
- `outputFilePath`: Destination PGN file. Must be different from input.
- `progress`: Reports percent completion (0..100). Reported only when total games are known.
- `cancellationToken`: Cancels processing.

## 3. High-Level Pipeline (Actual)
1. **Validate inputs**: input/output paths, input exists, input != output.
2. **Pass 1: Analyze stats**:
   - Stream the PGN once to build per-event tournament stats (headers only) and total game count.
3. **Compute categories**:
   - For each event, compute its category (or skip if thresholds fail).
4. **Pass 2: Tag and write**:
   - Stream the PGN again, add `[EventCategory "<n>"]` where a category exists, and write to a temp output.
5. **Replace final output**: `FileReplacementHelper.ReplaceFile(temp, output)`.
6. **Cleanup**: On failure/cancel, temp output is deleted.

## 4. Category Calculation (Actual)
The category is computed per event from player ratings and game counts:

### 4.1 Inputs Collected per Tournament
Tournament identity uses a composite key:
`Event`, `Site`, `EventDate` (fallback `Date`), `Section`, `Stage` (case-insensitive).

- `GameCount`: total games for the tournament.
- `Players`: distinct player names from `White` and `Black`.
- `PlayerRatings`: highest observed Elo per player from `WhiteElo` / `BlackElo` (leading digits only).

### 4.2 Thresholds
Category is **not** assigned if any of the following are true:
- `GameCount < 6`
- `Players.Count < 2`
- Average games per player `< 0.6`, computed as `(2 * GameCount) / Players.Count`
- No valid ratings (`rating <= 0`)
- Average rating `< 2251`

### 4.3 Formula
If thresholds pass:

```
category = 1 + floor((avgRating - 2251) / 25)
```

## 5. Output Behavior (Actual)
- For games whose tournament key matches a categorized tournament, the header is updated:
  - `EventCategory = "<category>"`
- Existing `EventCategory` values are overwritten.
- Games with missing `Event` or events without computed categories are unchanged.

## 6. Progress Reporting (Actual)
- `TotalGames` is derived from the first pass.
- Percent is reported as `processed / total * 100` during pass 2.
- If there are no games (`total = 0`), progress is only reported as `100` at completion.

## 7. Error Handling & Validation (Actual)
| Scenario | Behavior |
| --- | --- |
| Input file missing | Throws `FileNotFoundException` before work begins |
| Input == output | Throws `InvalidOperationException` |
| Cancellation | Throws `OperationCanceledException`; temp output is deleted |
| I/O failure | Temp output is deleted; exception is rethrown |

## 8. Performance Characteristics
- **Time Complexity:** O(N) for stats + O(N) for tagging, where N is game count.
- **Memory Footprint:** O(E + P) for events and unique players per event.

## 9. Limitations (Current Implementation)
- Uses **only** event-level averages from `Event`, `WhiteElo`, and `BlackElo`.
- Uses the **highest observed** rating per player for the event.
- Does not integrate with binary index fields.
- Does not support custom rule sets or multi-label categories.
