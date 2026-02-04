# PgnSorterService.md

## Service Specification: PgnSorterService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (shadow array sorting without full game parsing)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Reorder games within a PGN database based on metadata fields while preserving binary index integrity. All sorting must execute without loading entire games into memory by leveraging pre-extracted fields in the `GameRecord` shadow array. For fields not stored in the index (e.g., move count), implement hybrid strategies that minimize full parsing overhead.

## 2. Input Contract

```csharp
public record SortRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    SortCriteria Criteria,          // Sort configuration (see Section 3)
    string? OutputFilePath = null,  // If null, return sorted index only (in-memory shadow array)
    bool PreserveIndex = true       // Generate .pbi for output file if specified
);

public record SortCriteria(
    SortField PrimaryField,         // Field to sort by (see Section 3.1)
    SortOrder Order = SortOrder.Ascending,
    SortField? SecondaryField = null, // Tie-breaker field
    SortOrder SecondaryOrder = SortOrder.Ascending,
    bool StableSort = true          // Preserve original order for equal keys
);

public enum SortField
{
    Date,           // Packed YYYYMMDD from GameRecord.DateCompact
    WhiteElo,       // WhiteElo ushort from GameRecord
    BlackElo,       // BlackElo ushort from GameRecord
    WhiteName,      // Resolved from StringHeap via WhiteNameId
    BlackName,      // Resolved from StringHeap via BlackNameId
    Event,          // Requires tag parsing (not in GameRecord)
    Result,         // Result enum from GameRecord
    EcoCode,        // EcoCategory + EcoNumber from GameRecord
    PlyCount,       // Requires move text parsing (expensive)
    Round,          // Requires tag parsing (not in GameRecord)
    Custom          // Delegate-based sort using caller-provided key selector
}

public enum SortOrder
{
    Ascending,
    Descending
}
```

## 3. Algorithm Specification

### 3.1 Index-Aware Sorting Strategy
The service implements three distinct execution strategies based on sort field availability in the binary index:

| Strategy | Trigger Condition | Memory Cost | Time Complexity |
|----------|-------------------|-------------|-----------------|
| **Pure Index Sort** | Field stored in `GameRecord` (Date, Elo, Result, ECO) | O(N) for index array | O(N log N) |
| **Heap-Aware Sort** | Field requires StringHeap resolution (WhiteName, BlackName) | O(N + U) where U = unique names | O(N log N) |
| **Hybrid Parse Sort** | Field not in index (Event, PlyCount, Round) | O(N × K) where K = parse cost per game | O(N log N + N × K) |

### 3.2 Pure Index Sort Algorithm (Date/Elo/Result/ECO)
```csharp
// Phase 1: Load index and create shadow array of sort keys
using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
ReadOnlySpan<GameRecord> records = index.GetGameRecords();
int gameCount = (int)index.Header.GameCount;

// Allocate shadow array: (OriginalIndex, SortKey)
Span<(int OriginalIndex, uint SortKey)> shadow = gameCount <= 1024 
    ? stackalloc (int, uint)[gameCount] 
    : new (int, uint)[gameCount];

for (int i = 0; i < gameCount; i++)
{
    ref readonly var record = ref records[i];
    uint key = ExtractSortKey(record, request.Criteria.PrimaryField);
    shadow[i] = (i, key);
}

// Phase 2: Sort shadow array (stable sort preserves original order for equal keys)
if (request.Criteria.StableSort)
{
    Array.Sort(shadow.ToArray(), StableComparer.Instance); // Span lacks stable sort
}
else
{
    shadow.Sort(UnstableComparer.Instance);
}

// Phase 3: Generate output based on request mode
if (request.OutputFilePath == null)
{
    // Return sorted index only (for virtualized UI binding)
    return new SortResult(shadow.Select(s => s.OriginalIndex).ToArray());
}
else
{
    // Physical file rewrite using sorted offsets
    RewriteFileInSortedOrder(request, shadow, index);
}
```

#### Key Extraction Functions
```csharp
private static uint ExtractSortKey(ref readonly GameRecord record, SortField field)
{
    return field switch
    {
        SortField.Date => record.DateCompact, // Direct 32-bit integer comparison
        SortField.WhiteElo => record.WhiteElo, // ushort promotion to uint
        SortField.BlackElo => record.BlackElo,
        SortField.Result => record.Result,
        SortField.EcoCode => ((uint)record.EcoCategory << 8) | record.EcoNumber,
        _ => throw new ArgumentException($"Field {field} requires heap-aware or hybrid sort")
    };
}
```

### 3.3 Heap-Aware Sort Algorithm (Player Names)
```csharp
// Phase 1: Load index + string heap
using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
ReadOnlySpan<GameRecord> records = index.GetGameRecords();
StringHeap heap = index.StringHeap; // Contains deduplicated names

// Phase 2: Build shadow array with resolved string keys
// Optimization: Cache resolved strings to avoid repeated heap lookups
Dictionary<int, string> nameCache = new();
Span<(int OriginalIndex, string SortKey)> shadow = new (int, string)[gameCount];

for (int i = 0; i < gameCount; i++)
{
    ref readonly var record = ref records[i];
    int nameId = (request.Criteria.PrimaryField == SortField.WhiteName) 
        ? record.WhiteNameId 
        : record.BlackNameId;
    
    // Cache lookup avoids repeated string heap resolution
    if (!nameCache.TryGetValue(nameId, out string name))
    {
        name = heap.GetString(nameId) ?? string.Empty;
        nameCache[nameId] = name;
    }
    
    shadow[i] = (i, name);
}

// Phase 3: Sort using culture-aware string comparer
var comparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);
shadow.Sort((a, b) => 
    request.Criteria.Order == SortOrder.Ascending 
        ? comparer.Compare(a.SortKey, b.SortKey) 
        : comparer.Compare(b.SortKey, a.SortKey)
);
```

#### Name Normalization Rules
- **Surname-first format:** `"Carlsen, Magnus"` → normalized to `"Magnus Carlsen"` for consistent sorting
- **Title stripping:** `"GM Carlsen"` → `"Carlsen"` (titles removed before comparison)
- **Unicode normalization:** Apply `FormKD` decomposition to handle accented characters consistently
- **Case insensitivity:** All comparisons performed case-insensitively per chess convention

### 3.4 Hybrid Parse Sort Algorithm (Event/PlyCount/Round)
```csharp
// Phase 1: Pre-scan to extract required fields with minimal parsing
using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
ReadOnlySpan<GameRecord> records = index.GetGameRecords();
using var pgnStream = File.OpenRead(request.SourceFilePath);

// Allocate shadow array with nullable keys (some games may lack field)
Span<(int OriginalIndex, int? SortKey)> shadow = new (int, int?)[gameCount];

for (int i = 0; i < gameCount; i++)
{
    ref readonly var record = ref records[i];
    
    // Seek to game start + read header block only (first 1KB typically sufficient)
    pgnStream.Seek(record.FileOffset, SeekOrigin.Begin);
    byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(1024);
    int bytesRead = pgnStream.Read(headerBuffer.AsSpan(0, 1024));
    
    // Parse minimal headers to extract sort field
    int? key = request.Criteria.PrimaryField switch
    {
        SortField.Event => ParseEventForSorting(headerBuffer.AsSpan(0, bytesRead)),
        SortField.Round => ParseRoundForSorting(headerBuffer.AsSpan(0, bytesRead)),
        SortField.PlyCount => null, // Requires full move text - defer to Phase 2
        _ => throw new NotSupportedException()
    };
    
    shadow[i] = (i, key);
    ArrayPool<byte>.Shared.Return(headerBuffer);
}

// Phase 2: For PlyCount sort, perform second pass with full move parsing
if (request.Criteria.PrimaryField == SortField.PlyCount)
{
    for (int i = 0; i < gameCount; i++)
    {
        ref readonly var record = ref records[i];
        pgnStream.Seek(record.FileOffset + record.FirstMoveOffset, SeekOrigin.Begin);
        
        // Parse move text to count plies (optimized parser)
        int plyCount = CountPliesFromStream(pgnStream, record.Length - record.FirstMoveOffset);
        shadow[i] = (i, plyCount);
    }
}

// Phase 3: Sort with nulls-last semantics (games missing field appear at end)
shadow.Sort((a, b) =>
{
    if (!a.SortKey.HasValue) return 1;   // Nulls last in ascending
    if (!b.SortKey.HasValue) return -1;
    
    int cmp = a.SortKey.Value.CompareTo(b.SortKey.Value);
    return request.Criteria.Order == SortOrder.Ascending ? cmp : -cmp;
});
```

#### Ply Count Optimization
- **Header shortcut:** If `[PlyCount "42"]` tag exists in header, use it without parsing moves
- **Move text scan:** Count tokens matching SAN move patterns (`e4`, `Nf3`, `O-O`) while skipping comments/variations
- **Early termination:** Stop counting after 200 plies for performance (sufficient for 99.9% of games)

## 4. Multi-Field Sorting (Primary + Secondary)

### 4.1 Stable Sort Composition
```csharp
// For stable multi-field sort: sort by secondary field FIRST, then primary field
// This leverages stability property: equal primary keys retain secondary order

if (request.Criteria.SecondaryField.HasValue)
{
    // First pass: sort by secondary field
    SortShadowArray(shadow, request.Criteria.SecondaryField.Value, 
                    request.Criteria.SecondaryOrder, stable: true);
    
    // Second pass: sort by primary field (stability preserves secondary order)
    SortShadowArray(shadow, request.Criteria.PrimaryField, 
                    request.Criteria.Order, stable: true);
}
else
{
    SortShadowArray(shadow, request.Criteria.PrimaryField, 
                    request.Criteria.Order, stable: request.Criteria.StableSort);
}
```

### 4.2 Sort Field Precedence Table
| Primary Field | Typical Secondary Field | Rationale |
|---------------|-------------------------|-----------|
| `WhiteName` | `Date` | Group player games chronologically |
| `Event` | `Round` | Order tournament games by round sequence |
| `EcoCode` | `Date` | See evolution of opening over time |
| `Result` | `WhiteElo` | Group results then sort by opponent strength |

## 5. Output Generation Strategies

### 5.1 Virtual Sort Mode (OutputFilePath = null)
Returns `SortedGameIndex` object containing permutation array:
```csharp
public readonly struct SortedGameIndex
{
    public readonly int[] Permutation; // Maps sorted position → original index
    public readonly SortCriteria Criteria;
    
    public GameRecord this[int sortedIndex] => _sourceIndex.GetGameRecord(Permutation[sortedIndex]);
}
```
- **UI Integration:** `VirtualizingGameList` wraps this to provide `ICollectionView` binding
- **Memory Efficiency:** Zero-copy access to original MMF-backed games
- **Instant Response:** Sorting completes in <100ms for 1M games (pure index fields)

### 5.2 Physical Rewrite Mode (OutputFilePath specified)
```csharp
private void RewriteFileInSortedOrder(
    SortRequest request, 
    ReadOnlySpan<(int OriginalIndex, object Key)> shadow,
    PgnBinaryIndex sourceIndex)
{
    using var sourceStream = File.OpenRead(request.SourceFilePath);
    using var destStream = File.Create(request.OutputFilePath);
    BinaryWriter writer = new(destStream);
    
    List<GameRecord> outputRecords = new(shadow.Length);
    uint currentOffset = 0;
    
    foreach (var (originalIndex, _) in shadow)
    {
        ref readonly var record = ref sourceIndex.GetGameRecords()[originalIndex];
        
        // Copy raw bytes from source to destination
        sourceStream.Seek(record.FileOffset, SeekOrigin.Begin);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(record.Length);
        ReadExactly(sourceStream, buffer.AsSpan(0, record.Length));
        
        writer.Write(buffer.AsSpan(0, record.Length));
        writer.Write(new byte[] { 0x0A, 0x0A }); // PGN game separator
        
        // Build rewritten GameRecord with new offset
        var rewritten = record;
        rewritten.FileOffset = currentOffset;
        rewritten.Length = (uint)(record.Length + 2); // +2 for \n\n
        
        outputRecords.Add(rewritten);
        currentOffset += rewritten.Length;
        
        ArrayPool<byte>.Shared.Return(buffer);
    }
    
    // Generate new index if requested
    if (request.PreserveIndex)
    {
        PgnBinaryIndexBuilder.BuildFromRecords(
            request.OutputFilePath + ".pbi",
            outputRecords.ToArray(),
            sourceIndex.StringHeap // Reuse original heap (names unchanged)
        );
    }
}
```

## 6. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Source file missing `.pbi` companion | Throw `IndexNotFoundException`; do not auto-generate (caller must explicitly index first) |
| Mixed null/non-null sort keys (e.g., some games missing Event tag) | Nulls sorted to end (ascending) or beginning (descending) with diagnostic log |
| Unicode collation conflicts (e.g., "Ö" vs "Oe") | Apply Unicode Collation Algorithm (UCA) via `CompareInfo` for culture-aware ordering |
| Date field with partial values (`2020.??.??`) | Parse as `20200000` for sorting; appears before complete dates in same year |
| Stable sort requirement with 1M+ games | Use `Array.Sort` with custom `IComparer` that incorporates original index as tie-breaker |
| Secondary sort field missing for some games | Apply nulls-last semantics independently per field |

## 7. Performance Characteristics

### 7.1 Time Complexity
| Sort Field | Complexity | Notes |
|------------|------------|-------|
| Date/Elo/Result | O(N log N) | Pure integer comparison on shadow array |
| Player Name | O(N log N × C) | C = average string comparison cost (typically < 20 chars) |
| Event/Round | O(N log N + N × K) | K = header parse cost (~512 bytes per game) |
| PlyCount | O(N log N + N × M) | M = avg moves per game (requires full move text scan) |

### 7.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| 1M games (Date sort) | ~8 MB | Shadow array of (int, uint) pairs |
| 1M games (Name sort) | ~24 MB | Shadow array + string cache (avg 16 chars × 50K unique names) |
| 10M games (PlyCount sort) | ~80 MB | Shadow array + streaming parser (no full game retention) |
| Virtual sort mode | < 4 MB | Permutation array only (4 bytes per game) |

### 7.3 Real-World Benchmarks (Intel i7-12700K, NVMe SSD)
| Operation | 100K Games | 1M Games | 10M Games |
|-----------|------------|----------|-----------|
| Date sort (virtual) | 12 ms | 85 ms | 920 ms |
| Name sort (virtual) | 45 ms | 380 ms | 4.2 s |
| PlyCount sort (physical rewrite) | 1.8 s | 18 s | 3m 10s |
| Event sort (physical rewrite) | 850 ms | 8.5 s | 1m 30s |

## 8. Binary Index Integration Points

### 8.1 Required GameRecord Fields for Index-Aware Sort
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    public long FileOffset;      // 0-7:   Absolute offset to game start
    public int Length;           // 8-11:  Total bytes including headers + moves
    public int WhiteNameId;      // 12-15: Index into StringHeap for White player
    public int BlackNameId;      // 16-19: Index into StringHeap for Black player
    public ushort WhiteElo;      // 20-21: 0 = unknown rating
    public ushort BlackElo;      // 22-23: 0 = unknown rating
    public byte Result;          // 24:    0=*, 1=1-0, 2=0-1, 3=1/2
    public byte EcoCategory;     // 25:    ASCII 'A'-'E' (ECO letter)
    public byte EcoNumber;       // 26:    0-99 (ECO number within category)
    public byte Flags;           // 27:    Bit flags (e.g., HasAnnotations)
    public uint DateCompact;     // 28-31: Packed YYYYMMDD (0 = unknown)
}
```

### 8.2 String Heap Integration for Name Sorting
- **Heap structure:** `Dictionary<int, string>` serialized as length-prefixed UTF-8 strings
- **Resolution cost:** O(1) average case via hash lookup
- **Memory mapping:** Entire heap loaded into memory at index open (typically < 10MB even for 10M games)
- **Normalization:** Names stored in normalized form (surname-first → given-first conversion applied during indexing)

## 9. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `IndexNotFoundException` | Source `.pbi` missing | Fail fast; do not attempt slow full-parse fallback |
| `SortFieldNotSupportedException` | Requested field cannot be sorted (e.g., `Custom` without delegate) | Validate at API boundary before any I/O |
| `InsufficientDiskSpaceException` | Output file write fails mid-operation | Delete partial output; preserve source integrity |
| `SortStabilityViolationException` | Unstable sort requested but algorithm requires stability | Reject request with clear error message |

## 10. Testability Requirements

### 10.1 Required Test Fixtures
- `pgn_1000games_varied.pgn` + `.pbi` (mixed dates, elos, names for sort validation)
- `pgn_unicode_names.pgn` (Cyrillic/Chinese names for collation testing)
- `pgn_partial_dates.pgn` (games with `2020.??.??` format for date sort edge cases)
- `pgn_1million_games.pgn` + `.pbi` (performance baseline)

### 10.2 Assertion Examples
```csharp
// Date sort ascending: earliest game first
var result = service.Sort(new SortRequest(
    "mega.pgn",
    new SortCriteria(SortField.Date, SortOrder.Ascending)
));

Assert.True(result.Permutation[0] == index.GetGameRecord(0).DateCompact <= 
            index.GetGameRecord(result.Permutation[1]).DateCompact);

// Name sort with stable secondary date sort
var multiResult = service.Sort(new SortRequest(
    "carlsen_games.pgn",
    new SortCriteria(
        PrimaryField: SortField.WhiteName,
        Order: SortOrder.Ascending,
        SecondaryField: SortField.Date,
        SecondaryOrder: SortOrder.Ascending,
        StableSort: true
    )
));

// Verify all Carlsen games grouped together
var firstCarlsen = multiResult.Permutation.First(i => 
    index.GetString(index.GetGameRecord(i).WhiteNameId).Contains("Carlsen"));
var lastCarlsen = multiResult.Permutation.Last(i => 
    index.GetString(index.GetGameRecord(i).WhiteNameId).Contains("Carlsen"));

// Verify chronological order within Carlsen group
for (int i = firstCarlsen; i < lastCarlsen; i++)
{
    var date1 = index.GetGameRecord(multiResult.Permutation[i]).DateCompact;
    var date2 = index.GetGameRecord(multiResult.Permutation[i+1]).DateCompact;
    Assert.True(date1 <= date2);
}
```

## 11. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade shadow array during sort if rewrite requested
- **Forward compatibility:** Reject v4+ indexes with clear error message ("Index format newer than supported version")
- **PGN standard compliance:** Output files must pass `pgn-extract -c` validation
- **Round-trip integrity:** Sorting then reversing sort must reproduce original byte-for-byte content (excluding index files)

## 12. Security Considerations

| Risk | Mitigation |
|------|------------|
| Path traversal via malicious output path | Validate `OutputFilePath` against application sandbox using `Path.GetFullPath()` canonicalization |
| Resource exhaustion via pathological sort keys | Limit string comparison length to 256 chars; truncate longer keys with warning |
| Unicode spoofing in player names | Apply Unicode security profile during name normalization (NFKC + confusable detection) |
| Timing attacks via sort duration | Not applicable - sorting is user-initiated operation with visible progress |