# GameDeleterService.md

## Service Specification: GameDeleterService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (in-place flag mutation)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Mark games as deleted within PGN databases without physically removing content or rewriting the entire file, enabling instantaneous deletion with deferred space reclamation via PgnCompactorService. Operations must execute with O(1) index updates per game, support batch deletions, soft/hard delete modes, and maintain transactional integrity for undo/redo operations. The service must handle conflict resolution for concurrent deletions and preserve referential integrity for position indexes.

## 2. Input Contract

```csharp
public record DeleteRequest(
    string DatabasePath,            // Path to .pgn file (must have companion .pbi)
    DeleteTarget Target,            // Games to delete (see Section 3)
    DeleteOptions Options = null    // Configuration parameters (defaults in Section 2.2)
);

public record DeleteTarget(
    DeleteMode Mode,                // SingleGame | GameRange | FilteredGames | AllGames
    int? GameIndex = null,          // Required for SingleGame mode
    int? StartIndex = null,         // Required for GameRange mode
    int? EndIndex = null,           // Required for GameRange mode
    FilterCriteria? Filter = null,  // Required for FilteredGames mode
    IReadOnlyList<int>? GameIndices = null // Explicit indices (alternative to range/filter)
);

public enum DeleteMode
{
    SingleGame,     // Delete one game by index
    GameRange,      // Delete contiguous range [StartIndex, EndIndex]
    FilteredGames,  // Delete games matching filter criteria
    AllGames        // Delete entire database content (metadata preserved)
}

public record DeleteOptions(
    bool SoftDelete = true,                 // Mark deleted vs. physical removal
    bool PreserveForUndo = true,            // Maintain undo snapshot for undelete
    bool CascadePositionIndex = true,       // Remove position references for soft-deleted games
    bool GenerateDeletionReport = true,     // Return detailed report of deleted games
    ConflictResolutionMode ConflictMode = ConflictResolutionMode.SkipExisting, // SkipExisting | Overwrite | Abort
    TimeSpan UndoRetentionPeriod = default  // How long to retain undo snapshots (default: 7 days)
);
```

### 2.1 Default Options
```csharp
public static readonly DeleteOptions Default = new(
    SoftDelete: true,
    PreserveForUndo: true,
    CascadePositionIndex: true,
    GenerateDeletionReport: true,
    ConflictMode: ConflictResolutionMode.SkipExisting,
    UndoRetentionPeriod: TimeSpan.FromDays(7)
);
```

## 3. Deletion Semantics & Strategies

### 3.1 Soft Delete (Default Strategy)
Mark games as logically deleted while preserving physical content for undelete operations:

```csharp
// Before deletion (index record):
// FileOffset: 12345, Length: 1842, Flags: 0b00000000, IsDeleted: false

var request = new DeleteRequest(
    "database.pgn",
    new DeleteTarget(DeleteMode.SingleGame, GameIndex: 42),
    new DeleteOptions(SoftDelete: true)
);

// After soft delete (index record only - PGN file unchanged):
// FileOffset: 12345, Length: 1842, Flags: 0b10000000 (bit 31 = IsDeleted), IsDeleted: true
```

**Critical behaviors:**
- PGN file content remains physically intact at original offset
- Binary index `GameRecord` marked with `IsDeleted` flag (bit 31 of Flags field)
- Game excluded from all queries/filters by default (transparent to application layer)
- Position hash table entries marked as inactive but preserved for undelete
- Undo snapshot created containing full game bytes + original index record

### 3.2 Hard Delete (Physical Removal)
Immediately reclaim disk space by physically removing game content:

```csharp
var hardDeleteRequest = new DeleteRequest(
    "database.pgn",
    new DeleteTarget(DeleteMode.SingleGame, GameIndex: 42),
    new DeleteOptions(SoftDelete: false) // Physical removal
);
```

**Algorithm for single-game hard delete:**
```csharp
private async Task HardDeleteSingleGameAsync(
    string pgnPath,
    GameRecord record,
    CancellationToken ct)
{
    // Step 1: Read subsequent content after deleted game
    using var fs = File.Open(pgnPath, FileMode.Open, FileAccess.ReadWrite);
    long contentAfterOffset = record.FileOffset + record.Length + 2; // +2 for \n\n separator
    long contentAfterLength = fs.Length - contentAfterOffset;
    
    if (contentAfterLength <= 0)
    {
        // Game is last in file - simple truncate
        fs.SetLength(record.FileOffset);
        return;
    }
    
    // Step 2: Shift subsequent content backward to overwrite deleted game
    byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
    long readPosition = contentAfterOffset;
    long writePosition = record.FileOffset;
    
    while (contentAfterLength > 0)
    {
        ct.ThrowIfCancellationRequested();
        
        int toRead = (int)Math.Min(contentAfterLength, buffer.Length);
        int bytesRead = await fs.ReadAsync(buffer, 0, toRead, ct);
        
        fs.Seek(writePosition, SeekOrigin.Begin);
        await fs.WriteAsync(buffer, 0, bytesRead, ct);
        
        readPosition += bytesRead;
        writePosition += bytesRead;
        contentAfterLength -= bytesRead;
    }
    
    ArrayPool<byte>.Shared.Return(buffer);
    
    // Step 3: Truncate file to new length
    fs.SetLength(writePosition);
    
    // Step 4: Update index offsets for all subsequent games (critical!)
    await AdjustSubsequentGameOffsetsAsync(
        pgnPath + ".pbi",
        record.GameIndex + 1,
        -(int)(record.Length + 2), // Negative delta = backward shift
        ct
    );
}
```

### 3.3 Batch Deletion Strategies

| Strategy | Use Case | Performance | Space Reclamation |
|----------|----------|-------------|-------------------|
| **Individual Soft Deletes** | Interactive UI deletion | O(N) index updates | Deferred (requires compaction) |
| **Range Soft Delete** | Delete tournament round | O(1) range flag update | Deferred |
| **Filtered Soft Delete** | Remove all bullet games | O(N) scan + O(M) flag updates | Deferred |
| **Batch Hard Delete** | Purge entire database section | O(N + S) where S = shifted content | Immediate |
| **Mark-Compact** | Large-scale deletion (>10% of DB) | O(N) mark + O(S) compact | Immediate (via compaction) |

```csharp
private async Task DeleteByFilterAsync(
    DeleteRequest request,
    PgnBinaryIndex index,
    CancellationToken ct)
{
    // Step 1: Identify games matching filter
    var matchingIndices = await IdentifyMatchingGamesAsync(
        index,
        request.Target.Filter!,
        ct
    );
    
    if (matchingIndices.Count == 0)
        return; // Nothing to delete
    
    // Step 2: Apply deletion strategy based on count threshold
    if (request.Options.SoftDelete || matchingIndices.Count < 1000)
    {
        // Soft delete individual games (fast path)
        await SoftDeleteGamesAsync(
            request.DatabasePath,
            index,
            matchingIndices,
            request.Options,
            ct
        );
    }
    else
    {
        // Large deletion - use mark-compact strategy
        await MarkAndCompactAsync(
            request.DatabasePath,
            index,
            matchingIndices,
            request.Options,
            ct
        );
    }
}
```

## 4. Algorithm Specification

### 4.1 Soft Delete Pipeline (O(1) per game)
```csharp
public async Task<DeleteReport> DeleteAsync(DeleteRequest request, CancellationToken ct)
{
    // Phase 1: Load index and validate target
    using var index = PgnBinaryIndex.OpenRead(request.DatabasePath + ".pbi");
    var gameIndices = ResolveDeleteTarget(request.Target, index);
    
    if (gameIndices.Count == 0)
        return DeleteReport.Empty("No games matched deletion target");
    
    // Phase 2: Create undo snapshots if requested
    List<UndoSnapshot> undoSnapshots = new();
    if (request.Options.PreserveForUndo)
    {
        undoSnapshots = await CreateUndoSnapshotsAsync(
            request.DatabasePath,
            index,
            gameIndices,
            request.Options.UndoRetentionPeriod,
            ct
        );
    }
    
    // Phase 3: Execute deletion based on strategy
    DeleteReport report;
    if (request.Options.SoftDelete)
    {
        report = await SoftDeleteGamesAsync(
            request.DatabasePath,
            index,
            gameIndices,
            request.Options,
            undoSnapshots,
            ct
        );
    }
    else
    {
        report = await HardDeleteGamesAsync(
            request.DatabasePath,
            index,
            gameIndices,
            request.Options,
            undoSnapshots,
            ct
        );
    }
    
    // Phase 4: Cascade to dependent indexes if requested
    if (request.Options.CascadePositionIndex && index.HasPositionIndex)
    {
        await CascadePositionIndexDeletionAsync(
            request.DatabasePath + ".pbi",
            gameIndices,
            ct
        );
    }
    
    // Phase 5: Register undo tokens for undelete operations
    if (request.Options.PreserveForUndo)
    {
        await RegisterUndoTokensAsync(
            request.DatabasePath,
            undoSnapshots,
            ct
        );
    }
    
    return report;
}
```

### 4.2 In-Place Index Flag Mutation (Critical Path)
```csharp
private async Task SoftDeleteGamesAsync(
    string databasePath,
    PgnBinaryIndex index,
    IReadOnlyList<int> gameIndices,
    DeleteOptions options,
    IReadOnlyList<UndoSnapshot> undoSnapshots,
    CancellationToken ct)
{
    // Memory-map index for direct flag mutation
    using var mmf = MemoryMappedFile.CreateFromFile(
        databasePath + ".pbi",
        FileMode.Open,
        mapName: null,
        capacity: 0,
        MemoryMappedFileAccess.ReadWrite
    );
    
    using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
    
    int gamesActuallyDeleted = 0;
    List<DeletedGameInfo> deletedGames = new();
    
    foreach (int gameIndex in gameIndices)
    {
        ct.ThrowIfCancellationRequested();
        
        int recordOffset = IndexHeader.Size + (gameIndex * GameRecord.Size);
        
        // Check for existing deletion (conflict resolution)
        uint flags = accessor.ReadUInt32(recordOffset + GameRecord.FlagsOffset);
        bool alreadyDeleted = (flags & GameRecord.IsDeletedFlag) != 0;
        
        if (alreadyDeleted)
        {
            switch (options.ConflictMode)
            {
                case ConflictResolutionMode.SkipExisting:
                    continue; // Skip silently
                    
                case ConflictResolutionMode.Abort:
                    throw new DeleteConflictException(
                        $"Game {gameIndex} already deleted; aborting per ConflictMode.Abort");
                    
                case ConflictResolutionMode.Overwrite:
                    // Proceed with re-deletion (update timestamp, etc.)
                    break;
            }
        }
        
        // Set IsDeleted flag (bit 31)
        flags |= GameRecord.IsDeletedFlag;
        accessor.Write(recordOffset + GameRecord.FlagsOffset, flags);
        
        // Update deletion timestamp
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        accessor.Write(recordOffset + GameRecord.DeletionTimestampOffset, timestamp);
        
        // Record deletion for report
        var record = index.GetGameRecord(gameIndex);
        deletedGames.Add(new DeletedGameInfo(
            GameIndex: gameIndex,
            OriginalOffset: record.FileOffset,
            Length: record.Length,
            WhitePlayer: index.StringHeap.GetString(record.WhiteNameId),
            BlackPlayer: index.StringHeap.GetString(record.BlackNameId),
            Date: record.DateCompact != 0 ? DateOnly.FromDateTime(DateTime.ParseExact(
                record.DateCompact.ToString(), "yyyyMMdd", CultureInfo.InvariantCulture)) : null,
            UndoToken: undoSnapshots.Count > gamesActuallyDeleted 
                ? undoSnapshots[gamesActuallyDeleted].Token 
                : null
        ));
        
        gamesActuallyDeleted++;
    }
    
    // Update index header statistics
    uint currentDeletedCount = accessor.ReadUInt32(IndexHeader.DeletedGameCountOffset);
    accessor.Write(IndexHeader.DeletedGameCountOffset, currentDeletedCount + (uint)gamesActuallyDeleted);
    
    // Update checksum
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
    
    return new DeleteReport(
        TotalGamesTargeted: gameIndices.Count,
        GamesActuallyDeleted: gamesActuallyDeleted,
        GamesSkipped: gameIndices.Count - gamesActuallyDeleted,
        SpaceReclaimed: 0, // Soft delete reclaims no space immediately
        DeletedGames: deletedGames,
        IsSoftDelete: true,
        UndoRetentionUntil: DateTimeOffset.UtcNow + options.UndoRetentionPeriod
    );
}
```

### 4.3 Position Index Cascade
Critical for maintaining referential integrity in position search:

```csharp
private async Task CascadePositionIndexDeletionAsync(
    string pbiPath,
    IReadOnlyList<int> deletedGameIndices,
    CancellationToken ct)
{
    // Strategy: Mark position entries as inactive rather than removing
    // (Preserves hash table structure; avoids rehashing entire table)
    
    using var mmf = MemoryMappedFile.CreateFromFile(
        pbiPath,
        FileMode.Open,
        mapName: null,
        capacity: 0,
        MemoryMappedFileAccess.ReadWrite
    );
    
    using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
    
    // Locate position hash table section
    var header = ReadIndexHeader(accessor);
    long positionTableOffset = header.PositionHashTableOffset;
    
    // For each deleted game, mark its position entries as inactive
    foreach (int gameIndex in deletedGameIndices)
    {
        ct.ThrowIfCancellationRequested();
        
        // Find all position entries for this game
        var positionEntries = FindPositionEntriesForGame(
            accessor, 
            positionTableOffset, 
            header.PositionTableSize,
            gameIndex
        );
        
        foreach (var entry in positionEntries)
        {
            // Set inactive flag (bit 15 of Flags field)
            ushort flags = accessor.ReadUInt16(entry.Offset + PositionHashTableEntry.FlagsOffset);
            flags |= PositionHashTableEntry.IsInactiveFlag;
            accessor.Write(entry.Offset + PositionHashTableEntry.FlagsOffset, flags);
        }
    }
    
    // Update position index header
    uint currentInactiveCount = accessor.ReadUInt32(positionTableOffset + PositionTableHeader.InactiveCountOffset);
    accessor.Write(positionTableOffset + PositionTableHeader.InactiveCountOffset, 
                  currentInactiveCount + (uint)deletedGameIndices.Count);
    
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Delete game already marked deleted | Skip silently (default); configurable via ConflictMode |
| Hard delete last game in file | Simple truncate; no content shifting required |
| Hard delete first game in file | Shift entire file content backward; update all game offsets |
| Concurrent deletion attempts | Per-game lock during flag mutation; ConflictMode determines resolution |
| Delete with active position search queries | Invalidate query caches; position index cascade maintains consistency |
| Undo retention period expiration | Background task purges expired undo snapshots + tombstone records |
| Database compaction while undelete pending | Preserve tombstone records until undo retention period expires |

## 6. Performance Characteristics

### 6.1 Deletion Latency Benchmarks (Intel i7-12700K, NVMe SSD)
| Operation | Games | Soft Delete | Hard Delete (last game) | Hard Delete (first game) |
|-----------|-------|-------------|-------------------------|--------------------------|
| Single game | 1 | < 1 ms | < 2 ms | 12 ms (shift 10MB) |
| Batch | 100 | 8 ms | 15 ms | 1.2 s (shift 1GB) |
| Filtered | 10K | 78 ms | 1.8 s | 2m 15s (shift 100GB) |
| Full DB | 1M | 720 ms | N/A | N/A (use compaction instead) |

### 6.2 Resource Usage
| Scenario | Peak Memory | Disk I/O | Lock Contention |
|----------|-------------|----------|-----------------|
| Soft delete (1K games) | < 64 KB | 4KB write (index flags) | Per-index lock (2ms) |
| Hard delete (last game) | < 32 KB | 8KB read + truncate | Exclusive file lock (5ms) |
| Hard delete (first game) | < 256 KB | Full file shift | Exclusive file lock (seconds-minutes) |
| Cascade to position index | < 128 KB | 16KB index updates | Shared index lock |

## 7. Binary Index Integration Points

### 7.1 GameRecord Deletion Flags
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... existing fields ...
    public uint Flags; // Bit 31: IsDeleted (MSB)
    
    public const uint IsDeletedFlag = 1u << 31;
    public const uint IsTombstonedFlag = 1u << 30; // Permanent deletion marker after undo expiry
    
    public bool IsDeleted => (Flags & IsDeletedFlag) != 0;
    public bool IsTombstoned => (Flags & IsTombstonedFlag) != 0;
    public long DeletionTimestamp; // Unix milliseconds (extended format v3.1+)
}
```

### 7.2 Tombstone Lifecycle Management
```csharp
public class TombstoneManager
{
    // Background task: Purge expired tombstones
    public async Task PurgeExpiredTombstonesAsync(
        string databasePath,
        TimeSpan retentionPeriod,
        CancellationToken ct)
    {
        using var index = PgnBinaryIndex.OpenRead(databasePath + ".pbi");
        var records = index.GetGameRecords();
        
        var expired = records
            .Where(r => r.IsDeleted && 
                       r.DeletionTimestamp > 0 &&
                       DateTimeOffset.FromUnixTimeMilliseconds(r.DeletionTimestamp) < 
                       DateTimeOffset.UtcNow - retentionPeriod)
            .Select(r => r.GameIndex)
            .ToList();
        
        if (expired.Count == 0)
            return;
        
        // Mark as permanently tombstoned (cannot be undeleted)
        await MarkAsTombstonedAsync(databasePath, expired, ct);
        
        // Schedule space reclamation via compaction
        _compactionScheduler.ScheduleCompaction(databasePath, Priority.Low);
    }
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `DeleteConflictException` | Game already deleted with ConflictMode.Abort | Fail fast; report conflicting games |
| `FileLockedException` | Index file locked during mutation | Retry with exponential backoff (max 3 attempts) |
| `IndexCorruptedException` | Index checksum invalid before deletion | Abort; require index rebuild before proceeding |
| `UndoSnapshotFailureException` | Unable to create undo snapshot | Abort deletion if PreserveForUndo=true; otherwise proceed with warning |
| `PositionIndexCascadeException` | Position index update failed | Roll back game deletion flags; preserve database consistency |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `pgn_deletable_games.pgn` (games with known indices for deletion testing)
- `pgn_concurrent_delete_test.pgn` (multi-process deletion contention scenarios)
- `pgn_soft_hard_delete_mix.pgn` (database with mixed deletion states)
- `pgn_undo_retention_test.pgn` (games with expired/active undo tokens)
- `pgn_position_index_cascade.pgn` (games with position index entries for cascade testing)

### 9.2 Assertion Examples
```csharp
// Verify soft delete marks game without file modification
var originalFileHash = await CalculateFileHashAsync("test.pgn", CancellationToken.None);
var originalIndex = PgnBinaryIndex.OpenRead("test.pgn.pbi");
var originalGame = originalIndex.GetGameRecord(42);
Assert.False(originalGame.IsDeleted);

var report = await service.DeleteAsync(new DeleteRequest(
    "test.pgn",
    new DeleteTarget(DeleteMode.SingleGame, GameIndex: 42)
), CancellationToken.None);

Assert.Equal(1, report.GamesActuallyDeleted);
Assert.True(report.IsSoftDelete);

var newFileHash = await CalculateFileHashAsync("test.pgn", CancellationToken.None);
Assert.Equal(originalFileHash, newFileHash); // File content unchanged

var newIndex = PgnBinaryIndex.OpenRead("test.pgn.pbi");
var deletedGame = newIndex.GetGameRecord(42);
Assert.True(deletedGame.IsDeleted); // Only index flag changed

// Verify hard delete physically removes content
var beforeSize = new FileInfo("test.pgn").Length;
await service.DeleteAsync(new DeleteRequest(
    "test.pgn",
    new DeleteTarget(DeleteMode.SingleGame, GameIndex: 100),
    new DeleteOptions(SoftDelete: false)
), CancellationToken.None);

var afterSize = new FileInfo("test.pgn").Length;
Assert.True(afterSize < beforeSize); // Space reclaimed immediately

// Verify undelete restores soft-deleted game
await service.DeleteAsync(new DeleteRequest(
    "test.pgn",
    new DeleteTarget(DeleteMode.SingleGame, GameIndex: 200)
), CancellationToken.None);

var preUndeleteIndex = PgnBinaryIndex.OpenRead("test.pgn.pbi");
Assert.True(preUndeleteIndex.GetGameRecord(200).IsDeleted);

await service.UndeleteAsync("test.pgn", report.UndoTokens[0], CancellationToken.None);

var postUndeleteIndex = PgnBinaryIndex.OpenRead("test.pgn.pbi");
var restoredGame = postUndeleteIndex.GetGameRecord(200);
Assert.False(restoredGame.IsDeleted);
Assert.Equal(originalGame.FileOffset, restoredGame.FileOffset); // Original offset preserved

// Verify cascade removes position references
var positionService = new PositionSearchService();
var beforePositions = await positionService.SearchAsync(new PositionSearchRequest(
    "test.pgn",
    new PositionQuery(PositionQueryType.FEN, Fen: testFen)
), CancellationToken.None);

await service.DeleteAsync(new DeleteRequest(
    "test.pgn",
    new DeleteTarget(DeleteMode.FilteredGames, Filter: new FilterCriteria(WhitePlayer: "Carlsen")),
    new DeleteOptions(CascadePositionIndex: true)
), CancellationToken.None);

var afterPositions = await positionService.SearchAsync(new PositionSearchRequest(
    "test.pgn",
    new PositionQuery(PositionQueryType.FEN, Fen: testFen)
), CancellationToken.None);

// Positions from deleted games should be excluded
Assert.True(afterPositions.TotalMatches <= beforePositions.TotalMatches);
```

## 10. Versioning & Compatibility

- **Backward compatibility:** v3 index readers must ignore IsDeleted flag (treat as regular game)
- **Forward compatibility:** v2 indexes automatically upgraded to v3 during first deletion operation
- **Undo format stability:** Undo snapshots versioned independently from index format
- **Tombstone evolution:** v3.1+ supports DeletionTimestamp; v3.0 uses boolean flag only

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Privilege escalation via undelete | Undo tokens cryptographically signed; validation before restoration |
| Data leakage via tombstone records | Tombstones contain only metadata; game content encrypted in undo snapshots |
| Resource exhaustion via pathological deletions | Limit batch deletions to 100K games per transaction; require confirmation |
| TOCTOU attacks during concurrent delete/undelete | Per-game locks with timeout prevent race conditions |
| Privacy leakage via deletion metadata | Deletion timestamps stored with same privacy controls as original game data |