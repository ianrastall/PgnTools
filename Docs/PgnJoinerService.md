# PgnJoinerService.md

## Service Specification: PgnJoinerService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (index merging + conflict resolution)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Merge multiple PGN source files into a single coherent output database while preserving binary index integrity. Operations must execute in streaming mode without loading entire source files into memory. The service must handle tag normalization conflicts, deduplication strategies, and index coherence across heterogeneous sources.

## 2. Input Contract

```csharp
public record JoinRequest(
    IReadOnlyList<string> SourceFiles,      // Paths to .pgn files (each must have companion .pbi)
    string OutputFilePath,                  // Path for merged output file
    JoinOptions Options,                    // Configuration parameters
    bool PreserveIndex = true               // Generate .pbi for output file
);

public record JoinOptions(
    bool Deduplicate = false,               // Remove exact duplicates during merge
    DeduplicationMode DedupMode = DeduplicationMode.StrictHash,
    bool NormalizeTags = true,              // Apply canonical tag formatting across sources
    bool ReindexEco = false,                // Re-run ECO analysis on all games (expensive)
    bool PreserveOriginalOffsets = false,   // Store source file + offset in custom tag (debugging)
    ConflictResolutionStrategy TagConflictResolution = ConflictResolutionStrategy.FirstWins,
    bool EnsureBlankLineBetweenSources = true // PGN standard compliance
);
```

### 2.1 Deduplication Modes
```csharp
public enum DeduplicationMode
{
    StrictHash,         // Hash entire normalized game text (moves + tags)
    MoveTextHash,       // Hash only move text (ignores tag differences)
    PositionalHash,     // Hash critical positions (start + move 10 + end)
    FuzzyMatch          // Tolerate minor move order variations (requires board eval)
}
```

### 2.2 Conflict Resolution Strategies
```csharp
public enum ConflictResolutionStrategy
{
    FirstWins,          // First occurrence's tag value preserved
    LastWins,           // Last occurrence's tag value preserved
    HighestEloWins,     // For WhiteElo/BlackElo: preserve highest value
    LongestTextWins,    // For Event/Site: preserve longest string (assumed most complete)
    CustomResolver      // Delegate to caller-provided resolver function
}
```

## 3. Algorithm Specification

### 3.1 Two-Phase Execution Model
All joins execute in two distinct phases to minimize memory pressure and enable streaming:

#### Phase 1: Index Validation & Pre-Scan (O(N) time, O(1) memory per file)
```csharp
// Validate all sources have compatible indexes
List<PgnBinaryIndex> sourceIndexes = new(capacity: request.SourceFiles.Count);
foreach (string sourceFile in request.SourceFiles)
{
    if (!File.Exists(sourceFile))
        throw new FileNotFoundException($"Source file not found: {sourceFile}");
    
    string indexFile = sourceFile + ".pbi";
    if (!File.Exists(indexFile) && request.PreserveIndex)
        throw new IndexNotFoundException($"Companion index missing: {indexFile}");
    
    var index = PgnBinaryIndex.OpenRead(indexFile);
    sourceIndexes.Add(index);
}

// Calculate total game count for progress reporting
uint totalGames = sourceIndexes.Sum(idx => idx.Header.GameCount);
```

#### Phase 2: Streaming Merge with Deduplication Tracking
```csharp
using var outputStream = File.Create(request.OutputFilePath);
BinaryWriter writer = new(outputStream);

HashSet<string> seenSignatures = request.Options.Deduplicate 
    ? new HashSet<string>(StringComparer.Ordinal) 
    : null;

uint currentOffset = 0;
List<GameRecord> outputRecords = request.PreserveIndex 
    ? new List<GameRecord>((int)totalGames) 
    : null;

DateTime lastProgressReport = DateTime.UtcNow;
int gamesProcessed = 0;
int duplicatesSkipped = 0;

foreach (var (sourceIndex, sourceFile) in sourceIndexes.Zip(request.SourceFiles))
{
    using var sourceStream = File.OpenRead(sourceFile);
    
    foreach (var record in sourceIndex.GetGameRecords())
    {
        // Read raw game bytes from source
        sourceStream.Seek(record.FileOffset, SeekOrigin.Begin);
        byte[] gameBytes = new byte[record.Length];
        ReadExactly(sourceStream, gameBytes);
        
        // Apply tag normalization if requested
        if (request.Options.NormalizeTags)
        {
            gameBytes = NormalizeTags(gameBytes, request.Options.TagConflictResolution);
        }
        
        // Generate deduplication signature if enabled
        string signature = null;
        if (request.Options.Deduplicate)
        {
            signature = GenerateSignature(gameBytes, request.Options.DedupMode);
            
            // Skip if duplicate detected
            if (!seenSignatures.Add(signature))
            {
                duplicatesSkipped++;
                continue;
            }
        }
        
        // Inject source provenance tag if requested (debugging only)
        if (request.Options.PreserveOriginalOffsets)
        {
            string provenance = $"[SourceFile \"{Path.GetFileName(sourceFile)}\"]\n" +
                               $"[SourceOffset \"{record.FileOffset}\"]\n";
            gameBytes = InjectProvenanceTags(gameBytes, provenance);
        }
        
        // Write to output stream
        if (request.Options.EnsureBlankLineBetweenSources && gamesProcessed > 0)
        {
            writer.Write(new byte[] { 0x0A, 0x0A }); // \n\n
        }
        
        writer.Write(gameBytes);
        writer.Write(new byte[] { 0x0A, 0x0A }); // Game separator
        
        // Build output index record if preserving index
        if (request.PreserveIndex)
        {
            var outputRecord = record;
            outputRecord.FileOffset = currentOffset;
            outputRecord.Length = (int)(gameBytes.Length + 2); // +2 for trailing \n\n
            
            // Resolve string heap references (critical for cross-file joins)
            outputRecord.WhiteNameId = RemapStringHeapId(
                record.WhiteNameId, 
                sourceIndex.StringHeap,
                outputStringHeap
            );
            outputRecord.BlackNameId = RemapStringHeapId(
                record.BlackNameId,
                sourceIndex.StringHeap,
                outputStringHeap
            );
            
            outputRecords.Add(outputRecord);
            currentOffset += (uint)outputRecord.Length;
        }
        
        gamesProcessed++;
        
        // Progress reporting (throttled)
        if (DateTime.UtcNow - lastProgressReport > TimeSpan.FromMilliseconds(100) || 
            gamesProcessed % 1000 == 0)
        {
            double percent = (double)gamesProcessed / totalGames * 100;
            OnProgress?.Invoke(new JoinProgress(percent, gamesProcessed, duplicatesSkipped));
            lastProgressReport = DateTime.UtcNow;
        }
    }
}
```

### 3.2 Signature Generation Algorithms by Mode

#### StrictHash Mode
```csharp
private string GenerateStrictHashSignature(byte[] gameBytes)
{
    // Normalize whitespace and case before hashing
    string normalized = NormalizeGameText(gameBytes);
    
    // SHA256 provides collision resistance for exact duplicates
    using var sha = SHA256.Create();
    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
    return Convert.ToHexString(hash);
}

private string NormalizeGameText(byte[] bytes)
{
    string text = Encoding.UTF8.GetString(bytes);
    
    // Collapse all whitespace to single space
    text = Regex.Replace(text, @"\s+", " ");
    
    // Normalize tag case: [White "Carlsen"] → [White "Carlsen"] (preserve value case)
    text = Regex.Replace(text, @"\[([A-Z][a-z]*)\s+\""[^""]*\""\]", 
        m => $"[{m.Groups[1].Value} \"{m.Groups[2].Value}\"]");
    
    // Remove redundant spaces around moves
    text = Regex.Replace(text, @"\s*([1-9][0-9]*\.\.\.)\s*", "$1 ");
    
    return text;
}
```

#### MoveTextHash Mode
```csharp
private string GenerateMoveTextHashSignature(byte[] gameBytes)
{
    // Extract only move text (after headers)
    string moveText = ExtractMoveText(gameBytes);
    
    // Remove all comments, variations, and NAGs
    string cleaned = StripAnnotations(moveText);
    
    // Hash normalized move sequence
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(cleaned)));
}
```

#### PositionalHash Mode
```csharp
private string GeneratePositionalHashSignature(byte[] gameBytes)
{
    // Parse critical positions using internal board representation
    var positions = ExtractCriticalPositions(gameBytes);
    
    // FEN strings provide canonical position representation
    string fenStart = positions.Start?.ToFen() ?? "";
    string fenMid = positions.Move10?.ToFen() ?? "";
    string fenEnd = positions.End?.ToFen() ?? "";
    
    // Concatenate FENs with result for uniqueness
    string composite = $"{fenStart}|{fenMid}|{fenEnd}|{positions.Result}";
    
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(composite)));
}
```

## 4. Binary Index Integration

### 4.1 String Heap Remapping Strategy
When merging files from heterogeneous sources, player names may have different heap IDs:

| Source File | String Heap ID | Value |
|-------------|----------------|-------|
| `carlsen.pgn` | 1 | "Carlsen, Magnus" |
| `nakamura.pgn` | 3 | "Carlsen, Magnus" |

**Remapping algorithm:**
```csharp
private int RemapStringHeapId(
    int sourceId, 
    StringHeap sourceHeap,
    Dictionary<string, int> outputHeapMap)
{
    string value = sourceHeap.GetString(sourceId);
    
    // Check if value already exists in output heap
    if (outputHeapMap.TryGetValue(value, out int existingId))
        return existingId;
    
    // Assign new ID and store mapping
    int newId = outputHeapMap.Count;
    outputHeapMap[value] = newId;
    return newId;
}
```

### 4.2 Index Coherence Guarantees
- **Offset continuity:** Output file offsets form contiguous sequence starting at 0
- **Length preservation:** Game lengths preserved exactly (no reformatting unless normalization requested)
- **Heap deduplication:** Output string heap contains each unique value exactly once
- **Checksum validation:** Final index includes CRC32 of entire `.pgn` content for integrity verification

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Source file missing `.pbi` companion | Fail immediately if `PreserveIndex=true`; proceed with warning if `false` (slower full parse required) |
| Heterogeneous index versions (v2 + v3) | Auto-upgrade v2 records to v3 format during merge; log warning |
| Conflicting ECO assignments (`[ECO "B90"]` vs `[ECO "B92"]`) | Apply `TagConflictResolution` strategy; log conflict details to diagnostics stream |
| Games with binary/gibberish content | Skip malformed games + log offset + source file; continue merge |
| Disk full mid-operation | Abort with partial file cleanup; throw `InsufficientDiskSpaceException` with bytes written |
| Source files contain overlapping game sets | Deduplication removes duplicates; progress report shows `skipped: X duplicates` |

## 6. Performance Characteristics

### 6.1 Time Complexity
| Operation | Complexity | Notes |
|-----------|------------|-------|
| Basic join (no dedupe) | O(N) | N = total games across sources |
| StrictHash dedupe | O(N) amortized | HashSet lookup O(1) average case |
| PositionalHash dedupe | O(N × M) | M = avg moves per game (requires position eval) |
| Index generation | O(N) | Single pass to build shadow array |

### 6.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| 10 files × 100K games (no dedupe) | < 1 MB | Stream games sequentially; no game data retained |
| StrictHash dedupe (1M games) | ~32 MB | HashSet of 64-byte SHA256 hashes (1M × 32 bytes) |
| PositionalHash dedupe | < 64 KB | Reuse single board instance for position eval |
| String heap remapping | O(U) | U = unique player/event names (typically < 0.1% of games) |

### 6.3 I/O Patterns
- **Read pattern:** Sequential scan of each source file (optimal for HDD/SSD)
- **Write pattern:** Single sequential write to output file (maximizes throughput)
- **Buffering:** 64KB I/O buffers via `ArrayPool<byte>` to avoid LOH allocations
- **Progress reporting:** Throttled to 10Hz maximum to avoid UI thread saturation

## 7. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `SourceFileNotFoundException` | Any source `.pgn` missing | Fail fast before any I/O begins |
| `IndexVersionMismatchException` | Source `.pbi` format incompatible | Require explicit `AllowVersionMismatch=true` flag |
| `PartialWriteException` | Disk full mid-operation | Delete partial output file; preserve all source files intact |
| `TagNormalizationException` | Malformed tag prevents normalization | Skip game + log diagnostic; continue merge |
| `DeduplicationCollisionException` | Hash collision detected (extremely rare) | Fall back to full content comparison; log security event |

## 8. Testability Requirements

### 8.1 Required Test Fixtures
- `pgn_identical_games.pgn` (10 copies of same game for dedupe testing)
- `pgn_heterogeneous_sources.pgn` (files with different tag conventions)
- `pgn_unicode_names.pgn` (Cyrillic/Chinese player names for heap remapping)
- `pgn_malformed_partial.pgn` (files with corrupted games at boundaries)

### 8.2 Assertion Examples
```csharp
// Basic join: 2 files × 50 games → 100 games output
var result = service.Join(new JoinRequest(
    new[] { "file1.pgn", "file2.pgn" },
    "merged.pgn",
    new JoinOptions()
));

Assert.Equal(100, result.TotalGames);
Assert.Equal(0, result.DuplicatesSkipped);

// Deduplication: 2 files with 10 overlapping games → 90 unique games
var dedupeResult = service.Join(new JoinRequest(
    new[] { "file1.pgn", "file2_with_duplicates.pgn" },
    "deduped.pgn",
    new JoinOptions(Deduplicate: true, DedupMode: DeduplicationMode.StrictHash)
));

Assert.Equal(90, dedupeResult.TotalGames);
Assert.Equal(10, dedupeResult.DuplicatesSkipped);

// Index validation: Output index must be readable and match game count
var outputIndex = PgnBinaryIndex.OpenRead("deduped.pgn.pbi");
Assert.Equal(90, outputIndex.Header.GameCount);
```

## 9. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade shadow array during merge
- **Forward compatibility:** Reject v4+ indexes with clear error message ("Index format newer than supported version")
- **PGN standard compliance:** Output files must pass `pgn-extract -c` validation
- **Round-trip integrity:** Splitting then joining output files must reproduce byte-for-byte identical content (excluding index files)

## 10. Security Considerations

| Risk | Mitigation |
|------|------------|
| Path traversal via malicious filenames | Validate all source paths against application sandbox using `Path.GetFullPath()` canonicalization |
| Hash collision attacks (deliberate duplicates) | Use SHA256 (not MD5/SHA1); fall back to full content comparison on hash match |
| Resource exhaustion via pathological dedupe sets | Limit HashSet growth to 10M entries; throw `OutOfMemoryException` with diagnostic before system collapse |
| Unicode spoofing in player names | Apply Unicode security profile during string heap deduplication (NFKC + confusable detection) |