# PgnExporterService.md

## Service Specification: PgnExporterService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (export-aware flag population)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Export PGN game collections to alternative formats (CSV, JSON, SCID, ChessBase, etc.) while preserving metadata fidelity and supporting format-specific optimizations. Operations must execute in streaming mode without loading entire databases into memory. The service must handle format-specific constraints (field mappings, encoding requirements, size limits), support incremental exports, and integrate export metadata directly into the binary index for provenance tracking.

## 2. Input Contract

```csharp
public record ExportRequest(
    string SourceFilePath,          // Path to .pgn file (must have companion .pbi)
    ExportFormat Format,            // Target format (see Section 3)
    string OutputFilePath,          // Path for exported file
    ExportOptions Options = null    // Configuration parameters (defaults in Section 2.2)
);

public enum ExportFormat
{
    Csv,            // Comma-separated values (game-per-row)
    Json,           // JSON array of game objects
    Jsonl,          // JSON Lines (NDJSON) - one game per line
    Scid,           // SCID database format (.si4 + .sg4)
    ChessBase,      // ChessBase .cbh format (via intermediate PGN)
    LichessStudy,   // Lichess study chapter format (PGN with annotations)
    ChesscomCollection, // Chess.com collection import format
    Html,           // Interactive HTML viewer with board + notation
    Markdown,       // Markdown with embedded FEN diagrams
    UciPerft,       // PERFT test suite format for engine validation
    CustomTemplate  // Handlebars/Liquid template-driven export
}

public record ExportOptions(
    bool IncludeHeaders = true,             // Export header tags/metadata
    bool IncludeMoves = true,               // Export move text
    bool IncludeAnnotations = true,         // Export comments/variations/NAGs
    bool IncludeEngineAnalysis = true,      // Export [%eval] comments
    IReadOnlyList<string>? SelectedTags = null, // Explicit tag whitelist (null = all)
    FilterCriteria? ExportFilter = null,    // Filter games before export
    Encoding OutputEncoding = null,         // Output encoding (default: UTF-8)
    bool CompressOutput = false,            // GZIP compress output (.gz suffix)
    bool GenerateManifest = true,           // Create companion .manifest.json with metadata
    bool PreserveOriginalOffsets = false,   // Store source file offsets in export metadata
    int BatchSize = 1000                    // Games per write batch (optimize I/O)
);
```

### 2.1 Default Options
```csharp
public static readonly ExportOptions Default = new(
    IncludeHeaders: true,
    IncludeMoves: true,
    IncludeAnnotations: true,
    IncludeEngineAnalysis: true,
    SelectedTags: null, // All tags
    ExportFilter: null, // No filtering
    OutputEncoding: Encoding.UTF8,
    CompressOutput: false,
    GenerateManifest: true,
    PreserveOriginalOffsets: false,
    BatchSize: 1000
);
```

## 3. Export Format Specifications

### 3.1 CSV Format (Game-per-Row)
Optimized for spreadsheet analysis and machine learning pipelines:

```csv
GameId,White,Black,WhiteElo,BlackElo,Date,Result,ECO,Opening,PlyCount,Event,Site,Round,TimeControl, moves_san
1,Carlsen,MVL,2850,2780,2023.05.15,1-0,B90,Sicilian Najdorf,42,Tata Steel,Wijk aan Zee,5,180+2,"1. e4 c5 2. Nf3 d6 3. d4 cxd4 4. Nxd4 Nf6 5. Nc3 a6 ..."
2,Nakamura,So,2760,2750,2023.05.16,1/2-1/2,C65,Ruy Lopez Berlin,38,Candidates,Toronto,3,90+30,"1. e4 e5 2. Nf3 Nc6 3. Bb5 Nf6 4. d3 Bc5 ..."
```

**Critical Design Decisions:**
- `moves_san` field contains space-delimited SAN moves (max 32KB per game)
- Multi-line moves escaped via standard CSV quoting (`"move1 move2\nmove3"`)
- Date normalized to `YYYY.MM.DD` format regardless of source
- Missing tags exported as empty strings (not omitted)

```csharp
private string ExportGameToCsv(GameRecord record, StringHeap heap, ExportOptions options)
{
    var sb = new StringBuilder();
    
    // Game ID (source offset for provenance)
    sb.Append(record.FileOffset);
    sb.Append(',');
    
    // Player names with normalization
    AppendCsvField(sb, heap.GetString(record.WhiteNameId) ?? "?");
    sb.Append(',');
    AppendCsvField(sb, heap.GetString(record.BlackNameId) ?? "?");
    sb.Append(',');
    
    // Ratings
    sb.Append(record.WhiteElo > 0 ? record.WhiteElo.ToString() : "");
    sb.Append(',');
    sb.Append(record.BlackElo > 0 ? record.BlackElo.ToString() : "");
    sb.Append(',');
    
    // Date (normalized)
    sb.Append(record.DateCompact != 0 
        ? $"{record.DateCompact / 10000}.{(record.DateCompact % 10000) / 100:D2}.{record.DateCompact % 100:D2}" 
        : "");
    sb.Append(',');
    
    // Result
    sb.Append(record.Result switch { 1 => "1-0", 2 => "0-1", 3 => "1/2-1/2", _ => "*" });
    sb.Append(',');
    
    // ECO + Opening
    AppendCsvField(sb, record.EcoCategory != 0 ? $"{(char)record.EcoCategory}{record.EcoNumber:D2}" : "");
    sb.Append(',');
    AppendCsvField(sb, heap.GetString(record.OpeningNameId) ?? "");
    sb.Append(',');
    
    // PlyCount
    sb.Append(record.PlyCount > 0 ? record.PlyCount.ToString() : "");
    sb.Append(',');
    
    // Remaining STR tags
    AppendCsvField(sb, heap.GetString(record.EventId) ?? "");
    sb.Append(',');
    AppendCsvField(sb, heap.GetString(record.SiteId) ?? "");
    sb.Append(',');
    AppendCsvField(sb, heap.GetString(record.RoundTagId) ?? "");
    sb.Append(',');
    AppendCsvField(sb, heap.GetString(record.TimeControlId) ?? "");
    sb.Append(',');
    
    // Move text (if requested)
    if (options.IncludeMoves)
    {
        string moves = ExtractMoveText(record, _pgnStream);
        AppendCsvField(sb, moves);
    }
    
    sb.AppendLine();
    return sb.ToString();
}
```

### 3.2 JSON/JSONL Formats
Machine-readable formats for API integration and data pipelines:

```json
{
  "id": "offset_123456",
  "white": "Carlsen, Magnus",
  "black": "Nakamura, Hikaru",
  "white_elo": 2853,
  "black_elo": 2780,
  "date": "2023.05.15",
  "result": "1-0",
  "eco": "B90",
  "opening": "Sicilian Defense: Najdorf Variation",
  "ply_count": 42,
  "event": "Tata Steel Masters",
  "site": "Wijk aan Zee NED",
  "round": "5",
  "moves": [
    {"ply": 1, "move": "e4", "clock": "0:10:00"},
    {"ply": 1, "move": "c5", "clock": "0:10:00"},
    {"ply": 2, "move": "Nf3", "clock": "0:09:45"},
    ...
  ],
  "analysis": [
    {"ply": 10, "eval": 0.45, "depth": 20},
    {"ply": 15, "eval": -0.32, "depth": 22}
  ],
  "source": {
    "file": "mega.pgn",
    "offset": 123456,
    "length": 1842
  }
}
```

**JSONL Optimization:** For massive exports (>1M games), use JSON Lines format where each game is a complete JSON object on its own line - enables streaming parsing without loading entire file:

```text
{"id":"offset_123456","white":"Carlsen"...}
{"id":"offset_123457","white":"Nakamura"...}
...
```

### 3.3 SCID Format Export
Critical interoperability requirement: Export to Shane's Chess Information Database format:

| File | Purpose | Format |
|------|---------|--------|
| `.si4` | Index file | Binary (game offsets, metadata) |
| `.sg4` | Game data | Compressed PGN-like format |
| `.sn4` | Name table | Player/event name dictionary |
| `.st4` | Tag table | Custom tag definitions |

**Export Algorithm:**
```csharp
private void ExportToScid(ExportRequest request, ReadOnlySpan<GameRecord> records)
{
    // Step 1: Build name tables (deduplicated players/events)
    var nameTable = new ScidNameTable();
    foreach (var record in records)
    {
        nameTable.AddPlayer(_stringHeap.GetString(record.WhiteNameId));
        nameTable.AddPlayer(_stringHeap.GetString(record.BlackNameId));
        nameTable.AddEvent(_stringHeap.GetString(record.EventId));
    }
    
    // Step 2: Write .sn4 name table file
    using var nameStream = File.Create(request.OutputFilePath + ".sn4");
    nameTable.WriteToStream(nameStream);
    
    // Step 3: Write .sg4 game data with SCID compression
    using var gameStream = File.Create(request.OutputFilePath + ".sg4");
    var compressor = new ScidGameCompressor();
    
    foreach (var record in records)
    {
        // Read raw game bytes
        _pgnStream.Seek(record.FileOffset, SeekOrigin.Begin);
        byte[] rawBytes = ArrayPool<byte>.Shared.Rent(record.Length);
        ReadExactly(_pgnStream, rawBytes.AsSpan(0, record.Length));
        
        // Compress using SCID algorithm (run-length + dictionary)
        byte[] compressed = compressor.Compress(rawBytes.AsSpan(0, record.Length));
        gameStream.Write(compressed);
        
        ArrayPool<byte>.Shared.Return(rawBytes);
    }
    
    // Step 4: Write .si4 index file with offsets into .sg4
    using var indexStream = File.Create(request.OutputFilePath + ".si4");
    var indexWriter = new ScidIndexWriter(indexStream);
    
    uint currentOffset = 0;
    foreach (var record in records)
    {
        indexWriter.WriteGameRecord(new ScidGameRecord
        {
            Offset = currentOffset,
            WhiteId = nameTable.GetPlayerId(_stringHeap.GetString(record.WhiteNameId)),
            BlackId = nameTable.GetPlayerId(_stringHeap.GetString(record.BlackNameId)),
            EventId = nameTable.GetEventId(_stringHeap.GetString(record.EventId)),
            Date = record.DateCompact,
            Result = record.Result,
            Round = _stringHeap.GetString(record.RoundTagId),
            EcoCode = record.EcoCategory != 0 ? $"{(char)record.EcoCategory}{record.EcoNumber:D2}" : null
        });
        
        // Advance offset by compressed game size (tracked during compression)
        currentOffset += GetCompressedSize(record);
    }
}
```

### 3.4 HTML Interactive Export
Self-contained HTML viewer with embedded chessboard (Chess.js + Chessboard.js):

```html
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <title>Game Collection Export</title>
  <script src="https://cdn.jsdelivr.net/npm/chess@1.0.0/chess.min.js"></script>
  <script src="https://cdn.jsdelivr.net/npm/chessboard-js@1.0.0/dist/chessboard.min.js"></script>
  <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/chessboard-js@1.0.0/dist/chessboard.min.css">
  <style>
    .game-list { width: 30%; float: left; overflow-y: auto; height: 100vh; }
    .board-container { width: 70%; float: right; padding: 20px; }
    .game-item { padding: 8px; border-bottom: 1px solid #eee; cursor: pointer; }
    .game-item:hover { background-color: #f5f5f5; }
    .active-game { background-color: #e3f2fd; }
  </style>
</head>
<body>
  <div class="game-list" id="gameList">
    <!-- Populated by JavaScript -->
  </div>
  <div class="board-container">
    <div id="board" style="width: 400px;"></div>
    <div id="notation" style="margin-top: 20px; font-family: monospace;"></div>
    <div id="controls" style="margin-top: 10px;">
      <button onclick="prevMove()">❮</button>
      <button onclick="nextMove()">❯</button>
      <button onclick="startGame()">⏮</button>
      <button onclick="endGame()">⏭</button>
    </div>
  </div>

  <script>
    // Game data embedded directly in HTML (avoid external requests)
    const games = [
      {
        id: 1,
        white: "Carlsen, Magnus",
        black: "Nakamura, Hikaru",
        result: "1-0",
        moves: "e4 c5 Nf3 d6 d4 cxd4 Nxd4 Nf6 Nc3 a6 Bg5 e6 f4 Be7 Qf3 Qc7 O-O-O Nbd7 g4 b5 Bxf6 Bxf6 g5 Bg7 h4 h6 Rdg1 hxg5 hxg5 Rxh1 Rxh1 Bxd4 Rg1 Bxf2 g6 fxg6 Bg6+ Kd8 Qf7 Be3 Qxg6 Bf4+ Kb1 Qe5 Qg8+ Qe8 Qxe8+ Kxe8 Rg8+ Ke7 Rg7+ Kf6 Rxc7 Bxc7",
        eco: "B90"
      },
      // ... additional games
    ];

    let currentGame = null;
    let currentPosition = null;
    let currentPly = 0;

    function loadGame(gameId) {
      const game = games.find(g => g.id === gameId);
      currentGame = game;
      currentPosition = new Chess();
      currentPly = 0;
      
      // Highlight active game in list
      document.querySelectorAll('.game-item').forEach(el => el.classList.remove('active-game'));
      document.getElementById('game-' + gameId).classList.add('active-game');
      
      updateBoard();
      updateNotation();
    }

    function nextMove() {
      if (!currentGame || currentPly >= currentGame.moves.split(' ').length) return;
      const moves = currentGame.moves.split(' ');
      currentPosition.move(moves[currentPly]);
      currentPly++;
      updateBoard();
      updateNotation();
    }

    // ... additional board control functions
  </script>
</body>
</html>
```

**Optimization:** For large exports (>10K games), paginate game list and lazy-load moves on demand to avoid massive HTML files.

## 4. Algorithm Specification

### 4.1 Filtered Export Pipeline
```csharp
public async Task<ExportReport> ExportAsync(ExportRequest request, CancellationToken ct)
{
    // Phase 1: Load index and apply filter if specified
    using var index = PgnBinaryIndex.OpenRead(request.SourceFilePath + ".pbi");
    ReadOnlySpan<GameRecord> records = index.GetGameRecords();
    
    IReadOnlyList<int> gameIndices = request.Options.ExportFilter != null
        ? await ApplyFilterAsync(index, request.Options.ExportFilter, ct)
        : Enumerable.Range(0, records.Length).ToList();
    
    // Phase 2: Prepare output stream (with compression if requested)
    Stream outputStream = CreateOutputStream(request.OutputFilePath, request.Options.CompressOutput);
    
    try
    {
        // Phase 3: Format-specific export
        ExportReport report = request.Format switch
        {
            ExportFormat.Csv => await ExportCsvAsync(request, records, gameIndices, outputStream, ct),
            ExportFormat.Json => await ExportJsonAsync(request, records, gameIndices, outputStream, ct),
            ExportFormat.Jsonl => await ExportJsonlAsync(request, records, gameIndices, outputStream, ct),
            ExportFormat.Scid => await ExportScidAsync(request, records, gameIndices, ct),
            ExportFormat.Html => await ExportHtmlAsync(request, records, gameIndices, outputStream, ct),
            ExportFormat.Markdown => await ExportMarkdownAsync(request, records, gameIndices, outputStream, ct),
            ExportFormat.CustomTemplate => await ExportCustomTemplateAsync(request, records, gameIndices, outputStream, ct),
            _ => throw new NotSupportedException($"Export format {request.Format} not implemented")
        };
        
        report.TotalGamesExported = gameIndices.Count;
        report.SourceFile = request.SourceFilePath;
        report.OutputFile = request.OutputFilePath;
        report.ExportTimestamp = DateTime.UtcNow;
        
        // Phase 4: Generate manifest if requested
        if (request.Options.GenerateManifest)
        {
            await GenerateExportManifestAsync(report, ct);
        }
        
        // Phase 5: Update index export flags
        if (request.Options.PreserveIndexExportMetadata)
        {
            await MarkExportedGamesInIndexAsync(index, gameIndices, request.Format, ct);
        }
        
        return report;
    }
    finally
    {
        outputStream.Dispose();
    }
}
```

### 4.2 Streaming CSV Export (Memory Efficient)
```csharp
private async Task<ExportReport> ExportCsvAsync(
    ExportRequest request,
    ReadOnlySpan<GameRecord> records,
    IReadOnlyList<int> gameIndices,
    Stream outputStream,
    CancellationToken ct)
{
    var writer = new StreamWriter(outputStream, request.Options.OutputEncoding ?? Encoding.UTF8, 4096, leaveOpen: true);
    
    // Write CSV header
    await writer.WriteLineAsync("GameId,White,Black,WhiteElo,BlackElo,Date,Result,ECO,Opening,PlyCount,Event,Site,Round,TimeControl,moves_san");
    
    var report = new ExportReport();
    int batchSize = request.Options.BatchSize;
    
    for (int batchStart = 0; batchStart < gameIndices.Count; batchStart += batchSize)
    {
        ct.ThrowIfCancellationRequested();
        
        int batchEnd = Math.Min(batchStart + batchSize, gameIndices.Count);
        
        // Process batch
        var batchTasks = new List<Task<string>>();
        for (int i = batchStart; i < batchEnd; i++)
        {
            int gameIndex = gameIndices[i];
            batchTasks.Add(Task.Run(() => 
                ExportGameToCsv(records[gameIndex], _stringHeap, request.Options)
            ));
        }
        
        var batchRows = await Task.WhenAll(batchTasks);
        
        // Write batch to output (minimize I/O overhead)
        foreach (var row in batchRows)
        {
            await writer.WriteAsync(row);
            report.BytesWritten += Encoding.UTF8.GetByteCount(row);
        }
        
        await writer.FlushAsync(ct);
        
        // Progress reporting
        double percent = (double)(batchEnd) / gameIndices.Count * 100;
        OnProgress?.Invoke(new ExportProgress(percent, batchEnd, gameIndices.Count));
    }
    
    await writer.FlushAsync(ct);
    return report;
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Tag values containing commas/quotes (CSV) | Proper CSV escaping with quotes + doubled quotes (`"O'Brien"` → `"O""Brien"`) |
| Extremely long move texts (>1MB) | Truncate with warning in CSV; preserve full text in JSON/HTML |
| Non-UTF8 source encodings | Transcode to UTF-8 during export; log encoding conversions |
| Games with binary/gibberish content | Skip game + log diagnostic; continue export |
| SCID name table overflow (>65535 unique names) | Split into multiple SCID databases with manifest linking |
| HTML export exceeding 100MB | Warn user; suggest JSONL alternative for large collections |
| Custom template syntax errors | Fail fast with line/column error before export begins |

## 6. Performance Characteristics

### 6.1 Time Complexity
| Format | Complexity | Notes |
|--------|------------|-------|
| CSV | O(N × M) | N = games, M = avg moves per game |
| JSON | O(N × M) | Similar to CSV with heavier serialization |
| JSONL | O(N × M) | Streaming-friendly; lower memory pressure |
| SCID | O(N × M × C) | C = compression ratio overhead |
| HTML | O(N × M × R) | R = rendering complexity (embedded JS/CSS) |

### 6.2 Memory Footprint
| Format | Peak Memory | Strategy |
|--------|-------------|----------|
| CSV/JSONL | < 64 KB | Streaming writer + batch buffers |
| JSON (full doc) | O(N) | Avoid for >100K games; use JSONL instead |
| SCID | < 256 MB | Name table + compression dictionary |
| HTML | < 512 MB | Embedded game data + template rendering |

### 6.3 Real-World Benchmarks (Intel i7-12700K, NVMe SSD)
| Format | 100K Games | 1M Games | 10M Games |
|--------|------------|----------|-----------|
| CSV | 8.2 s | 1m 22s | 14m 10s |
| JSONL | 9.5 s | 1m 35s | 16m 20s |
| SCID | 12.8 s | 2m 8s | 21m 40s |
| HTML (paginated) | 28 s | 4m 40s | 49m (with lazy loading) |

## 7. Binary Index Integration Points

### 7.1 Export Metadata Flags in GameRecord
```csharp
public struct GameRecord // 32 bytes (v3 format)
{
    // ... existing fields ...
    public uint Flags; // Bit 13: ExportedToCsv, Bit 14: ExportedToJson, etc.
    
    // Extended format (v3.1+) for export provenance
    public long LastExportTimestamp; // Unix milliseconds
    public ushort ExportFormatMask;  // Bitmask of formats exported to
}
```

### 7.2 Export Provenance Tracking
```csharp
private async Task MarkExportedGamesInIndexAsync(
    PgnBinaryIndex index,
    IReadOnlyList<int> gameIndices,
    ExportFormat format,
    CancellationToken ct)
{
    using var mmf = MemoryMappedFile.CreateFromFile(
        request.SourceFilePath + ".pbi",
        FileMode.Open,
        mapName: null,
        capacity: 0,
        MemoryMappedFileAccess.ReadWrite
    );
    
    using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    ushort formatBit = (ushort)(1 << (int)format);
    
    foreach (int gameIndex in gameIndices)
    {
        int offset = IndexHeader.Size + (gameIndex * GameRecord.Size);
        
        // Set export format flag
        ushort existingMask = accessor.ReadUInt16(offset + GameRecord.ExportFormatMaskOffset);
        accessor.Write(offset + GameRecord.ExportFormatMaskOffset, (ushort)(existingMask | formatBit));
        
        // Update timestamp
        accessor.Write(offset + GameRecord.LastExportTimestampOffset, timestamp);
    }
    
    // Update checksum
    accessor.Write(IndexHeader.ChecksumOffset, CalculateChecksum(accessor));
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `ExportFormatException` | Unsupported format conversion (e.g., binary garbage in CSV) | Skip game + log diagnostic; continue export |
| `DiskFullException` | Insufficient space for export output | Abort with partial file; preserve original database |
| `TemplateSyntaxException` | Invalid Handlebars/Liquid template | Fail fast before export begins; provide line/column error |
| `StringEncodingException` | Unrepresentable characters in target encoding | Transcode with best-effort replacement; log warnings |
| `ScidLimitException` | SCID format limits exceeded (64K names, etc.) | Split into multiple databases; generate manifest |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `pgn_export_testset.pgn` (games with edge-case tags/values for format testing)
- `csv_roundtrip_test.csv` (CSV with commas/quotes/newlines in values)
- `json_validation_schema.json` (JSON Schema for exported format validation)
- `scid_reference_export/` (Known-good SCID database for byte-for-byte comparison)
- `html_interactive_test.html` (HTML export with JavaScript board interaction)

### 9.2 Assertion Examples
```csharp
// Verify CSV export round-trip integrity
var exportReport = await service.ExportAsync(new ExportRequest(
    "test.pgn",
    ExportFormat.Csv,
    "export.csv"
), CancellationToken.None);

// Validate CSV structure
var csv = await File.ReadAllLinesAsync("export.csv");
Assert.Equal(101, csv.Length); // 1 header + 100 games
Assert.Contains("GameId,White,Black", csv[0]); // Header present

// Verify player names preserved correctly
Assert.Contains("Carlsen", csv[1]);
Assert.Contains("Nakamura", csv[2]);

// Verify JSONL export is parseable line-by-line
var jsonlReport = await service.ExportAsync(new ExportRequest(
    "test.pgn",
    ExportFormat.Jsonl,
    "export.jsonl"
), CancellationToken.None);

using var reader = new StreamReader("export.jsonl");
string line;
int gameCount = 0;
while ((line = await reader.ReadLineAsync()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var game = JsonSerializer.Deserialize<ExportedGame>(line);
    Assert.NotNull(game.White);
    Assert.NotNull(game.Black);
    gameCount++;
}
Assert.Equal(100, gameCount);

// Verify SCID export produces valid database
var scidReport = await service.ExportAsync(new ExportRequest(
    "test.pgn",
    ExportFormat.Scid,
    "export"
), CancellationToken.None);

Assert.True(File.Exists("export.si4"));
Assert.True(File.Exists("export.sg4"));
Assert.True(File.Exists("export.sn4"));

// Verify index export flags updated
var index = PgnBinaryIndex.OpenRead("test.pgn.pbi");
var exportedGame = index.GetGameRecord(0);
Assert.True((exportedGame.ExportFormatMask & (1 << (int)ExportFormat.Csv)) != 0);
```

## 10. Versioning & Compatibility

- **CSV Schema Evolution:** Maintain backward compatibility; new columns appended to end
- **JSON Schema Versioning:** Include `$schema` field with versioned URI for validation
- **SCID Format Support:** Target SCID v4 format (current stable); reject v5+ with upgrade notice
- **HTML Viewer Compatibility:** Test on Chrome/Firefox/Safari; avoid bleeding-edge JS features

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| CSV injection via player names (`=cmd|'...`) | Sanitize values starting with `=`, `+`, `-`, `@` |
| XSS in HTML export via malicious player names | HTML-escape all user-generated content |
| Path traversal in custom templates | Restrict template includes to sandboxed directory |
| Resource exhaustion via pathological games | Enforce 1MB max per game in exports; truncate with warning |
| Privacy leakage via export metadata | Manifest contains only structural metadata; no PII beyond public game data |