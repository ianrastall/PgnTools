# PgnFilterService.md

## Service Specification: PgnFilterService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (`GameRecord` shadow array traversal)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Extract a subset of games from a PGN database matching user-defined criteria without loading entire games into memory. All filtering occurs via header metadata inspection using binary index offsets; move text parsing occurs only when explicitly required by filter conditions.

## 2. Input Contract

```csharp
public record FilterRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    FilterCriteria Criteria,        // Filter rules object (see Section 3)
    string? OutputFilePath = null,  // If null, return matching GameRecord[] only
    bool PreserveIndex = true       // Generate .pbi for output file if specified
);

public record FilterCriteria(
    PlayerFilter? WhitePlayer = null,
    PlayerFilter? BlackPlayer = null,
    StringFilter? Event = null,
    StringFilter? Site = null,
    DateRangeFilter? DateRange = null,
    EloRangeFilter? WhiteElo = null,
    EloRangeFilter? BlackElo = null,
    ResultFilter? Result = null,
    EcoFilter? Eco = null,
    RoundFilter? Round = null,
    int? MinMoves = null,           // Requires move parsing (expensive)
    int? MaxMoves = null,           // Requires move parsing (expensive)
    bool? HasAnnotations = null,    // Requires move text scan for '{' or '('
    bool? IsCheckmate = null        // Requires final position evaluation
);
```

### 2.1 Filter Primitive Types

#### PlayerFilter
```csharp
public record PlayerFilter(
    string Pattern,                 // "Carlsen", "Car*", "*sen", "Car*sen"
    MatchMode Mode = MatchMode.Wildcard  // Wildcard | Exact | Contains | Regex
);
```
- **Wildcard rules:** `*` matches zero or more characters; `?` matches exactly one character
- **Normalization:** Case-insensitive comparison after Unicode normalization (FormC)
- **Edge case:** `"?"` alone matches any single-character name (rare but valid)

#### StringFilter
```csharp
public record StringFilter(
    string Value,
    StringComparison Comparison = StringComparison.OrdinalIgnoreCase
);
```

#### DateRangeFilter
```csharp
public record DateRangeFilter(
    DateOnly? StartDate = null,     // Inclusive
    DateOnly? EndDate = null        // Inclusive
);
```
- **Lenient parsing required:** Must handle partial dates:
  - `"2020.??.??"` → matches any date in 2020
  - `"2020.05.??"` → matches May 2020
  - `"????.??.??"` → treated as missing date (fails filter unless both bounds null)
- **Invalid dates:** `"2020.02.30"` → parsed as `DateOnly(2020, 2, 29)` (leap year adjustment) or rejected per validation policy

#### EloRangeFilter
```csharp
public record EloRangeFilter(
    int? Min = null,                // Inclusive
    int? Max = null                 // Inclusive
);
```
- **Missing ratings:** Games without `[WhiteElo ""]` tag automatically fail filter if Min/Max specified
- **Zero values:** `[WhiteElo "0"]` treated as missing rating (FIDE convention)

#### ResultFilter
```csharp
[Flags]
public enum GameResult
{
    Unknown = 0,
    WhiteWins = 1,
    BlackWins = 2,
    Draw = 4,
    All = WhiteWins | BlackWins | Draw
}
```

#### EcoFilter
```csharp
public record EcoFilter(
    string CodeOrRange,             // "B90", "B00-B99", "Sicilian"
    MatchMode Mode = MatchMode.Exact // Exact | Range | OpeningNameSubstring
);
```
- **Range syntax:** `"A00-C99"` includes all codes lexicographically between bounds
- **Opening name match:** Requires pre-loaded ECO database mapping codes → names

#### RoundFilter
```csharp
public record RoundFilter(
    int? ExactRound = null,
    int? MinRound = null,
    int? MaxRound = null
);
```
- **Multi-round values:** `[Round "1.1"]` (armageddon) → parsed as `1` for filtering purposes
- **Missing round:** Treated as round 0 (fails numeric filters)

## 3. Algorithm Specification

### 3.1 Two-Phase Execution Model
All filtering executes in two distinct phases to minimize I/O and parsing overhead:

#### Phase 1: Header-Only Index Scan (O(N) time, O(1) memory per game)
```csharp
List<uint> matchingOffsets = new(capacity: estimatedMatchCount);

// Load binary index header + map GameRecord array
using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
ReadOnlySpan<GameRecord> records = index.GetGameRecords();

foreach (ref readonly GameRecord record in records)
{
    // Fast-path rejection using pre-extracted index fields
    if (criteria.WhiteElo?.Min.HasValue == true && record.WhiteElo < criteria.WhiteElo.Min.Value)
        continue;
    if (criteria.WhiteElo?.Max.HasValue == true && record.WhiteElo > criteria.WhiteElo.Max.Value)
        continue;
    // ... repeat for BlackElo, Date (packed), Result enum
    
    // If all fast-path filters pass OR no fast-path filters exist:
    if (PassesHeaderFilters(record, criteria))
    {
        matchingOffsets.Add(record.FileOffset);
    }
}
```

#### Phase 2: Deep Inspection (Only for games passing Phase 1)
Required when filters need data not stored in `GameRecord`:
- `MinMoves` / `MaxMoves`: Parse move text to count plies
- `HasAnnotations`: Scan for `{` (comments) or `(` (variations)
- `IsCheckmate`: Play through final position to verify mate

```csharp
if (requiresDeepInspection)
{
    using var pgnFile = File.OpenRead(request.SourceFilePath);
    
    foreach (uint offset in matchingOffsets.ToArray()) // Copy to avoid mutation during iteration
    {
        // Seek to game start + read header block (first 1KB typically sufficient)
        pgnFile.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
        int bytesRead = pgnFile.Read(buffer.AsSpan(0, 1024));
        
        // Parse minimal header to extract missing fields
        var tags = PgnHeaderParser.Parse(buffer.AsSpan(0, bytesRead));
        
        if (!PassesRemainingFilters(tags, criteria))
        {
            matchingOffsets.Remove(offset); // Reject after deep inspection
        }
        
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
```

### 3.2 Filter Evaluation Precedence
Filters combine with **logical AND** semantics. Evaluation order optimized for cost:

| Priority | Filter Type | Cost | Reason |
|----------|-------------|------|--------|
| 1 | Pre-indexed fields (Elo, Date, Result) | O(1) | Already in GameRecord |
| 2 | Tag-based filters (Player, Event, Round) | O(K) | Requires header block scan (≤1KB) |
| 3 | Move-count filters | O(M) | Requires full move text parse |
| 4 | Positional filters (Checkmate) | O(M) + board eval | Requires playing through game |

### 3.3 Output Generation Strategies

#### Strategy A: Index-Only Result (OutputFilePath = null)
- Return `GameRecord[]` containing only matching records
- Preserve original file offsets (enables random access without rewrite)
- Caller responsible for UI binding or further processing

#### Strategy B: Physical File Extraction (OutputFilePath specified)
```csharp
using var source = File.OpenRead(request.SourceFilePath);
using var dest = File.Create(request.OutputFilePath);
BinaryWriter writer = new(dest);

foreach (uint offset in matchingOffsets)
{
    // Seek to game in source
    source.Seek(offset, SeekOrigin.Begin);
    
    // Read game length from index (avoid parsing to find boundary)
    uint length = index.GetGameLengthAtOffset(offset);
    
    // Buffer copy (64KB chunks to avoid LOH allocation)
    byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
    uint remaining = length;
    
    while (remaining > 0)
    {
        int toRead = (int)Math.Min(remaining, buffer.Length);
        int read = source.Read(buffer, 0, toRead);
        writer.Write(buffer, 0, read);
        remaining -= (uint)read;
    }
    
    // Ensure game separation (PGN standard requires blank line between games)
    writer.Write(new byte[] { 0x0A, 0x0A }); // \n\n
    
    ArrayPool<byte>.Shared.Return(buffer);
}

// Generate companion index if requested
if (request.PreserveIndex)
{
    PgnBinaryIndexBuilder.BuildFromOffsets(
        request.OutputFilePath,
        matchingOffsets.ToArray(),
        index
    );
}
```

## 4. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Source file missing `.pbi` companion | Throw `IndexNotFoundException`; do not auto-generate (caller must explicitly index first) |
| Corrupted index (game count mismatch) | Validate during open: `if (index.GameCount != actualGameCount) throw IndexCorruptedException` |
| Filter with contradictory bounds (`MinElo=2800, MaxElo=2000`) | Reject at validation layer with `ArgumentException` before scan begins |
| Unicode player names (e.g., "Nakamura, Hikaru") | Normalize to FormC before wildcard matching; preserve original in output |
| Games with malformed `[Date "2020"]` (missing month/day) | Parse as `DateOnly(2020, 1, 1)` for filtering; log warning but do not reject game |
| Filter requesting move count on 10GB file | Warn caller via callback: "Move count filter requires full parse - estimated 45min runtime" |
| Zero games match criteria | Return empty result set (not error); generate valid empty PGN file with standard headers if output requested |

## 5. Performance Characteristics

### 5.1 Time Complexity
| Operation | Complexity | Notes |
|-----------|------------|-------|
| Header-only filter | O(N) | N = total games; single sequential index scan |
| Tag-value filter | O(N × K) | K = avg header size (typically ≤512 bytes) |
| Move-count filter | O(N × M) | M = avg moves per game; requires full parse |
| Output file generation | O(M) | M = matching games; sequential I/O optimal |

### 5.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| Filter 1M games (header-only) | < 1 MB | Stream offsets only; no game data loaded |
| Filter + output 100K matches | ~800 KB | Store offsets (4 bytes × 100K) + I/O buffers |
| Deep inspection (move count) | < 64 KB | Reuse single game buffer via ArrayPool |

### 5.3 I/O Patterns
- **Read pattern:** Sequential scan of `.pbi` (index) + random access to `.pgn` for matched games
- **Write pattern:** Sequential append to output file (optimal for SSD/HDD)
- **Cache efficiency:** Index scan fits in L3 cache for files ≤500K games (16MB index)

## 6. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `FileNotFoundException` | Source `.pgn` or `.pbi` missing | Caller must validate paths before invocation |
| `IndexVersionException` | `.pbi` format version unsupported | Require index rebuild with current version |
| `IOException` | Disk full during output write | Partial file left on disk; caller responsible for cleanup |
| `OperationCanceledException` | `CancellationToken` triggered | Return partial results + cancellation flag; index remains consistent |

## 7. Binary Index Integration Points

### 7.1 Required GameRecord Fields
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    public uint FileOffset;      // 0-3:   Absolute offset to game start
    public uint Length;          // 4-7:   Total bytes including headers + moves
    public uint TagBlockLength;  // 8-11:  Bytes from start to first move token
    public uint FirstMoveOffset; // 12-15: Offset within game to first move
    public uint PackedDate;      // 16-19: YYYYMMDD (0 = unknown)
    public ushort WhiteElo;      // 20-21: 0 = unknown
    public ushort BlackElo;      // 22-23: 0 = unknown
    public byte Result;          // 24:    0=*, 1=1-0, 2=0-1, 3=1/2
    public byte EcoCodeIndex;    // 25:    Index into ECO string heap (0 = unknown)
    public ushort Reserved;      // 26-27: Future expansion
    public uint StringHeapOffset; // 28-31: Offset to null-terminated player names in heap
}
```

### 7.2 Index-Aware Optimizations
- **Date filtering:** Compare `PackedDate` directly as integer (`20200101` ≤ `date` ≤ `20201231`)
- **Elo filtering:** Compare `WhiteElo`/`BlackElo` ushort values without parsing
- **Result filtering:** Bitmask check against `Result` enum value
- **Player name filtering:** Requires string heap lookup (offset stored in `StringHeapOffset`)

## 8. Testability Requirements

### 8.1 Required Test Fixtures
- `pgn_10games_mixed.pgn` + valid `.pbi` (small, hand-verified)
- `pgn_100k_games.pgn` + `.pbi` (performance baseline)
- `pgn_malformed_headers.pgn` (edge case validation)
- `pgn_unicode_players.pgn` (UTF-8 normalization tests)

### 8.2 Assertion Examples
```csharp
// Filter: Carlsen as White, 2020-2022
var results = service.Filter(new FilterRequest(
    "mega.pgn",
    new FilterCriteria(
        WhitePlayer: new PlayerFilter("Carlsen", MatchMode.Contains),
        DateRange: new DateRangeFilter(
            StartDate: new DateOnly(2020, 1, 1),
            EndDate: new DateOnly(2022, 12, 31)
        )
    )
));

Assert.True(results.MatchingGames.Count > 0);
Assert.All(results.MatchingGames, game => {
    Assert.Contains("Carlsen", game.WhitePlayer, StringComparison.OrdinalIgnoreCase);
    Assert.InRange(game.Date, new DateOnly(2020,1,1), new DateOnly(2022,12,31));
});
```

## 9. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format (auto-upgrade to v3 on write)
- **Forward compatibility:** Reject v4+ indexes with clear error message ("Index format newer than supported version")
- **PGN standard compliance:** Adhere to [PGN Spec v1.0](https://www.chessclub.com/user/PGN-spec) for output generation
- **Extension tags:** Preserve all non-standard tags (`[MyCustomTag "value"]`) during filtering/extraction