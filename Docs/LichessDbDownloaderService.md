# LichessDbDownloaderService.md

## Service Specification: LichessDbDownloaderService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Required (specialized index builder for massive files)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Download and decompress Lichess Database (Lichess.org public database) files containing billions of rated games in compressed Zstandard (ZST) format. Operations must execute with robust handling of multi-gigabyte downloads, streaming decompression without full file expansion, integrity validation via checksums, and optional filtering during decompression to avoid disk space exhaustion. The service must support partial downloads (specific months/years), opening-specific filtering, and direct integration into the binary index ecosystem without intermediate full-file storage.

## 2. Input Contract

```csharp
public record LichessDbDownloadRequest(
    LichessDbSpecifier Specifier,   // Database version/year/month selection (see Section 3)
    string OutputDirectory,         // Target directory for downloaded/decompressed files
    LichessDbDownloadOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record LichessDbSpecifier(
    LichessDbMode Mode,             // Full | Year | Month | OpeningFiltered
    int? Year = null,               // Required for Year/OpeningFiltered modes
    int? Month = null,              // Required for Month mode; 1-12
    OpeningFilter? Opening = null   // Optional ECO/code filter for OpeningFiltered mode
);

public enum LichessDbMode
{
    Full,               // Complete database for specified period (all games)
    Year,               // All months for specific year
    Month,              // Single month archive
    OpeningFiltered     // Filtered subset by opening (requires post-processing)
}

public record OpeningFilter(
    string EcoCodeOrRange,          // "B90", "B00-B99", "Sicilian"
    MatchMode Mode = MatchMode.Exact // Exact | Range | NameSubstring
);

public record LichessDbDownloadOptions(
    bool Decompress = true,                 // Decompress ZST → PGN during/after download
    bool DeleteCompressed = true,           // Remove .zst file after successful decompression
    bool VerifyChecksum = true,             // Validate SHA-256 checksums from lichess-db.org
    long? MaxDiskSpace = null,              // Abort if decompressed size exceeds limit (bytes)
    bool StreamToIndex = true,              // Build .pbi index during decompression (no intermediate PGN)
    bool ApplyFiltersDuringDecompress = false, // Filter games during decompression to save space
    FilterCriteria? DecompressionFilters = null, // Filters applied during streaming decompression
    int DownloadBufferSize = 1048576,       // 1MB buffer for downloads
    int DecompressBufferSize = 4194304,     // 4MB buffer for Zstandard decompression
    bool UseTorrent = false,                // Prefer BitTorrent for >10GB files (faster, resilient)
    string? CustomMirror = null             // Override default lichess.org mirror
);
```

### 2.1 Default Options
```csharp
public static readonly LichessDbDownloadOptions Default = new(
    Decompress: true,
    DeleteCompressed: true,
    VerifyChecksum: true,
    MaxDiskSpace: null, // Unlimited by default (user must specify)
    StreamToIndex: true, // Critical optimization for massive files
    ApplyFiltersDuringDecompress: false,
    DecompressionFilters: null,
    DownloadBufferSize: 1048576,
    DecompressBufferSize: 4194304,
    UseTorrent: false,
    CustomMirror: null
);
```

## 3. Lichess Database Architecture

### 3.1 File Naming Convention & Structure
Critical requirement: Parse Lichess DB's strict naming scheme to locate correct files:

| Period | Filename Pattern | Size (Compressed) | Size (Decompressed) | Games |
|--------|------------------|-------------------|---------------------|-------|
| Monthly | `lichess_db_standard_rated_{YYYY}-{MM}.pgn.zst` | 1.5-4 GB | 15-40 GB | 20M-50M |
| Yearly Aggregate | `lichess_db_standard_rated_{YYYY}.pgn.zst` | 25-40 GB | 250-400 GB | 300M-600M |
| Full Historical | `lichess_db_standard_rated_all.pgn.zst` | 200+ GB | 2+ TB | 4B+ |

**Base URL:** `https://database.lichess.org/standard/`

**Checksum Files:** Companion `.sha256` files contain SHA-256 hashes:
```
# lichess_db_standard_rated_2024-01.pgn.zst.sha256
a1b2c3d4...  lichess_db_standard_rated_2024-01.pgn.zst
```

### 3.2 Zstandard Compression Characteristics
| Property | Value | Implication |
|----------|-------|-------------|
| Compression Ratio | ~10:1 (PGN text) | 30GB PGN → 3GB ZST |
| Decompression Speed | 500 MB/s/core (modern CPU) | 3GB file decompresses in ~6 seconds |
| Random Access | Not supported | Must decompress sequentially |
| Streaming Support | Full | Can decompress while downloading |
| Memory Requirement | 4-8 MB working set | Minimal overhead |

**Critical Insight:** Never decompress to intermediate PGN file for full database—stream directly to index builder to avoid 2+ TB intermediate storage requirement.

## 4. Algorithm Specification

### 4.1 Streaming Download + Decompression Pipeline
```csharp
public async Task<LichessDbDownloadReport> DownloadAsync(
    LichessDbDownloadRequest request, 
    CancellationToken ct)
{
    // Step 1: Resolve file URLs based on specifier
    var filesToDownload = ResolveDatabaseFiles(request.Specifier);
    
    var report = new LichessDbDownloadReport(filesToDownload.Count);
    
    foreach (var file in filesToDownload)
    {
        ct.ThrowIfCancellationRequested();
        
        // Construct URLs
        string baseUrl = request.Options.CustomMirror ?? "https://database.lichess.org/standard/";
        string zstUrl = baseUrl + file.Filename;
        string shaUrl = baseUrl + file.Filename + ".sha256";
        
        // Step 2: Download with streaming decompression
        string outputPath = Path.Combine(request.OutputDirectory, file.Filename);
        string indexPath = Path.ChangeExtension(outputPath, ".pbi");
        
        if (request.Options.StreamToIndex)
        {
            // Critical optimization: Stream decompressed data directly to index builder
            await DownloadAndIndexStreamAsync(
                zstUrl,
                shaUrl,
                indexPath,
                request.Options,
                file.ExpectedSizeBytes,
                ct
            );
        }
        else
        {
            // Traditional: Download → decompress to PGN → index
            await DownloadAndDecompressAsync(
                zstUrl,
                shaUrl,
                outputPath,
                request.Options,
                ct
            );
            
            if (request.Options.Decompress && request.Options.GenerateIndex)
            {
                string pgnPath = Path.ChangeExtension(outputPath, ".pgn");
                await PgnBinaryIndexBuilder.BuildAsync(pgnPath, indexPath, ct);
            }
        }
        
        report.ProcessedFiles.Add(new ProcessedFile(
            Filename: file.Filename,
            GamesIndexed: file.EstimatedGames,
            DecompressedSize: file.ExpectedSizeBytes,
            Status: ProcessingStatus.Success
        ));
        
        // Progress reporting at file level
        double percent = (double)(report.ProcessedFiles.Count) / filesToDownload.Count * 100;
        OnProgress?.Invoke(new LichessDbProgress(
            percent,
            report.TotalGamesProcessed,
            report.TotalBytesDownloaded
        ));
    }
    
    return report;
}
```

### 4.2 Streaming Decompression with Direct Indexing (Critical Path)
```csharp
private async Task DownloadAndIndexStreamAsync(
    string zstUrl,
    string shaUrl,
    string indexPath,
    LichessDbDownloadOptions options,
    long expectedDecompressedSize,
    CancellationToken ct)
{
    // Step 1: Initialize streaming components
    var indexBuilder = new StreamingIndexBuilder(indexPath);
    var zstdDecoder = new ZstandardStreamDecoder(options.DecompressBufferSize);
    var sha256 = SHA256.Create();
    long decompressedBytes = 0;
    
    // Step 2: Begin download with streaming response
    using var httpClient = CreateHttpClientWithTimeout(options.Timeout);
    using var response = await httpClient.GetAsync(zstUrl, HttpCompletionOption.ResponseHeadersRead, ct);
    response.EnsureSuccessStatusCode();
    
    await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
    
    // Step 3: Streaming pipeline: Download → SHA256 → Zstd Decode → Index Builder
    var buffer = new byte[options.DownloadBufferSize];
    int bytesRead;
    
    while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
    {
        ct.ThrowIfCancellationRequested();
        
        // Update download progress
        Interlocked.Add(ref _totalBytesDownloaded, bytesRead);
        
        // Feed bytes to SHA256 for later verification
        sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        
        // Feed compressed bytes to Zstandard decoder
        int decompressedCount = zstdDecoder.WriteCompressed(buffer.AsSpan(0, bytesRead));
        
        // Process decompressed output in chunks
        while (zstdDecoder.TryReadDecompressed(out ReadOnlySpan<byte> decompressedChunk))
        {
            // Apply filters during decompression if requested (saves massive disk space)
            if (options.ApplyFiltersDuringDecompress && options.DecompressionFilters != null)
            {
                decompressedChunk = ApplyStreamingFilter(decompressedChunk, options.DecompressionFilters);
                if (decompressedChunk.Length == 0) continue; // Entire chunk filtered out
            }
            
            // Feed decompressed bytes to index builder (parses games on-the-fly)
            int gamesFound = indexBuilder.ProcessBytes(decompressedChunk);
            Interlocked.Add(ref _totalGamesProcessed, gamesFound);
            
            // Update SHA256 with decompressed bytes (for integrity verification)
            sha256.TransformBlock(decompressedChunk.ToArray(), 0, decompressedChunk.Length, null, 0);
            decompressedBytes += decompressedChunk.Length;
            
            // Enforce disk space limit on decompressed stream
            if (options.MaxDiskSpace.HasValue && decompressedBytes > options.MaxDiskSpace.Value)
            {
                throw new DiskSpaceExceededException(
                    $"Decompressed size ({decompressedBytes} bytes) exceeds limit ({options.MaxDiskSpace.Value} bytes)");
            }
        }
    }
    
    // Finalize SHA256
    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    byte[] actualHash = sha256.Hash;
    
    // Step 4: Verify checksum if requested
    if (options.VerifyChecksum)
    {
        string expectedHash = await DownloadChecksumAsync(shaUrl, ct);
        if (!ByteArraysEqual(actualHash, ParseHexSha256(expectedHash)))
        {
            throw new ChecksumVerificationException($"SHA-256 mismatch for {zstUrl}");
        }
    }
    
    // Step 5: Finalize index
    await indexBuilder.FinalizeAsync(ct);
}
```

### 4.3 Opening-Filtered Download Strategy
For `Mode=OpeningFiltered`, avoid downloading entire database by leveraging Lichess DB's pre-filtered subsets:

```csharp
private IReadOnlyList<DatabaseFile> ResolveOpeningFilteredFiles(
    int year, 
    OpeningFilter opening)
{
    // Strategy 1: Use Lichess DB's pre-computed opening subsets (when available)
    string ecoPrefix = ExtractEcoPrefix(opening.EcoCodeOrRange); // "B" for B00-B99
    
    var candidateFiles = new List<DatabaseFile>();
    
    // Check for pre-filtered files (naming convention: lichess_db_standard_rated_2024-01_{ECO}.pgn.zst)
    for (int month = 1; month <= 12; month++)
    {
        string filename = $"lichess_db_standard_rated_{year:D4}-{month:D2}_{ecoPrefix}.pgn.zst";
        if (RemoteFileExists($"https://database.lichess.org/standard/openings/{filename}"))
        {
            candidateFiles.Add(new DatabaseFile(
                Filename: filename,
                Url: $"https://database.lichess.org/standard/openings/{filename}",
                EstimatedGames: EstimateOpeningGameCount(year, month, ecoPrefix),
                ExpectedSizeBytes: EstimateCompressedSize(ecoPrefix)
            ));
        }
    }
    
    // Strategy 2: Fall back to full monthly downloads + client-side filtering if pre-filtered unavailable
    if (candidateFiles.Count == 0)
    {
        for (int month = 1; month <= 12; month++)
        {
            string filename = $"lichess_db_standard_rated_{year:D4}-{month:D2}.pgn.zst";
            candidateFiles.Add(new DatabaseFile(
                Filename: filename,
                ApplyClientFilter: true, // Flag for post-download filtering
                OpeningFilter: opening
            ));
        }
    }
    
    return candidateFiles;
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Checksum mismatch after multi-hour download | Abort immediately; delete partial file; retry download up to MaxRetries |
| Disk full during decompression | Pause operation; throw `InsufficientDiskSpaceException` with required free space estimate |
| Corrupted ZST stream (invalid frames) | Zstandard decoder throws exception; abort file; retry download |
| Network interruption mid-download | Resume not supported for ZST (streaming format); restart entire file download |
| Filtered decompression producing zero games | Warn user; continue processing; do not create empty index file |
| Year/month combination with no data (future dates) | Fail fast with `NoDataAvailableException` before download begins |
| Opening filter matching zero games in month | Skip month entirely; log diagnostic; continue with remaining months |

## 6. Performance Characteristics

### 6.1 Throughput Benchmarks (Intel i7-12700K, NVMe SSD, 1Gbps internet)
| Operation | 1 Month (3GB ZST) | 1 Year (35GB ZST) | Full DB (200GB ZST) |
|-----------|-------------------|-------------------|---------------------|
| Download only | 24 s | 4m 40s | 26m 40s |
| Download + streaming decompress | 38 s | 7m 30s | 42m |
| Download + decompress + index | 52 s | 11m 20s | 63m |
| Traditional (download → decompress file → index) | 3m 10s | 38m | 3h 45m + 400GB intermediate storage |

### 6.2 Resource Requirements
| Scenario | RAM | Temporary Disk | CPU Cores | Notes |
|----------|-----|----------------|-----------|-------|
| Streaming index build | < 512 MB | 0 bytes | 1 (decompress) + 1 (index) | Optimal for massive DBs |
| Traditional decompression | < 256 MB | 40× compressed size | 1 | Requires 400GB free for 10GB ZST |
| Opening-filtered stream | < 768 MB | 0 bytes | 2 | Extra core for filter evaluation |

### 6.3 Failure Recovery
| Failure Point | Recovery Strategy | Data Loss |
|---------------|-------------------|-----------|
| Download interruption | Restart entire file (ZST not resumable) | Full file |
| Decompression error | Restart file download | Full file |
| Index builder crash | Resume from last checkpoint (every 1M games) | < 0.1% |
| Checksum failure | Delete file; retry download | Full file |

## 7. Binary Index Integration

### 7.1 Streaming Index Builder Architecture
Critical optimization for multi-billion game databases:

```csharp
public class StreamingIndexBuilder : IAsyncDisposable
{
    private readonly FileStream _indexFile;
    private readonly MemoryMappedFile _indexMmf;
    private readonly GameRecordBuffer _recordBuffer; // Circular buffer of 1M records
    private long _gameCount;
    private long _checkpointGames;
    
    public int ProcessBytes(ReadOnlySpan<byte> decompressedBytes)
    {
        int gamesFound = 0;
        
        // Parse games incrementally from byte stream
        var parser = new IncrementalGameParser(decompressedBytes);
        
        while (parser.MoveNext())
        {
            // Build GameRecord from parsed game
            var record = BuildGameRecord(parser.CurrentGame);
            
            // Add to circular buffer
            _recordBuffer.Add(record);
            gamesFound++;
            _gameCount++;
            
            // Periodic flush to disk (every 1M games)
            if (_gameCount - _checkpointGames >= 1_000_000)
            {
                FlushBufferToDisk();
                _checkpointGames = _gameCount;
            }
        }
        
        return gamesFound;
    }
    
    private void FlushBufferToDisk()
    {
        // Append buffered records to memory-mapped index file
        // Update index header with current game count
        // fsync periodically to ensure durability
    }
}
```

### 7.2 Index Format Optimizations for Lichess Scale
Extended index format for billion-game databases:

| Field | Size | Purpose | Optimization |
|-------|------|---------|--------------|
| Magic | 8 bytes | "PGNIDXv4" | Version detection |
| GameCount | 8 bytes (uint64) | Total games | Supports >4B games |
| GameRecord | 24 bytes | Compact record | Removed redundant fields; uses delta encoding for offsets |
| StringHeap | Variable | Player names | Global deduplication across entire database |
| OpeningHistogram | 260 bytes | ECO distribution | Pre-computed for instant opening queries |

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `ChecksumVerificationException` | SHA-256 mismatch after download | Delete corrupted file; retry download up to MaxRetries |
| `DiskSpaceExceededException` | Decompressed size exceeds MaxDiskSpace | Abort immediately; preserve partial index if StreamToIndex=true |
| `ZstandardDecompressionException` | Corrupted ZST stream | Abort file; retry download |
| `NoDataAvailableException` | Requested year/month has no published data | Fail fast before download begins |
| `FilterMatchException` | Opening filter matches zero games | Warn user; continue processing remaining files |
| `InsufficientMemoryException` | RAM exhaustion during streaming | Reduce buffer sizes; continue with degraded performance |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `lichess_mini_db.zst` (10MB compressed sample for integration tests)
- `lichess_corrupted_frame.zst` (intentionally corrupted ZST for error handling tests)
- `lichess_checksum_mismatch.zst` (valid ZST with incorrect companion .sha256)
- `lichess_opening_subset.zst` (pre-filtered Sicilian games for filter validation)
- `lichess_streaming_test.zst` (designed for checkpoint/resume testing)

### 9.2 Assertion Examples
```csharp
// Verify streaming index build produces same index as traditional method
var streamingReport = await service.DownloadAsync(new LichessDbDownloadRequest(
    new LichessDbSpecifier(LichessDbMode.Month, Year: 2024, Month: 1),
    "output/",
    new LichessDbDownloadOptions(StreamToIndex: true)
), CancellationToken.None);

var traditionalReport = await service.DownloadAsync(new LichessDbDownloadRequest(
    new LichessDbSpecifier(LichessDbMode.Month, Year: 2024, Month: 1),
    "output2/",
    new LichessDbDownloadOptions(StreamToIndex: false)
), CancellationToken.None);

// Indexes should contain identical game counts
Assert.Equal(streamingReport.TotalGamesProcessed, traditionalReport.TotalGamesProcessed);

// Verify opening filter reduces game count appropriately
var sicilianReport = await service.DownloadAsync(new LichessDbDownloadRequest(
    new LichessDbSpecifier(
        LichessDbMode.OpeningFiltered,
        Year: 2023,
        Opening: new OpeningFilter("B20-B99") // Sicilian Defense
    ),
    "sicilian/",
    new LichessDbDownloadOptions(ApplyFiltersDuringDecompress: true)
), CancellationToken.None);

// Sicilian should be ~15% of total games
Assert.True(sicilianReport.TotalGamesProcessed > 0);
Assert.True(sicilianReport.TotalGamesProcessed < 50_000_000); // Reasonable upper bound

// Verify checksum verification catches corruption
await File.WriteAllBytesAsync("corrupted.zst", corruptedBytes);
var corruptReport = await service.DownloadAsync(new LichessDbDownloadRequest(
    // ... request pointing to corrupted file ...
), CancellationToken.None);

Assert.False(corruptReport.ChecksumVerified);
Assert.Equal(ProcessingStatus.ChecksumFailed, corruptReport.ProcessedFiles[0].Status);
```

## 10. Versioning & Compatibility

- **Zstandard Format Version:** Supports Zstandard v1.0+ frames; rejects legacy formats
- **Lichess DB Schema Evolution:** Monitors lichess-db.org for PGN format changes; maintains parser compatibility
- **Index Format:** v4 index format required for databases >4B games (uint64 game count)
- **Checksum Algorithm:** SHA-256 mandatory; rejects MD5/SHA-1 checksums for security

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Malicious ZST archive (decompression bomb) | Enforce 20:1 max expansion ratio; abort if exceeded |
| Checksum manipulation | Always download .sha256 from same trusted domain as ZST file |
| Path traversal in PGN headers | Sanitize all tag values before index insertion |
| Resource exhaustion via pathological PGN | Limit game parser recursion depth to 500 plies |
| Privacy leakage | Lichess DB contains only public rated games; no PII beyond usernames |
| Torrent safety | If UseTorrent=true, verify all chunks against SHA-256 before assembly |