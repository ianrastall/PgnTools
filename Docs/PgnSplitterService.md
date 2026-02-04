# PgnSplitterService.md

## Service Specification: PgnSplitterService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (shadow array partitioning)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Partition a source PGN database into multiple smaller output files based on partitioning criteria while preserving binary index coherence. All operations must execute in streaming mode without loading entire source file into memory. Output files must be immediately usable with companion `.pbi` indexes if requested.

## 2. Input Contract

```csharp
public record SplitRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    SplitMode Mode,                 // Partitioning strategy (see Section 3)
    SplitOptions Options,           // Configuration parameters
    string TargetDirectory,         // Output directory (must exist and be writable)
    string? FilePrefix = null,      // Optional prefix for output files (default: "split")
    bool PreserveIndex = true       // Generate .pbi for each output file
);

public enum SplitMode
{
    GameCount,      // Fixed number of games per file
    Player,         // Partition by player name (White or Black)
    Event,          // Partition by tournament/event name
    Round,          // Partition by round number within events
    EcoCode,        // Partition by ECO opening code (A00, B00, etc.)
    DateYear,       // Partition by year component of game date
    DateMonth       // Partition by year-month (YYYY-MM)
}

public record SplitOptions(
    int? GamesPerFile = null,               // Required for GameCount mode
    PlayerRole? PlayerRole = null,          // White/Black/Both for Player mode
    bool SanitizeFilenames = true,          // Replace invalid chars in output filenames
    bool IncludeHeadersInEveryFile = true,  // Repeat Seven Tag Roster in each output file
    bool DeduplicateWithinPartition = false // Remove duplicates within each partition only
);
```

### 2.1 PlayerRole Enumeration
```csharp
public enum PlayerRole
{
    White,   // Partition by White player name only
    Black,   // Partition by Black player name only
    Both     // Create separate partitions for each player appearance (White OR Black)
}
```
- **Both semantics:** A game with Carlsen (White) vs Nakamura (Black) appears in *both* `Carlsen.pgn` and `Nakamura.pgn`
- **Default behavior:** `PlayerRole.White` when unspecified in Player mode

## 3. Partitioning Algorithms by Mode

### 3.1 GameCount Mode (Fixed-Size Partitioning)
**Objective:** Create output files containing exactly `GamesPerFile` games (final file may contain fewer).

#### Algorithm
```csharp
// Phase 1: Validate and prepare
ValidateGameCountMode(request); // Throws if GamesPerFile <= 0 or null

using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
uint totalGames = index.Header.GameCount;
int partitions = (int)Math.Ceiling((double)totalGames / request.Options.GamesPerFile.Value);

// Phase 2: Partition assignment without I/O
Span<PartitionAssignment> assignments = stackalloc PartitionAssignment[(int)totalGames];
for (uint i = 0; i < totalGames; i++)
{
    int partitionIndex = (int)(i / request.Options.GamesPerFile.Value);
    assignments[i] = new PartitionAssignment(partitionIndex, index.GetGameRecord(i));
}

// Phase 3: Streaming write per partition
for (int p = 0; p < partitions; p++)
{
    string outputFile = Path.Combine(
        request.TargetDirectory,
        $"{request.FilePrefix ?? "split"}_{p + 1:D4}.pgn"
    );
    
    using var outputStream = File.Create(outputFile);
    BinaryWriter writer = new(outputStream);
    
    // Write games assigned to this partition
    foreach (var assignment in assignments)
    {
        if (assignment.PartitionIndex != p) continue;
        
        byte[] gameBytes = ReadGameBytes(
            request.SourceFilePath,
            assignment.Record.FileOffset,
            assignment.Record.Length
        );
        
        writer.Write(gameBytes);
        writer.Write(new byte[] { 0x0A, 0x0A }); // PGN game separator (\n\n)
    }
    
    // Generate index if requested
    if (request.PreserveIndex)
    {
        GenerateIndexForPartition(outputFile, assignments.Where(a => a.PartitionIndex == p));
    }
}
```

#### Edge Cases
| Scenario | Handling |
|----------|----------|
| Source has 0 games | Create single empty file `split_0001.pgn` with valid (empty) index |
| GamesPerFile = 1 | Create N files each containing exactly one game |
| Final partition underfilled | Accept partial partition; do not pad with dummy games |
| Source file corruption mid-partition | Abort entire operation; delete partially written outputs; throw `CorruptSourceException` |

### 3.2 Player Mode (Dynamic Partitioning by Name)
**Objective:** Group games by player name(s) into separate files named after the player.

#### Algorithm
```csharp
// Phase 1: Discover unique player names via index scan
Dictionary<string, List<GameRecord>> partitions = new(StringComparer.OrdinalIgnoreCase);

using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
foreach (var record in index.GetGameRecords())
{
    string[] playerNames = ExtractPlayerNames(record, request.Options.PlayerRole.Value);
    
    foreach (string playerName in playerNames)
    {
        string safeName = SanitizePlayerName(playerName, request.Options.SanitizeFilenames);
        if (!partitions.ContainsKey(safeName))
            partitions[safeName] = new List<GameRecord>();
        
        partitions[safeName].Add(record);
    }
}

// Phase 2: Write each partition to separate file
foreach (var (playerName, gameRecords) in partitions)
{
    string safeFilename = $"{request.FilePrefix ?? "player"}_{playerName}.pgn";
    string outputFile = Path.Combine(request.TargetDirectory, safeFilename);
    
    using var outputStream = File.Create(outputFile);
    BinaryWriter writer = new(outputStream);
    
    foreach (var record in gameRecords)
    {
        byte[] gameBytes = ReadGameBytes(
            request.SourceFilePath,
            record.FileOffset,
            record.Length
        );
        
        // Inject player context tag if missing (optional enhancement)
        if (request.Options.IncludeHeadersInEveryFile)
        {
            gameBytes = EnsureTagPresence(gameBytes, "PlayerContext", playerName);
        }
        
        writer.Write(gameBytes);
        writer.Write(new byte[] { 0x0A, 0x0A });
    }
    
    if (request.PreserveIndex)
    {
        GenerateIndexForPartition(outputFile, gameRecords);
    }
}
```

#### Player Name Extraction Rules
| PlayerRole | Extraction Logic | Example Input | Output Names |
|------------|------------------|---------------|--------------|
| `White` | `[White "Carlsen, Magnus"]` | Carlsen, Magnus | `["Carlsen Magnus"]` |
| `Black` | `[Black "Nakamura, Hikaru"]` | Nakamura, Hikaru | `["Nakamura Hikaru"]` |
| `Both` | Both White + Black tags | Carlsen (W) vs Nakamura (B) | `["Carlsen Magnus", "Nakamura Hikaru"]` |

#### Filename Sanitization Rules
```csharp
private static string SanitizePlayerName(string name, bool aggressive = true)
{
    // Step 1: Unicode normalization
    string normalized = name.Normalize(NormalizationForm.FormKC);
    
    // Step 2: Remove diacritics (optional but recommended)
    if (aggressive)
        normalized = RemoveDiacritics(normalized);
    
    // Step 3: Replace invalid filesystem characters
    foreach (char invalid in Path.GetInvalidFileNameChars())
    {
        normalized = normalized.Replace(invalid, '_');
    }
    
    // Step 4: Collapse repeated underscores/spaces
    normalized = Regex.Replace(normalized, @"[_\s]+", "_");
    
    // Step 5: Trim leading/trailing underscores
    normalized = normalized.Trim('_');
    
    // Step 6: Enforce max length (filesystem limits)
    if (normalized.Length > 64)
        normalized = normalized.Substring(0, 64);
    
    return normalized;
}
```
- **Example:** `"Carlsen, Magnus"` → `"Carlsen_Magnus"`
- **Example:** `"Nakamura, Hikaru (2780)"` → `"Nakamura_Hikaru_2780"` (aggressive=false) or `"Nakamura_Hikaru"` (aggressive=true)

#### Edge Cases
| Scenario | Handling |
|----------|----------|
| Player name contains path separators (`/`, `\`) | Replace with underscore during sanitization |
| Multiple players map to same sanitized name | Append numeric suffix: `Carlsen_Magnus`, `Carlsen_Magnus_2` |
| Game missing White/Black tag | Skip game + log warning; do not assign to any partition |
| Unicode names (Cyrillic, Chinese) | Preserve original in game content; sanitize only for filename |

### 3.3 Event Mode
Identical algorithm to Player Mode but partitions by `[Event "Tata Steel Masters 2023"]` tag value. Sanitization applies to event names with additional rules:
- Replace year suffixes with compact form: `"Tata Steel Masters 2023"` → `"Tata_Steel_Masters_2023"`
- Collapse whitespace aggressively (tournament names often contain irregular spacing)

### 3.4 Round Mode
Partition by `[Round "5"]` or `[Round "5.1"]` (armageddon). Special handling:
- Parse round value as decimal: `"5.1"` → partition key `"5_1"`
- Games missing Round tag assigned to partition `"unknown_round"`
- Within multi-round events, maintain event context in output filename: `Tata_Steel_Round_5.pgn`

### 3.5 EcoCode Mode
Partition by ECO code extracted from `[ECO "B90"]` tag or inferred via opening tree:
- **Missing ECO tags:** If `AnalyzeMissingEco=true` in options, run lightweight ECO matcher before partitioning
- **ECO ranges:** Group by major classification (A, B, C, D, E) if `GroupByMajor=true`
- Output filenames: `eco_A00-A99.pgn`, `eco_B00-B99.pgn`, etc.

### 3.6 Date-Based Modes (Year/Month)
Partition by date components with lenient parsing:
```csharp
private static string ExtractYearPartition(GameRecord record)
{
    if (record.PackedDate == 0) return "unknown_year";
    int year = (int)(record.PackedDate / 10000); // YYYYMMDD → YYYY
    return year.ToString();
}

private static string ExtractMonthPartition(GameRecord record)
{
    if (record.PackedDate == 0) return "unknown_month";
    int year = (int)(record.PackedDate / 10000);
    int month = (int)((record.PackedDate % 10000) / 100);
    return $"{year:D4}-{month:D2}";
}
```
- Output filenames: `2023.pgn`, `2023-01.pgn`, etc.
- Games with partial dates (`2023.??.??`) assigned to year partition but excluded from month partitions

## 4. Binary Index Integration

### 4.1 Index Preservation Strategy
When `PreserveIndex=true`, each output file receives a valid `.pbi` containing only games assigned to that partition:

```csharp
private void GenerateIndexForPartition(
    string pgnFilePath,
    IEnumerable<GameRecord> recordsInPartition)
{
    // Build new index header
    var header = new IndexHeader
    {
        Magic = "PGNIDXv3",
        Version = 3,
        GameCount = (uint)recordsInPartition.Count()
    };
    
    // Rewrite file offsets relative to new file start
    List<GameRecord> rewrittenRecords = new();
    uint currentOffset = 0;
    
    foreach (var originalRecord in recordsInPartition)
    {
        var rewritten = originalRecord;
        rewritten.FileOffset = currentOffset; // Reset offset sequence
        rewritten.Length = originalRecord.Length; // Preserve original length
        
        rewrittenRecords.Add(rewritten);
        currentOffset += originalRecord.Length + 2; // +2 for \n\n separator
    }
    
    // Write index file
    string pbiPath = pgnFilePath + ".pbi";
    using var indexStream = File.Create(pbiPath);
    WriteIndexHeader(indexStream, header);
    WriteGameRecords(indexStream, rewrittenRecords);
    WriteStringHeap(indexStream, rewrittenRecords); // Deduplicated player/event names
}
```

### 4.2 Index-Aware Optimizations
- **Offset rewriting:** Game records maintain original lengths but receive new sequential offsets in output file
- **String heap deduplication:** Rebuild string heap per partition to minimize index size
- **Checksum validation:** Verify source index integrity before partitioning begins
- **Partial failure recovery:** If index generation fails mid-operation, delete corrupted `.pbi` but preserve valid `.pgn`

## 5. Performance Characteristics

### 5.1 Time Complexity
| Mode | Complexity | Notes |
|------|------------|-------|
| GameCount | O(N) | Single sequential scan + partitioned writes |
| Player/Event | O(N + P log P) | N = games, P = unique partitions (dictionary lookup) |
| EcoCode | O(N) if ECO indexed; O(N × M) if requires move parsing | M = avg moves for ECO matching |

### 5.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| GameCount (1M games, 10k per file) | < 4 MB | Store partition assignments as byte array (1 byte per game for 256 partitions) |
| Player mode (10k unique players) | ~200 KB | Dictionary of player names → partition IDs (not full game data) |
| All modes | < 64 KB per I/O buffer | Reuse single 64KB buffer via ArrayPool for game extraction |

### 5.3 I/O Patterns
- **Read pattern:** Sequential scan of `.pbi` + random access to `.pgn` for game extraction
- **Write pattern:** Sequential writes to multiple output files (interleaved writes minimized via partition buffering)
- **Buffering strategy:** Accumulate 1MB of game data per partition before flushing to reduce file open/close overhead

## 6. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `SplitModeConfigurationException` | Required parameter missing (e.g., `GamesPerFile=null` in GameCount mode) | Fail fast before any I/O begins |
| `TargetDirectoryNotFoundException` | Output directory does not exist | Throw immediately; do not attempt creation (caller responsibility) |
| `InsufficientDiskSpaceException` | Free space < estimated output size | Pre-check via `DriveInfo`; fail before operation starts |
| `PartialWriteException` | Disk full mid-operation | Delete all partially written outputs; preserve source integrity |
| `IndexVersionMismatchException` | Source `.pbi` format incompatible | Require explicit index rebuild before splitting |

## 7. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Source file has companion `.pbi` but index corrupt | Validate index checksum before partitioning; throw `CorruptIndexException` |
| Partition would create > 10,000 files (e.g., Player mode with massive database) | Warn via callback; require explicit `AllowLargePartitionCount=true` flag |
| Output filename exceeds filesystem limits (260 chars on Windows) | Truncate with hash suffix: `very_long_name..._a1b2c3.pgn` |
| Two partitions map to identical sanitized filename | Append numeric disambiguator: `Carlsen_Magnus.pgn`, `Carlsen_Magnus_2.pgn` |
| Source contains games with binary/gibberish content | Skip malformed games + log offset; continue partitioning valid games |
| Unicode normalization changes string length | Re-validate filename length after each sanitization step |

## 8. Testability Requirements

### 8.1 Required Test Fixtures
- `pgn_100games_varied.pgn` + `.pbi` (mixed players/events/dates)
- `pgn_unicode_players.pgn` (Cyrillic, Chinese, Arabic names)
- `pgn_malformed_partial.pgn` (missing tags, irregular formatting)
- `pgn_1million_games.pgn` + `.pbi` (performance/stress test)

### 8.2 Assertion Examples
```csharp
// GameCount mode: 100 games → 10 files of 10 games each
var result = service.Split(new SplitRequest(
    "100games.pgn",
    SplitMode.GameCount,
    new SplitOptions(GamesPerFile: 10),
    "output/",
    "test"
));

Assert.Equal(10, result.OutputFiles.Count);
Assert.All(result.OutputFiles, file => {
    var index = PgnBinaryIndex.OpenRead(file + ".pbi");
    Assert.Equal(10, index.Header.GameCount);
});

// Player mode: Carlsen appears in 15 games → carlsen_magnus.pgn has 15 games
var carlsenFile = result.OutputFiles
    .FirstOrDefault(f => Path.GetFileName(f).Contains("carlsen", StringComparison.OrdinalIgnoreCase));
Assert.NotNull(carlsenFile);
Assert.Equal(15, PgnBinaryIndex.OpenRead(carlsenFile + ".pbi").Header.GameCount);
```

## 9. Versioning & Compatibility

- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade shadow array during partitioning
- **Forward compatibility:** Reject v4+ indexes with clear error message
- **PGN standard compliance:** Output files must pass `pgn-extract` validation
- **Round-trip integrity:** Splitting then joining output files must reproduce original byte-for-byte (excluding index files)

## 10. Security Considerations

| Risk | Mitigation |
|------|------------|
| Path traversal via malicious event names (`../../etc/passwd`) | Aggressive filename sanitization + validation against `Path.GetInvalidPathChars()` |
| Zip slip during archive extraction (if splitting compressed sources) | Validate all output paths remain within `TargetDirectory` using `Path.GetFullPath()` canonicalization |
| Resource exhaustion via pathological partitioning (1 game → 1 file × 1M games) | Enforce maximum partition count (default: 10,000); require explicit override flag |
| Unicode spoofing attacks (homoglyphs in player names) | Apply Unicode security profile (NFKC + confusable detection) during sanitization |