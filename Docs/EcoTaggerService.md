# EcoTaggerService.md

## Service Specification: EcoTaggerService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (ECO field population in GameRecord shadow array)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Annotate games with Encyclopaedia of Chess Openings (ECO) codes and opening names by matching move sequences against a reference opening tree. Operations must execute in streaming mode without loading entire games into memory. The service must handle transpositions, partial games, Chess960 variants, and integrate ECO assignments directly into the binary index for instant filtering.

## 2. Input Contract

```csharp
public record EcoTagRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    EcoDatabaseSource Database,     // Reference opening tree source (see Section 3)
    string? OutputFilePath = null,  // If null, update index in-place without rewriting PGN
    EcoTagOptions Options = null    // Configuration parameters (defaults in Section 2.2)
);

public record EcoDatabaseSource(
    EcoDatabaseFormat Format,       // BinaryTree | PgnFile | EmbeddedResource
    string Path,                    // Path to eco.bin or eco.pgn
    int? MaxDepth = 20              // Maximum ply depth to consider for matching
);

public enum EcoDatabaseFormat
{
    BinaryTree,     // Pre-compiled binary format (optimal performance)
    PgnFile,        // Standard PGN format (requires runtime compilation)
    EmbeddedResource // Built-in minimal database for offline operation
}

public record EcoTagOptions(
    bool UpdateExistingTags = false,        // Overwrite existing [ECO] tags
    bool RequireExactMatch = false,         // Only tag if full sequence matches (no partial)
    bool TagTranspositions = true,          // Tag games that transpose into opening
    bool SkipChess960 = true,               // Skip games with [Variant "Chess960"]
    bool SkipShortGames = true,             // Skip games with < 4 plies (insufficient for ECO)
    bool InjectOpeningName = true,          // Add [Opening ""] tag alongside [ECO ""]
    bool InjectVariation = true,            // Add [Variation ""] for sub-lines (e.g., "Najdorf")
    int MinConfidence = 80                  // Minimum match confidence (0-100) to apply tag
);
```

### 2.1 Default Options
```csharp
public static readonly EcoTagOptions Default = new(
    UpdateExistingTags: false,
    RequireExactMatch: false,
    TagTranspositions: true,
    SkipChess960: true,
    SkipShortGames: true,
    InjectOpeningName: true,
    InjectVariation: true,
    MinConfidence: 80
);
```

## 3. ECO Database Formats

### 3.1 Binary Tree Format (eco.bin) - Recommended
Pre-compiled binary structure optimized for O(1) per-ply traversal:

```
File Header (16 bytes)
  Magic: "ECODBv2" (8 bytes)
  Version: uint32 (4 bytes)
  NodeCount: uint32 (4 bytes)

Node Array (24 bytes per node)
  Offset 0-3:   Move (U32 compressed SAN)
  Offset 4-5:   ECO Category (char 'A'-'E')
  Offset 6-7:   ECO Number (uint16 0-99)
  Offset 8-11:  OpeningNameOffset (uint32 into string heap)
  Offset 12-15: VariationNameOffset (uint32 into string heap)
  Offset 16-19: FirstChildOffset (uint32, 0 = no children)
  Offset 20-23: SiblingOffset (uint32, 0 = no siblings)

String Heap (UTF-8 null-terminated strings)
  Contains all opening names and variation names
```

#### Move Compression Scheme
| Move Type | Compression Strategy | Example | Bytes |
|-----------|----------------------|---------|-------|
| Pawn push | File + rank (e4 → 0x0504) | e4 | 2 |
| Piece move | PieceID + to-square (Nf3 → 0x020503) | Nf3 | 3 |
| Capture | PieceID + from-square + to-square + 'x' flag | Nxe5 | 4 |
| Castling | Special opcode (O-O → 0xFFFF, O-O-O → 0xFFFE) | O-O | 2 |
| Promotion | Base move + piece flag (e8=Q → 0x050801) | e8=Q | 3 |

### 3.2 PGN Format (eco.pgn) - Legacy Support
Standard PGN file containing ECO reference games:
```
[Event "ECO Reference"]
[Site "Reference"]
[Date "????.??.??"]
[Round "?"]
[White "ECO"]
[Black "Opening"]
[Result "*"]
[ECO "B90"]
[Opening "Sicilian Defense"]
[Variation "Najdorf Variation"]
[SetUp "1"]
[FEN "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"]

1... c5 2. Nf3 d6 3. d4 cxd4 4. Nxd4 Nf6 5. Nc3 a6 *
```
- **Runtime compilation required:** Service must parse PGN and build in-memory tree before tagging
- **Performance penalty:** 5-10× slower than binary format; suitable only for initial database setup
- **Validation:** Reject malformed ECO codes outside A00-E99 range during compilation

## 4. Algorithm Specification

### 4.1 Two-Phase Execution Model

#### Phase 1: Database Loading & Validation
```csharp
// Load appropriate database format
IEcoTree ecoTree = request.Database.Format switch
{
    EcoDatabaseFormat.BinaryTree => EcoTreeBinaryLoader.Load(request.Database.Path),
    EcoDatabaseFormat.PgnFile => EcoTreePgnCompiler.Compile(request.Database.Path),
    EcoDatabaseFormat.EmbeddedResource => EcoTreeEmbedded.LoadDefault(),
    _ => throw new NotSupportedException()
};

// Validate database integrity
if (!ecoTree.Validate())
    throw new CorruptDatabaseException($"ECO database failed validation: {request.Database.Path}");

// Determine effective max depth (database may have intrinsic limit)
int effectiveMaxDepth = Math.Min(
    request.Database.MaxDepth ?? 20,
    ecoTree.MaxDepth
);
```

#### Phase 2: Game Processing Loop
```csharp
using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
ReadOnlySpan<GameRecord> records = index.GetGameRecords();
int totalGames = records.Length;
int gamesTagged = 0;
int gamesSkipped = 0;

// Strategy selection based on output mode
if (request.OutputFilePath == null)
{
    // In-place index update mode (optimal for large databases)
    UpdateIndexInPlace(request, records, ecoTree, effectiveMaxDepth, ref gamesTagged, ref gamesSkipped);
}
else
{
    // Physical file rewrite mode (preserves original file)
    RewriteFileWithTags(request, records, ecoTree, effectiveMaxDepth, ref gamesTagged, ref gamesSkipped);
}
```

### 4.2 Core Matching Algorithm
```csharp
private EcoMatchResult MatchGameAgainstTree(
    GameRecord record,
    Stream pgnStream,
    IEcoTree ecoTree,
    int maxDepth,
    EcoTagOptions options)
{
    // Early rejection filters
    if (options.SkipChess960 && record.HasFlag(GameFlags.Chess960))
        return EcoMatchResult.Skipped(Reason: "Chess960 variant");
    
    if (options.SkipShortGames && record.PlyCount < 4)
        return EcoMatchResult.Skipped(Reason: "Insufficient moves");
    
    if (!options.UpdateExistingTags && record.EcoCategory != 0)
        return EcoMatchResult.Skipped(Reason: "ECO tag already present");
    
    // Initialize board state from FEN if present, otherwise standard start position
    ChessBoard board = record.HasFen 
        ? ChessBoard.FromFen(record.StartFen) 
        : ChessBoard.StartPosition();
    
    // Traverse opening tree move-by-move
    EcoTreeNode currentNode = ecoTree.Root;
    int matchedPlies = 0;
    List<Move> transpositionPath = new(); // Track moves for transposition detection
    
    // Seek to first move in PGN file
    pgnStream.Seek(record.FileOffset + record.FirstMoveOffset, SeekOrigin.Begin);
    
    // Parse moves incrementally (avoid full game load)
    var moveParser = new IncrementalMoveParser(pgnStream, record.Length - record.FirstMoveOffset);
    
    while (matchedPlies < maxDepth && moveParser.MoveNext())
    {
        Move move = moveParser.Current;
        
        // Validate move legality before tree traversal
        if (!board.IsLegalMove(move))
            break; // Invalid move sequence - terminate matching
        
        // Apply move to board state
        board.ApplyMove(move);
        transpositionPath.Add(move);
        
        // Find matching child node in ECO tree
        EcoTreeNode nextNode = currentNode.FindChild(move);
        
        if (nextNode == null)
        {
            // No exact match - check for transposition if enabled
            if (options.TagTranspositions)
            {
                nextNode = ecoTree.FindTransposition(board.Fen, currentNode);
                if (nextNode != null)
                {
                    // Transposition detected - continue from transposed position
                    currentNode = nextNode;
                    matchedPlies++; // Count transposed position as match
                    continue;
                }
            }
            
            // No match found - terminate traversal
            break;
        }
        
        // Advance to matched node
        currentNode = nextNode;
        matchedPlies++;
    }
    
    // Calculate match confidence based on depth and completeness
    int confidence = CalculateConfidence(matchedPlies, maxDepth, currentNode.IsTerminal);
    
    if (confidence < options.MinConfidence)
        return EcoMatchResult.Rejected(Reason: $"Confidence {confidence} < threshold {options.MinConfidence}");
    
    // Return successful match with ECO assignment
    return EcoMatchResult.Matched(
        ecoCode: $"{(char)currentNode.EcoCategory}{currentNode.EcoNumber:D2}",
        openingName: currentNode.OpeningName,
        variationName: currentNode.VariationName,
        matchedPlies: matchedPlies,
        confidence: confidence
    );
}
```

#### Confidence Calculation Formula
```
BaseConfidence = (MatchedPlies / MaxDepth) × 100

TerminalBonus = IsTerminalNode ? +15 : 0
TranspositionPenalty = UsedTransposition ? -10 : 0
ShortGamePenalty = (MatchedPlies < 8) ? -5 : 0

FinalConfidence = Clamp(BaseConfidence + TerminalBonus + TranspositionPenalty + ShortGamePenalty, 0, 100)
```

### 4.3 In-Place Index Update Strategy (OutputFilePath = null)
```csharp
private void UpdateIndexInPlace(
    EcoTagRequest request,
    ReadOnlySpan<GameRecord> records,
    IEcoTree ecoTree,
    int maxDepth,
    ref int gamesTagged,
    ref int gamesSkipped)
{
    // Memory-map the index file for direct mutation
    using var mmf = MemoryMappedFile.CreateFromFile(
        request.SourceFilePath + ".pbi",
        FileMode.Open,
        mapName: null,
        capacity: 0,
        MemoryMappedFileAccess.ReadWrite
    );
    
    using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
    
    // Process games in batches to enable progress reporting + cancellation
    const int batchSize = 1000;
    for (int batchStart = 0; batchStart < records.Length; batchStart += batchSize)
    {
        int batchEnd = Math.Min(batchStart + batchSize, records.Length);
        
        for (int i = batchStart; i < batchEnd; i++)
        {
            ref readonly var record = ref records[i];
            
            // Skip if already tagged and not forcing update
            if (!request.Options.UpdateExistingTags && record.EcoCategory != 0)
            {
                gamesSkipped++;
                continue;
            }
            
            // Match against ECO tree (requires PGN stream access)
            using var pgnStream = File.OpenRead(request.SourceFilePath);
            var result = MatchGameAgainstTree(record, pgnStream, ecoTree, maxDepth, request.Options);
            
            if (result.IsMatch)
            {
                // Update GameRecord fields directly in memory-mapped view
                int recordOffset = IndexHeader.Size + (i * GameRecord.Size);
                
                accessor.Write(recordOffset + GameRecord.EcoCategoryOffset, 
                              (byte)result.EcoCategoryChar);
                accessor.Write(recordOffset + GameRecord.EcoNumberOffset, 
                              (ushort)result.EcoNumber);
                
                // Update string heap references for opening name
                if (request.Options.InjectOpeningName && !string.IsNullOrEmpty(result.OpeningName))
                {
                    int nameId = index.StringHeap.GetInternedId(result.OpeningName);
                    accessor.Write(recordOffset + GameRecord.OpeningNameIdOffset, nameId);
                }
                
                gamesTagged++;
            }
            else
            {
                gamesSkipped++;
            }
        }
        
        // Report progress every batch
        double percent = (double)(batchStart + batchSize) / records.Length * 100;
        OnProgress?.Invoke(new EcoTagProgress(percent, gamesTagged, gamesSkipped));
        
        // Check for cancellation
        cancellationToken.ThrowIfCancellationRequested();
    }
    
    // Update index header checksum to reflect mutation
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

### 4.4 Physical Rewrite Strategy (OutputFilePath specified)
```csharp
private void RewriteFileWithTags(
    EcoTagRequest request,
    ReadOnlySpan<GameRecord> records,
    IEcoTree ecoTree,
    int maxDepth,
    ref int gamesTagged,
    ref int gamesSkipped)
{
    using var sourceStream = File.OpenRead(request.SourceFilePath);
    using var destStream = File.Create(request.OutputFilePath);
    BinaryWriter writer = new(destStream);
    
    List<GameRecord> outputRecords = new(records.Length);
    uint currentOffset = 0;
    
    for (int i = 0; i < records.Length; i++)
    {
        ref readonly var record = ref records[i];
        sourceStream.Seek(record.FileOffset, SeekOrigin.Begin);
        
        // Read raw game bytes
        byte[] gameBytes = new byte[record.Length];
        ReadExactly(sourceStream, gameBytes);
        
        // Match against ECO tree
        using var tempStream = new MemoryStream(gameBytes);
        var result = MatchGameAgainstTree(record, tempStream, ecoTree, maxDepth, request.Options);
        
        if (result.IsMatch)
        {
            // Inject ECO tags into game text
            gameBytes = InjectEcoTags(
                gameBytes,
                result.EcoCode,
                request.Options.InjectOpeningName ? result.OpeningName : null,
                request.Options.InjectVariation ? result.VariationName : null
            );
            
            gamesTagged++;
        }
        else
        {
            gamesSkipped++;
        }
        
        // Write to output stream
        writer.Write(gameBytes);
        writer.Write(new byte[] { 0x0A, 0x0A }); // PGN game separator
        
        // Build rewritten GameRecord
        var rewritten = record;
        rewritten.FileOffset = currentOffset;
        rewritten.Length = (uint)(gameBytes.Length + 2);
        
        if (result.IsMatch)
        {
            rewritten.EcoCategory = (byte)result.EcoCategoryChar;
            rewritten.EcoNumber = (ushort)result.EcoNumber;
            rewritten.OpeningNameId = request.Options.InjectOpeningName && !string.IsNullOrEmpty(result.OpeningName)
                ? outputStringHeap.GetInternedId(result.OpeningName)
                : 0;
        }
        
        outputRecords.Add(rewritten);
        currentOffset += rewritten.Length;
    }
    
    // Generate new index if requested
    if (request.PreserveIndex)
    {
        PgnBinaryIndexBuilder.BuildFromRecords(
            request.OutputFilePath + ".pbi",
            outputRecords.ToArray(),
            outputStringHeap
        );
    }
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Game with `[ECO "A00"]` already present | Skip unless `UpdateExistingTags=true`; log diagnostic if mismatched with detected opening |
| Transposition detected (e.g., English → Queen's Gambit) | Tag with destination opening if `TagTranspositions=true`; confidence reduced by 10% |
| Chess960 game with valid opening sequence | Skip if `SkipChess960=true`; otherwise attempt matching from FEN start position |
| Partial game ending mid-opening (e.g., 1.e4 c5 2.Nf3 *) | Tag with partial match if confidence ≥ threshold; variation field left empty |
| Ambiguous ECO assignment (multiple valid classifications) | Select deepest node in tree; log ambiguity to diagnostics stream |
| Malformed move text preventing parsing | Skip game + log offset; continue processing remaining games |
| ECO database missing critical openings | Tag as "A00" (Irregular) with confidence 50%; log missing opening to diagnostics |

## 6. Performance Characteristics

### 6.1 Time Complexity
| Operation | Complexity | Notes |
|-----------|------------|-------|
| Binary tree load | O(1) | Memory-mapped file access; ~50ms for 500KB eco.bin |
| PGN database compile | O(N × M) | N = ECO lines, M = avg moves per line; ~2s for full ECO |
| Per-game matching | O(D) | D = matched plies (typically 8-15); constant-time per ply |
| Full database tagging | O(G × D) | G = total games; D = avg matched depth |

### 6.2 Memory Footprint
| Scenario | Peak Memory | Strategy |
|----------|-------------|----------|
| Binary tree in memory | ~500 KB | Entire eco.bin mapped readonly |
| PGN compilation | ~5 MB | Intermediate tree structures during compilation |
| 1M games tagging | < 1 MB | Streaming parser; no game data retained |
| String heap expansion | O(U) | U = unique opening names (typically < 500) |

### 6.3 Real-World Benchmarks (Intel i7-12700K, NVMe SSD)
| Database Size | Binary Tree Load | 100K Games | 1M Games | 10M Games |
|---------------|------------------|------------|----------|-----------|
| Standard ECO (500KB) | 48 ms | 1.2 s | 12 s | 2m 5s |
| Extended ECO (2MB) | 62 ms | 1.8 s | 18 s | 3m 10s |
| PGN compile first run | 1.9 s | +1.2 s | +12 s | +2m 5s |

## 7. Binary Index Integration Points

### 7.1 Required GameRecord Fields for ECO Storage
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... other fields ...
    public byte EcoCategory;        // Offset 25: ASCII 'A'-'E' (0 = untagged)
    public byte EcoNumber;          // Offset 26: 0-99 (0 = untagged)
    public short OpeningNameId;     // Offset 27-28: Signed short (-1 = missing) into StringHeap
    public byte VariationNameId;    // Offset 29: Unsigned byte (0 = missing) into StringHeap
    // ... remaining fields ...
}
```

### 7.2 String Heap Integration
- **Deduplication:** Opening names interned during tagging to minimize heap size
- **Reference counting:** Not required - heap is immutable after index creation
- **Fallback behavior:** If heap full (2^15 unique names), truncate least-frequent names

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `EcoDatabaseNotFoundException` | Specified eco.bin/eco.pgn missing | Fail fast before any game processing |
| `CorruptDatabaseException` | Database checksum validation failed | Reject database; require redownload/recompilation |
| `UnsupportedEcoFormatException` | Database version newer than supported | Clear error message with version compatibility matrix |
| `PartialWriteException` | Disk full during physical rewrite | Delete partial output; preserve source integrity |
| `MoveParseException` | Unparseable move text in source game | Skip game + log diagnostic; continue processing |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `eco_standard.bin` (500KB pre-compiled standard ECO database)
- `pgn_eco_testset.pgn` (100 games with known ECO classifications)
- `pgn_transpositions.pgn` (games demonstrating transposition behavior)
- `pgn_chess960_openings.pgn` (Chess960 games with FEN start positions)
- `pgn_malformed_moves.pgn` (games with edge-case move notation)

### 9.2 Assertion Examples
```csharp
// Verify Sicilian Najdorf classification
var result = service.Tag(new EcoTagRequest(
    "sicilian_games.pgn",
    new EcoDatabaseSource(EcoDatabaseFormat.BinaryTree, "eco_standard.bin")
));

var index = PgnBinaryIndex.OpenRead("sicilian_games.pgn.pbi");
var najdorfGames = index.GetGameRecords()
    .Where(r => r.EcoCategory == (byte)'B' && r.EcoNumber == 90)
    .ToList();

Assert.True(najdorfGames.Count > 0);
Assert.All(najdorfGames, game => {
    string opening = index.StringHeap.GetString(game.OpeningNameId);
    Assert.Contains("Sicilian", opening, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Najdorf", index.StringHeap.GetString(game.VariationNameId), 
                   StringComparison.OrdinalIgnoreCase);
});

// Verify transposition handling (English → Queen's Gambit)
var transposedGame = index.GetGameRecord(42); // Known transposition example
Assert.Equal((byte)'D', transposedGame.EcoCategory);
Assert.Equal(30, transposedGame.EcoNumber); // D30 = Queen's Gambit
```

## 10. Versioning & Compatibility

- **ECO Database Versions:**
  - v1: Legacy format (unsupported in V5)
  - v2: Current format with 24-byte nodes + string heap (V5 native)
  - v3: Future format with position hashing (forward compatible via loader)
- **Backward compatibility:** Must read v2 `.pbi` format; auto-upgrade ECO fields during tagging
- **Forward compatibility:** Reject v4+ indexes with clear error message
- **PGN standard compliance:** Injected tags must conform to PGN Spec v1.0

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Malicious ECO database causing DoS | Validate database checksum before loading; enforce max node count (1M nodes) |
| Path traversal via database path | Canonicalize path using `Path.GetFullPath()`; restrict to application data directory |
| Unicode spoofing in opening names | Apply Unicode security profile during string heap interning (NFKC + confusable detection) |
| Resource exhaustion via pathological move sequences | Limit move parser recursion depth to 200 plies; throw `InvalidGameException` beyond limit |