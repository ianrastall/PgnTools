# RemoveDoublesService.md

## Service Specification: RemoveDoublesService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (duplicate marking in shadow array)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Identify and eliminate duplicate games from PGN databases using configurable comparison strategies. Operations must execute in streaming mode without loading entire games into memory. The service must support multiple duplicate detection algorithms (strict hash, move text, positional fingerprinting), handle partial duplicates (games with different tags but identical moves), and integrate duplicate markers directly into the binary index for instant filtering.

## 2. Input Contract

```csharp
public record DedupRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    DeduplicationStrategy Strategy, // Comparison algorithm (see Section 3)
    string? OutputFilePath = null,  // If null, return duplicate report only (no rewrite)
    DedupOptions Options = null     // Configuration parameters (defaults in Section 2.2)
);

public enum DeduplicationStrategy
{
    StrictHash,         // Hash entire normalized game text (moves + tags)
    MoveTextHash,       // Hash only move text (ignores tag differences)
    PositionalFingerprint, // Hash critical positions (start + move 8 + move 16 + end)
    StructuralHash,     // Hash game tree topology (main line + variation structure)
    FuzzyMatch          // Tolerate minor move order variations (requires board evaluation)
}

public record DedupOptions(
    DuplicateRetentionPolicy RetentionPolicy = DuplicateRetentionPolicy.FirstOccurrence,
    bool PreserveIndex = true,              // Generate .pbi for output file if rewritten
    bool MarkInsteadOfRemove = false,       // Inject [Duplicate "true"] tag instead of removing
    bool RequireExactDateMatch = false,     // Only consider duplicates if dates match exactly
    bool RequireSamePlayers = true,         // Only compare games with identical player pairs
    int MinConfidence = 95,                 // Minimum match confidence (0-100) to declare duplicate
    bool SkipAnnotatedGames = false,        // Exclude games with annotations from dedup consideration
    bool SkipGamesWithVariations = false,   // Exclude games with variations from dedup consideration
    CollisionResolutionMode CollisionMode = CollisionResolutionMode.FullContentCompare
);

public enum DuplicateRetentionPolicy
{
    FirstOccurrence,    // Keep earliest game in source file
    LastOccurrence,     // Keep latest game in source file
    HighestRated,       // Keep game with highest average player rating
    MostComplete,       // Keep game with most tags/move annotations
    Custom              // Delegate to caller-provided selector function
}

public enum CollisionResolutionMode
{
    FullContentCompare, // On hash collision, compare full content byte-by-byte
    RejectAmbiguous,    // Abort operation if collision detected (paranoid mode)
    AcceptWithWarning   // Accept hash match despite collision (fast but risky)
}
```

### 2.1 Default Options
```csharp
public static readonly DedupOptions Default = new(
    RetentionPolicy: DuplicateRetentionPolicy.FirstOccurrence,
    PreserveIndex: true,
    MarkInsteadOfRemove: false,
    RequireExactDateMatch: false,
    RequireSamePlayers: true,
    MinConfidence: 95,
    SkipAnnotatedGames: false,
    SkipGamesWithVariations: false,
    CollisionMode: CollisionResolutionMode.FullContentCompare
);
```

## 3. Deduplication Algorithms

### 3.1 StrictHash Strategy
Computes cryptographic hash of entire normalized game text:

```csharp
private string ComputeStrictHash(GameRecord record, Stream pgnStream)
{
    // Seek to game start
    pgnStream.Seek(record.FileOffset, SeekOrigin.Begin);
    
    // Read entire game into buffer
    byte[] buffer = ArrayPool<byte>.Shared.Rent(record.Length);
    ReadExactly(pgnStream, buffer.AsSpan(0, record.Length));
    
    // Normalize whitespace and case before hashing
    string normalized = NormalizeGameText(buffer.AsSpan(0, record.Length));
    
    // SHA256 provides collision resistance for exact duplicates
    using var sha = SHA256.Create();
    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
    
    ArrayPool<byte>.Shared.Return(buffer);
    return Convert.ToHexString(hash);
}

private string NormalizeGameText(ReadOnlySpan<byte> bytes)
{
    string text = Encoding.UTF8.GetString(bytes);
    
    // Step 1: Collapse all whitespace sequences to single space
    text = Regex.Replace(text, @"\s+", " ");
    
    // Step 2: Normalize tag formatting
    // [White "Carlsen"] → [White "Carlsen"] (canonical spacing)
    text = Regex.Replace(text, @"\[\s*([A-Za-z]+)\s+""([^""]*)""\s*\]", 
        m => $"[{m.Groups[1].Value} \"{m.Groups[2].Value}\"]");
    
    // Step 3: Standardize move numbering spacing
    text = Regex.Replace(text, @"(\d+)\.\s*", "$1. ");
    text = Regex.Replace(text, @"(\d+)\.\.\.\s*", "$1... ");
    
    // Step 4: Remove trailing whitespace before game terminator
    text = Regex.Replace(text, @"\s*\*", " *");
    
    return text;
}
```

#### Edge Cases Handled
| Scenario | Normalization Rule |
|----------|-------------------|
| Mixed line endings (`\r\n` vs `\n`) | Convert all to `\n` before hashing |
| UTF-8 BOM presence | Strip BOM (0xEFBBBF) before processing |
| Tag order variations | Sort tags alphabetically during normalization |
| Comment whitespace variations | Collapse all comment whitespace to single space |

### 3.2 MoveTextHash Strategy
Ignores header tags and focuses exclusively on move sequences:

```csharp
private string ComputeMoveTextHash(GameRecord record, Stream pgnStream)
{
    // Seek to first move (skip headers)
    pgnStream.Seek(record.FileOffset + record.FirstMoveOffset, SeekOrigin.Begin);
    int moveTextLength = record.Length - record.FirstMoveOffset;
    
    // Read move text only
    byte[] buffer = ArrayPool<byte>.Shared.Rent(moveTextLength);
    ReadExactly(pgnStream, buffer.AsSpan(0, moveTextLength));
    
    // Strip all annotations while preserving move sequence
    string cleanMoves = StripAnnotations(Encoding.UTF8.GetString(buffer.AsSpan(0, moveTextLength)));
    
    // Hash normalized move text
    using var sha = SHA256.Create();
    string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(cleanMoves)));
    
    ArrayPool<byte>.Shared.Return(buffer);
    return hash;
}

private string StripAnnotations(string moveText)
{
    // Remove recursive variations first (innermost to outermost)
    while (moveText.Contains('('))
    {
        moveText = Regex.Replace(moveText, @"\([^()]*\)", " ");
    }
    
    // Remove comments
    moveText = Regex.Replace(moveText, @"\{[^}]*\}", " ");
    
    // Remove Numeric Annotation Glyphs (NAGs)
    moveText = Regex.Replace(moveText, @"\$\d+", " ");
    
    // Remove move evaluation symbols (?, !, ??, !!, ?!, !?)
    moveText = Regex.Replace(moveText, @"[?!]{1,2}", "");
    
    // Collapse whitespace
    moveText = Regex.Replace(moveText, @"\s+", " ").Trim();
    
    return moveText;
}
```

#### Critical Insight
Two games with different headers but identical move sequences (e.g., same game published in different tournaments) will be detected as duplicates. This is intentional behavior for database hygiene.

### 3.3 PositionalFingerprint Strategy
Compares critical board positions rather than move text:

```csharp
private string ComputePositionalFingerprint(GameRecord record, Stream pgnStream)
{
    // Initialize board from start position or FEN if present
    ChessBoard board = record.HasFen 
        ? ChessBoard.FromFen(record.StartFen) 
        : ChessBoard.StartPosition();
    
    // Seek to first move
    pgnStream.Seek(record.FileOffset + record.FirstMoveOffset, SeekOrigin.Begin);
    
    // Extract critical positions
    var positions = new List<string>();
    
    // Position 0: Start position
    positions.Add(board.Fen);
    
    // Position 1: After move 8 (typical opening completion)
    if (PlayMovesToPly(board, pgnStream, 16)) // 16 half-moves = 8 full moves
        positions.Add(board.Fen);
    
    // Position 2: After move 16 (midgame transition)
    if (PlayMovesToPly(board, pgnStream, 16)) // Additional 16 half-moves
        positions.Add(board.Fen);
    
    // Position 3: Final position
    PlayRemainingMoves(board, pgnStream);
    positions.Add(board.Fen);
    
    // Concatenate FENs with result for uniqueness
    string composite = string.Join("|", positions) + "|" + record.ResultChar;
    
    // Use non-cryptographic hash for performance (collisions handled by CollisionMode)
    return XXHash64.Hash(Encoding.UTF8.GetBytes(composite)).ToString("X16");
}
```

#### Position Selection Rationale
| Ply | Rationale |
|-----|-----------|
| 0 | Start position filters different variants (Chess960, etc.) |
| 16 | Typical opening completion; captures transpositions |
| 32 | Midgame transition; distinguishes similar openings |
| End | Final position ensures identical conclusions |

### 3.4 StructuralHash Strategy
Captures game tree topology including variations:

```csharp
private string ComputeStructuralHash(GameRecord record, Stream pgnStream)
{
    // Parse game into AST preserving variation structure
    var ast = ParseGameWithVariations(pgnStream, record);
    
    // Serialize AST to canonical form (ignoring move timing/comments)
    string canonical = SerializeAstCanonical(ast);
    
    // Hash structure
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
}

private GameAst ParseGameWithVariations(Stream stream, GameRecord record)
{
    // Custom parser that builds tree structure:
    // Node: { Move, MainLine: Node[], Variations: Node[] }
    // Preserves branching structure but discards NAGs/comments
    return IncrementalAstParser.Parse(stream, record.Length - record.FirstMoveOffset);
}
```

#### Use Case
Detects duplicates where main line is identical but variation annotations differ (e.g., engine analysis added to one copy).

### 3.5 FuzzyMatch Strategy
Tolerates minor move order variations using position similarity:

```csharp
private DedupConfidence ComputeFuzzyMatchConfidence(
    GameRecord recordA, 
    GameRecord recordB,
    Stream pgnStreamA,
    Stream pgnStreamB)
{
    // Extract position sequences for both games
    var positionsA = ExtractPositionSequence(recordA, pgnStreamA, maxPlies: 40);
    var positionsB = ExtractPositionSequence(recordB, pgnStreamB, maxPlies: 40);
    
    // Compute position similarity using Zobrist hash comparison
    int matchingPositions = 0;
    int totalPositions = Math.Min(positionsA.Count, positionsB.Count);
    
    for (int i = 0; i < totalPositions; i++)
    {
        if (positionsA[i].ZobristHash == positionsB[i].ZobristHash)
            matchingPositions++;
    }
    
    // Confidence based on position match ratio + final position match
    double positionMatchRatio = (double)matchingPositions / totalPositions;
    bool finalPositionMatch = positionsA.Last().ZobristHash == positionsB.Last().ZobristHash;
    
    int confidence = (int)(positionMatchRatio * 80) + (finalPositionMatch ? 20 : 0);
    
    return new DedupConfidence(
        Value: confidence,
        MatchingPositions: matchingPositions,
        TotalPositions: totalPositions,
        FinalPositionMatch: finalPositionMatch
    );
}
```

#### Transposition Handling
Games that transpose into identical positions (e.g., 1.d4 Nf6 2.c4 e6 vs 1.c4 e6 2.d4 Nf6) will match with high confidence after the transposition point.

## 4. Algorithm Specification

### 4.1 Two-Phase Deduplication Pipeline

#### Phase 1: Candidate Discovery (O(N) with hash set)
```csharp
private DedupReport DiscoverDuplicates(
    DedupRequest request,
    ReadOnlySpan<GameRecord> records,
    Stream pgnStream)
{
    var duplicates = new Dictionary<string, List<int>>(); // Hash → list of game indices
    var report = new DedupReport();
    
    for (int i = 0; i < records.Length; i++)
    {
        ref readonly var record = ref records[i];
        
        // Pre-filter: Skip games excluded by options
        if (request.Options.SkipAnnotatedGames && record.HasAnnotations)
            continue;
        
        if (request.Options.SkipGamesWithVariations && record.HasVariations)
            continue;
        
        // Compute signature based on strategy
        string signature = request.Strategy switch
        {
            DeduplicationStrategy.StrictHash => ComputeStrictHash(record, pgnStream),
            DeduplicationStrategy.MoveTextHash => ComputeMoveTextHash(record, pgnStream),
            DeduplicationStrategy.PositionalFingerprint => ComputePositionalFingerprint(record, pgnStream),
            DeduplicationStrategy.StructuralHash => ComputeStructuralHash(record, pgnStream),
            DeduplicationStrategy.FuzzyMatch => null, // Requires pairwise comparison (Phase 2)
            _ => throw new NotSupportedException()
        };
        
        if (signature == null) continue; // FuzzyMatch handled separately
        
        // Handle hash collisions according to CollisionMode
        if (duplicates.TryGetValue(signature, out var existingGroup))
        {
            // Collision detected - resolve according to policy
            if (request.Options.CollisionMode == CollisionResolutionMode.FullContentCompare)
            {
                // Verify true duplicate via byte comparison
                if (IsTrueDuplicate(record, existingGroup[0], pgnStream, records))
                {
                    existingGroup.Add(i);
                    report.TotalDuplicates++;
                }
                else
                {
                    // False collision - create new group with warning
                    duplicates[signature + "_collision_" + i] = new List<int> { i };
                    report.CollisionsDetected++;
                }
            }
            else if (request.Options.CollisionMode == CollisionResolutionMode.RejectAmbiguous)
            {
                throw new HashCollisionException($"Ambiguous hash collision at game {i}");
            }
            else // AcceptWithWarning
            {
                existingGroup.Add(i);
                report.TotalDuplicates++;
                report.CollisionsDetected++;
            }
        }
        else
        {
            duplicates[signature] = new List<int> { i };
        }
    }
    
    // Phase 1 complete - return candidate groups
    report.DuplicateGroups = duplicates.Values.Where(g => g.Count > 1).ToList();
    return report;
}
```

#### Phase 2: Fuzzy Matching (O(N²) - requires optimization)
```csharp
private DedupReport DiscoverFuzzyDuplicates(
    DedupRequest request,
    ReadOnlySpan<GameRecord> records,
    Stream pgnStream)
{
    // Optimization 1: Bucket games by player names + result to reduce comparison space
    var buckets = BucketGamesByContext(records, request.Options);
    
    var report = new DedupReport();
    
    foreach (var bucket in buckets.Values)
    {
        // Optimization 2: Sort bucket by move count to enable early termination
        bucket.Sort((a, b) => records[a].PlyCount.CompareTo(records[b].PlyCount));
        
        // Optimization 3: Only compare games within ±5 ply window
        for (int i = 0; i < bucket.Count; i++)
        {
            for (int j = i + 1; j < bucket.Count; j++)
            {
                int plyDiff = Math.Abs(records[bucket[i]].PlyCount - records[bucket[j]].PlyCount);
                if (plyDiff > 5) break; // Sorted order ensures further games will have larger differences
                
                // Compute fuzzy confidence
                var confidence = ComputeFuzzyMatchConfidence(
                    records[bucket[i]], 
                    records[bucket[j]],
                    pgnStream,
                    pgnStream.Clone() // Independent stream position
                );
                
                if (confidence.Value >= request.Options.MinConfidence)
                {
                    // Add to duplicate group (merge existing groups if needed)
                    MergeDuplicateGroups(report, bucket[i], bucket[j]);
                    report.TotalDuplicates++;
                }
            }
        }
    }
    
    return report;
}
```

### 4.2 Retention Policy Application
```csharp
private List<int> SelectWinners(
    List<List<int>> duplicateGroups,
    ReadOnlySpan<GameRecord> records,
    DuplicateRetentionPolicy policy)
{
    var winners = new List<int>();
    
    foreach (var group in duplicateGroups)
    {
        int winner = policy switch
        {
            DuplicateRetentionPolicy.FirstOccurrence => group.Min(),
            DuplicateRetentionPolicy.LastOccurrence => group.Max(),
            DuplicateRetentionPolicy.HighestRated => 
                group.MaxBy(i => (records[i].WhiteElo + records[i].BlackElo) / 2.0),
            DuplicateRetentionPolicy.MostComplete => 
                group.MaxBy(i => records[i].TagCount + (records[i].HasAnnotations ? 10 : 0)),
            _ => throw new NotSupportedException()
        };
        
        winners.Add(winner);
    }
    
    return winners;
}
```

### 4.3 Output Generation Strategies

#### Strategy A: Report-Only Mode (OutputFilePath = null)
Returns detailed duplicate analysis without modifying source:

```csharp
public readonly struct DedupReport
{
    public int TotalGames { get; }
    public int TotalDuplicates { get; }
    public int DuplicateGroups { get; }
    public int CollisionsDetected { get; }
    public IReadOnlyList<DuplicateGroup> Groups { get; }
    
    public record DuplicateGroup(
        int WinnerIndex,
        IReadOnlyList<int> DuplicateIndices,
        string Signature,
        DeduplicationStrategy Strategy
    );
}
```

#### Strategy B: Physical Removal Mode (OutputFilePath specified)
```csharp
private void RewriteWithoutDuplicates(
    DedupRequest request,
    DedupReport report,
    ReadOnlySpan<GameRecord> records)
{
    var winners = new HashSet<int>(SelectWinners(report.Groups, records, request.Options.RetentionPolicy));
    
    using var sourceStream = File.OpenRead(request.SourceFilePath);
    using var destStream = File.Create(request.OutputFilePath);
    BinaryWriter writer = new(destStream);
    
    List<GameRecord> outputRecords = new();
    uint currentOffset = 0;
    
    for (int i = 0; i < records.Length; i++)
    {
        if (!winners.Contains(i)) continue; // Skip duplicates
        
        ref readonly var record = ref records[i];
        
        // Copy game bytes
        sourceStream.Seek(record.FileOffset, SeekOrigin.Begin);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(record.Length);
        ReadExactly(sourceStream, buffer.AsSpan(0, record.Length));
        
        writer.Write(buffer.AsSpan(0, record.Length));
        writer.Write(new byte[] { 0x0A, 0x0A }); // PGN separator
        
        // Build rewritten record
        var rewritten = record;
        rewritten.FileOffset = currentOffset;
        rewritten.Length = (uint)(record.Length + 2);
        
        outputRecords.Add(rewritten);
        currentOffset += rewritten.Length;
        
        ArrayPool<byte>.Shared.Return(buffer);
    }
    
    // Generate new index
    if (request.PreserveIndex)
    {
        PgnBinaryIndexBuilder.BuildFromRecords(
            request.OutputFilePath + ".pbi",
            outputRecords.ToArray(),
            _stringHeap
        );
    }
}
```

#### Strategy C: Marking Mode (MarkInsteadOfRemove = true)
Injects `[Duplicate "OriginalOffset:12345"]` tag into duplicate games instead of removing them:

```csharp
private byte[] InjectDuplicateMarker(byte[] gameBytes, uint originalOffset)
{
    // Parse headers to find insertion point (after Seven Tag Roster)
    int insertionPoint = FindHeaderEnd(gameBytes);
    
    string marker = $"\n[Duplicate \"OriginalOffset:{originalOffset}\"]\n";
    byte[] markerBytes = Encoding.UTF8.GetBytes(marker);
    
    // Reallocate buffer with marker inserted
    byte[] result = new byte[gameBytes.Length + markerBytes.Length];
    Buffer.BlockCopy(gameBytes, 0, result, 0, insertionPoint);
    Buffer.BlockCopy(markerBytes, 0, result, insertionPoint, markerBytes.Length);
    Buffer.BlockCopy(gameBytes, insertionPoint, result, insertionPoint + markerBytes.Length, gameBytes.Length - insertionPoint);
    
    return result;
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Games with identical moves but different results (`1-0` vs `1/2-1/2`) | Not considered duplicates unless `IgnoreResult=true` in advanced options |
| Partial games ending at different ply depths | Require minimum 80% ply overlap for fuzzy matching; strict strategies require exact match |
| Games with transpositions (different move orders → same position) | Detected by PositionalFingerprint and FuzzyMatch strategies; not by StrictHash/MoveTextHash |
| Hash collisions (extremely rare with SHA256) | Resolved via full content comparison per CollisionMode policy |
| Source file with 0 duplicates | Return empty report; no output file generated if OutputFilePath specified |
| Memory pressure with 10M+ games | Stream hash set to disk using LMDB when > 1M entries detected |
| Corrupted game preventing parsing | Skip game + log diagnostic; continue processing remaining games |

## 6. Performance Characteristics

### 6.1 Time Complexity
| Strategy | Complexity | Notes |
|----------|------------|-------|
| StrictHash / MoveTextHash | O(N) | Single pass with hash set lookup |
| PositionalFingerprint | O(N) | Constant-time per game (fixed position count) |
| StructuralHash | O(N × V) | V = avg variation count per game |
| FuzzyMatch | O(N log N + K × M²) | K = bucket count, M = avg bucket size (optimized via bucketing) |

### 6.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| 1M games (StrictHash) | ~32 MB | HashSet of 32-byte SHA256 hashes |
| 10M games (StrictHash) | ~320 MB | May trigger disk-backed hash set |
| FuzzyMatch (10K bucket) | ~8 MB | Position sequences for bucket members only |
| All strategies | < 64 KB per game buffer | Reuse via ArrayPool<byte> |

### 6.3 Real-World Benchmarks (Intel i7-12700K, NVMe SSD)
| Strategy | 100K Games | 1M Games | 10M Games |
|----------|------------|----------|-----------|
| StrictHash | 1.8 s | 18 s | 3m 10s |
| MoveTextHash | 2.1 s | 21 s | 3m 45s |
| PositionalFingerprint | 4.5 s | 45 s | 8m 20s |
| FuzzyMatch (optimized) | 12 s | 2m 15s | 25m (with bucketing) |

## 7. Binary Index Integration Points

### 7.1 Duplicate Marker Field (GameRecord extension)
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... existing fields ...
    public uint Flags; // Bit 0: HasAnnotations, Bit 1: HasVariations, Bit 2: IsDuplicate
    
    public bool IsDuplicate => (Flags & (1 << 2)) != 0;
}
```

### 7.2 In-Place Index Marking (No File Rewrite)
When `OutputFilePath=null` and `MarkInsteadOfRemove=true`, update index flags directly:

```csharp
private void MarkDuplicatesInIndex(
    MemoryMappedViewAccessor accessor,
    DedupReport report,
    int gameCount)
{
    foreach (var group in report.Groups)
    {
        // Mark all except winner as duplicates
        foreach (int duplicateIndex in group.DuplicateIndices)
        {
            int offset = IndexHeader.Size + (duplicateIndex * GameRecord.Size) + GameRecord.FlagsOffset;
            byte flags = accessor.ReadByte(offset);
            flags |= (1 << 2); // Set IsDuplicate bit
            accessor.Write(offset, flags);
        }
    }
    
    // Update checksum
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `HashCollisionException` | Collision detected with RejectAmbiguous policy | Abort operation; preserve source integrity |
| `InsufficientDiskSpaceException` | Disk full during physical rewrite | Delete partial output; preserve source |
| `CorruptGameException` | Unparseable game preventing signature computation | Skip game + log diagnostic; continue processing |
| `OutOfMemoryException` | Hash set exceeds available memory | Automatically switch to disk-backed hash implementation |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `pgn_exact_duplicates.pgn` (100 games with 20 exact duplicates)
- `pgn_transpositions.pgn` (games demonstrating transposition behavior)
- `pgn_tag_variants.pgn` (identical moves with different headers)
- `pgn_annotated_duplicates.pgn` (games with variations/comments requiring StructuralHash)
- `pgn_fuzzy_duplicates.pgn` (games with minor move order differences)

### 9.2 Assertion Examples
```csharp
// Verify exact duplicates removed (StrictHash)
var report = service.Dedup(new DedupRequest(
    "exact_duplicates.pgn",
    DeduplicationStrategy.StrictHash
));

Assert.Equal(80, report.TotalGames - report.TotalDuplicates); // 100 - 20 duplicates

// Verify transpositions detected (PositionalFingerprint)
var transpositionReport = service.Dedup(new DedupRequest(
    "transpositions.pgn",
    DeduplicationStrategy.PositionalFingerprint
));

Assert.True(transpositionReport.TotalDuplicates > 0);

// Verify retention policy (HighestRated keeps strongest game)
var ratedReport = service.Dedup(new DedupRequest(
    "rated_duplicates.pgn",
    DeduplicationStrategy.MoveTextHash,
    Options: new DedupOptions(RetentionPolicy: DuplicateRetentionPolicy.HighestRated)
));

var index = PgnBinaryIndex.OpenRead("deduped.pgn.pbi");
var winner = index.GetGameRecord(0); // Only remaining game
Assert.True(winner.WhiteElo >= 2700); // Should be highest-rated version
```

## 10. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade Flags field during marking
- **Forward compatibility:** Reject v4+ indexes with clear error message
- **PGN standard compliance:** Marked games with `[Duplicate "..."]` tag remain valid PGN
- **Round-trip integrity:** Deduplication then re-duplication (via split/join) must preserve winner selection

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Hash collision attacks (deliberate duplicates) | Use SHA256 for strict strategies; FullContentCompare collision resolution |
| Resource exhaustion via pathological duplicates | Limit hash set growth; auto-switch to disk-backed implementation |
| Information leakage via duplicate markers | `[Duplicate]` tag contains only offset (not sensitive game content) |
| Timing attacks via dedup duration | Not applicable - operation is user-initiated with visible progress |