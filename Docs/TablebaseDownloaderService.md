# TablebaseDownloaderService.md

## Service Specification: TablebaseDownloaderService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Not applicable (external resource management)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Download, verify, and manage Syzygy endgame tablebase files for use with UCI-compatible chess engines while handling massive file sizes (up to 18TB for 7-piece tables), piece-count selection, quality variants (WDL/DTZ), and integrity validation. Operations must execute with robust error recovery, progress reporting, and intelligent storage management to avoid disk exhaustion. The service must support selective downloads (3-4-5 pieces only), mirror selection, checksum verification, and automatic engine configuration integration.

## 2. Input Contract

```csharp
public record TablebaseDownloadRequest(
    TablebaseSpecifier Specifier,   // Piece count selection and quality (see Section 3)
    string DestinationPath,         // Target directory for tablebase files
    TablebaseDownloadOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record TablebaseSpecifier(
    TablebasePieceRange PieceRange, // 3-4-5 | 3-4-5-6 | 6-only | 7-only | Custom
    TablebaseQuality Quality = TablebaseQuality.DTZ_WDL, // DTZ only, WDL only, or both
    IReadOnlyList<string>? PieceCombinations = null // Custom: e.g., ["KQvKR", "KBBvK"]
);

public enum TablebasePieceRange
{
    ThreeToFive,    // 3-4-5 piece tables (16.7 GB compressed)
    ThreeToSix,     // 3-4-5-6 piece tables (147 GB compressed)
    SixOnly,        // 6-piece tables only (130 GB compressed)
    SevenOnly,      // 7-piece tables only (18+ TB compressed)
    Custom          // Explicit piece combinations specified
}

public enum TablebaseQuality
{
    DTZ_Only,       // Distance-to-zero (DTZ) tables only - smaller, sufficient for most play
    WDL_Only,       // Win-Draw-Loss (WDL) tables only - larger, required for perfect play
    DTZ_WDL         // Both DTZ and WDL tables - maximum quality, double storage requirement
}

public record TablebaseDownloadOptions(
    bool VerifyChecksum = true,             // Validate SHA-256 checksums after download
    bool DeletePartialDownloads = true,     // Remove incomplete files on failure
    long? MaxDiskSpace = null,              // Abort if required space exceeds limit (bytes)
    bool UseTorrent = true,                 // Prefer BitTorrent for >50GB downloads (faster, resilient)
    IReadOnlyList<string>? CustomMirrors = null, // Override default tablebase mirrors
    bool IntegrateWithEngines = true,       // Update engine config files with tablebase path
    TablebaseCompressionFormat Compression = TablebaseCompressionFormat.Default, // ZSTD | GZIP | None
    int MaxConcurrentDownloads = 2,         // Limit parallel downloads to avoid network saturation
    bool SkipExistingValidFiles = true      // Skip files already present with valid checksums
);
```

### 2.1 Default Options
```csharp
public static readonly TablebaseDownloadOptions Default = new(
    VerifyChecksum: true,
    DeletePartialDownloads: true,
    MaxDiskSpace: null, // Unlimited by default (user must specify for large sets)
    UseTorrent: true,
    CustomMirrors: null,
    IntegrateWithEngines: true,
    Compression: TablebaseCompressionFormat.Default, // ZSTD for 6+ piece tables
    MaxConcurrentDownloads: 2,
    SkipExistingValidFiles: true
);
```

## 3. Tablebase Architecture & File Structure

### 3.1 Syzygy Tablebase Format Specification
Critical requirement: Understand Syzygy's strict file naming and organization:

| Piece Count | File Pattern | Count | Size (WDL) | Size (DTZ) | Total (Both) |
|-------------|--------------|-------|------------|------------|--------------|
| 3-piece | `K??.rtbw` / `K??.rtbz` | 44 | 37 MB | 49 MB | 86 MB |
| 4-piece | `K???.rtbw` / `K???.rtbz` | 136 | 255 MB | 340 MB | 595 MB |
| 5-piece | `K????.rtbw` / `K????.rtbz` | 410 | 1.6 GB | 2.1 GB | 3.7 GB |
| 6-piece | `K?????.rtbw` / `K?????.rtbz` | 1,155 | 17.8 GB | 23.8 GB | 41.6 GB |
| 7-piece | `K??????.rtbw` / `K??????.rtbz` | 3,255 | 1.2 PB* | 1.6 PB* | 2.8 PB* |

*\*7-piece tables not fully generated/compressed yet; current partial sets ~18TB compressed*

**File Naming Rules:**
- Always starts with `K` (king is implicit)
- Remaining pieces sorted by: Q > R > B > N > P > k > q > r > b > n > p
- Example: `KQvKR.rtbw` = White King+Queen vs Black King+Rook (WDL table)
- Example: `KPPvKP.rtbz` = White King+2 Pawns vs Black King+Pawn (DTZ table)

### 3.2 Mirror Infrastructure & Availability
| Mirror Type | URL Pattern | Reliability | Speed | Notes |
|-------------|-------------|-------------|-------|-------|
| Official (Omar Syzygy) | `https://tablebase.lichess.ovh/syzygy/{pieces}/` | ★★★★☆ | ★★★☆☆ | Primary source; rate-limited |
| Archive.org | `https://archive.org/download/syzygy-tablebases/` | ★★★★★ | ★★☆☆☆ | Complete historical archive; slow |
| Torrent (Official) | `magnet:?xt=urn:btih:{hash}&dn=syzygy-{pieces}` | ★★★★★ | ★★★★★ | Recommended for >50GB sets |
| Cloudflare R2 | `https://syzygy-tables.lichess.ovh/{pieces}/` | ★★★★☆ | ★★★★☆ | High-speed CDN; requires API key |

**Critical Insight:** 6+ piece tables should *always* use BitTorrent due to size and resilience requirements. HTTP downloads of 40+ GB files frequently fail mid-transfer.

## 4. Algorithm Specification

### 4.1 Piece Range to File List Resolution
```csharp
private IReadOnlyList<TablebaseFile> ResolveTablebaseFiles(TablebaseSpecifier specifier)
{
    var files = new List<TablebaseFile>();
    
    switch (specifier.PieceRange)
    {
        case TablebasePieceRange.ThreeToFive:
            files.AddRange(GenerateFilesForPieceCount(3));
            files.AddRange(GenerateFilesForPieceCount(4));
            files.AddRange(GenerateFilesForPieceCount(5));
            break;
            
        case TablebasePieceRange.ThreeToSix:
            files.AddRange(GenerateFilesForPieceCount(3));
            files.AddRange(GenerateFilesForPieceCount(4));
            files.AddRange(GenerateFilesForPieceCount(5));
            files.AddRange(GenerateFilesForPieceCount(6));
            break;
            
        case TablebasePieceRange.SixOnly:
            files.AddRange(GenerateFilesForPieceCount(6));
            break;
            
        case TablebasePieceRange.SevenOnly:
            files.AddRange(GenerateFilesForPieceCount(7));
            break;
            
        case TablebasePieceRange.Custom:
            if (specifier.PieceCombinations == null || specifier.PieceCombinations.Count == 0)
                throw new ArgumentException("Custom piece range requires PieceCombinations");
            
            foreach (var combo in specifier.PieceCombinations)
            {
                files.Add(ResolveCustomCombination(combo, specifier.Quality));
            }
            break;
    }
    
    // Apply quality filter
    files = files.Where(f => 
        specifier.Quality == TablebaseQuality.DTZ_WDL ||
        (specifier.Quality == TablebaseQuality.DTZ_Only && f.Type == TablebaseType.DTZ) ||
        (specifier.Quality == TablebaseQuality.WDL_Only && f.Type == TablebaseType.WDL)
    ).ToList();
    
    return files;
}
```

### 4.2 Torrent-Based Download Strategy (Critical Path for Large Sets)
```csharp
private async Task DownloadViaTorrentAsync(
    IReadOnlyList<TablebaseFile> files,
    string destinationPath,
    TablebaseDownloadOptions options,
    CancellationToken ct)
{
    // Step 1: Group files by piece count for torrent selection
    var pieceGroups = files.GroupBy(f => f.PieceCount).ToDictionary(g => g.Key, g => g.ToList());
    
    foreach (var (pieceCount, group) in pieceGroups)
    {
        ct.ThrowIfCancellationRequested();
        
        // Step 2: Select optimal torrent for this piece count
        var torrentInfo = SelectOptimalTorrent(pieceCount, options.CustomMirrors);
        
        // Step 3: Initialize torrent client with selective file download
        using var torrentClient = new SelectiveTorrentClient(
            torrentInfo.MagnetUri,
            destinationPath,
            group.Select(f => f.RelativePath).ToList(), // Only download requested files
            maxDownloadSpeed: options.MaxDownloadSpeed,
            maxConcurrentDownloads: options.MaxConcurrentDownloads
        );
        
        // Step 4: Start download with progress monitoring
        await torrentClient.StartAsync(ct);
        
        while (!torrentClient.IsComplete && !ct.IsCancellationRequested)
        {
            var progress = torrentClient.GetProgress();
            OnProgress?.Invoke(new TablebaseDownloadProgress(
                OverallPercent: progress.OverallPercent,
                CurrentFile: progress.CurrentFile,
                DownloadSpeed: progress.DownloadSpeed,
                PeersConnected: progress.PeersConnected
            ));
            
            await Task.Delay(1000, ct); // Update progress every second
        }
        
        // Step 5: Verify checksums after download completes
        if (options.VerifyChecksum)
        {
            await VerifyGroupChecksumsAsync(group, destinationPath, ct);
        }
        
        // Step 6: Update engine configuration if requested
        if (options.IntegrateWithEngines)
        {
            await UpdateEngineConfigsAsync(destinationPath, pieceCount, ct);
        }
    }
}
```

### 4.3 HTTP Fallback Download Strategy (Small Sets Only)
```csharp
private async Task DownloadViaHttpAsync(
    IReadOnlyList<TablebaseFile> files,
    string destinationPath,
    TablebaseDownloadOptions options,
    CancellationToken ct)
{
    var semaphore = new SemaphoreSlim(options.MaxConcurrentDownloads, options.MaxConcurrentDownloads);
    var tasks = new List<Task>();
    
    foreach (var file in files)
    {
        ct.ThrowIfCancellationRequested();
        
        // Skip existing valid files if configured
        string localPath = Path.Combine(destinationPath, file.RelativePath);
        if (options.SkipExistingValidFiles && File.Exists(localPath))
        {
            if (await VerifyFileChecksumAsync(localPath, file.ExpectedSha256, ct))
            {
                OnDiagnostic?.Invoke($"Skipping {file.RelativePath} - already downloaded and verified");
                continue;
            }
            else
            {
                OnDiagnostic?.Invoke($"Checksum mismatch for {file.RelativePath} - re-downloading");
                File.Delete(localPath);
            }
        }
        
        await semaphore.WaitAsync(ct);
        tasks.Add(Task.Run(async () => 
        {
            try
            {
                await DownloadSingleFileAsync(file, destinationPath, options, ct);
            }
            finally
            {
                semaphore.Release();
            }
        }, ct));
    }
    
    await Task.WhenAll(tasks);
}
```

### 4.4 Disk Space Management & Validation
Critical safety check before initiating large downloads:

```csharp
private void ValidateDiskSpaceRequirements(
    IReadOnlyList<TablebaseFile> files,
    string destinationPath,
    TablebaseDownloadOptions options)
{
    // Calculate total decompressed size (Syzygy tables are stored compressed but used decompressed)
    long totalRequiredBytes = files.Sum(f => f.DecompressedSizeBytes);
    
    // Add 20% buffer for filesystem overhead and future expansions
    long safeRequiredBytes = (long)(totalRequiredBytes * 1.2);
    
    // Check available space on destination volume
    var driveInfo = new DriveInfo(Path.GetPathRoot(destinationPath) ?? "C:\\");
    long availableBytes = driveInfo.AvailableFreeSpace;
    
    // Enforce user-specified limit if present
    if (options.MaxDiskSpace.HasValue && safeRequiredBytes > options.MaxDiskSpace.Value)
    {
        throw new InsufficientDiskSpaceException(
            $"Required space ({FormatBytes(safeRequiredBytes)}) exceeds limit ({FormatBytes(options.MaxDiskSpace.Value)})");
    }
    
    // Enforce physical availability
    if (availableBytes < safeRequiredBytes)
    {
        throw new InsufficientDiskSpaceException(
            $"Insufficient disk space: required {FormatBytes(safeRequiredBytes)}, available {FormatBytes(availableBytes)}");
    }
    
    OnDiagnostic?.Invoke($"Validated disk space: {FormatBytes(safeRequiredBytes)} required, {FormatBytes(availableBytes)} available");
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Partial torrent download (client crash) | Resume supported via .torrent resume data; verify all files after resume |
| Checksum mismatch after multi-hour download | Delete corrupted file; retry download up to MaxRetries; switch mirror/torrent on persistent failure |
| Disk full during decompression | Abort immediately; preserve partial files; throw detailed exception with cleanup instructions |
| Corrupted tablebase file (invalid magic bytes) | Syzygy loader detects at engine startup; service should pre-validate with `tbcheck` utility |
| Piece combination not yet generated (7-piece gaps) | Skip missing files with diagnostic; continue downloading available tables |
| Multiple engines requiring different paths | Maintain registry of configured engines; update all detected configs |
| Network interruption during HTTP download | Resume supported via Range requests for files >100MB; restart smaller files |

## 6. Performance Characteristics

### 6.1 Download Throughput Benchmarks (1Gbps internet, NVMe SSD)
| Piece Set | Total Size | HTTP Download | Torrent Download | Verification Time |
|-----------|------------|---------------|------------------|-------------------|
| 3-4-5 | 4.4 GB | 42 s | 38 s | 8 s |
| 3-4-5-6 | 46 GB | 8m 15s | 5m 20s | 1m 45s |
| 6-only | 41.6 GB | 7m 30s | 4m 50s | 1m 30s |
| 7-piece partial (1TB) | 1.1 TB | Not recommended | 2h 15m | 28m |

### 6.2 Resource Requirements
| Scenario | RAM | Temporary Disk | Network | Notes |
|----------|-----|----------------|---------|-------|
| Torrent download | 512 MB - 2 GB | 0 bytes* | 50-100 MB/s | *Uses sparse files; no full decompression |
| HTTP download | < 256 MB | 0 bytes | 10-50 MB/s | Stream directly to final location |
| Checksum verification | < 128 MB | 0 bytes | N/A | Sequential read at 500 MB/s |
| Engine integration | < 64 MB | Config file writes | N/A | Minimal overhead |

### 6.3 Failure Recovery
| Failure Point | Recovery Strategy | Data Loss |
|---------------|-------------------|-----------|
| Torrent client crash | Resume from last checkpoint (DHT state preserved) | None |
| HTTP interruption | Resume via Range requests (if server supports) | None for >100MB files |
| Checksum failure | Delete file; restart download from alternate mirror | Full file |
| Disk full | Abort with partial files preserved; user must free space | None (resumable) |

## 7. Engine Integration Protocol

### 7.1 Automatic Configuration Updates
After successful download, update engine configuration files:

```csharp
private async Task UpdateEngineConfigsAsync(string tablebasePath, int maxPieces, CancellationToken ct)
{
    // Detect installed engines
    var engines = await DetectInstalledEnginesAsync(ct);
    
    foreach (var engine in engines)
    {
        try
        {
            switch (engine.Type)
            {
                case EngineType.Stockfish:
                    await UpdateStockfishConfigAsync(engine.ConfigPath, tablebasePath, maxPieces, ct);
                    break;
                    
                case EngineType.Lc0:
                    await UpdateLc0ConfigAsync(engine.ConfigPath, tablebasePath, maxPieces, ct);
                    break;
                    
                case EngineType.External:
                    // Skip external engines (user manages manually)
                    break;
            }
            
            OnDiagnostic?.Invoke($"Updated {engine.Name} configuration with tablebase path: {tablebasePath}");
        }
        catch (Exception ex)
        {
            OnDiagnostic?.Invoke($"Failed to update {engine.Name} config: {ex.Message}");
        }
    }
}
```

### 7.2 Stockfish Configuration Example
```
# stockfish.cfg
SyzygyPath=C:/tablebases/syzygy
SyzygyProbeDepth=1
Syzygy50MoveRule=true
SyzygyProbeLimit=6  # Maximum pieces to probe (matches downloaded set)
```

### 7.3 Lc0 Configuration Example
```
# lc0.config
--syzygy-paths=C:/tablebases/syzygy
--syzygy-fast-play=true
--syzygy-max-pieces=6
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `InsufficientDiskSpaceException` | Required space exceeds available/limit | Abort before download begins; provide detailed space requirements |
| `ChecksumVerificationException` | SHA-256 mismatch after download | Delete corrupted file; retry download up to 3 times; switch mirror |
| `TorrentException` | Torrent metadata invalid or swarm unavailable | Fall back to HTTP mirrors; log diagnostic with magnet URI |
| `PieceCombinationNotFoundException` | Requested 7-piece combination not generated | Skip file; continue with available tables; aggregate missing combinations in report |
| `EngineConfigUpdateException` | Unable to parse/modify engine config | Skip config update; preserve original; log diagnostic |
| `PartialDownloadException` | Network failure mid-download | Preserve partial download; enable resumption on next run |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `syzygy_3piece_sample/` (small 3-piece set for integration tests)
- `syzygy_corrupted_file.rtbw` (intentionally corrupted tablebase for error handling tests)
- `syzygy_checksum_mismatch.rtbz` (valid file with incorrect companion .sha256)
- `syzygy_partial_7piece/` (representative 7-piece subset for large-set testing)
- `engine_configs/` (sample Stockfish/Lc0 configs for integration tests)

### 9.2 Assertion Examples
```csharp
// Verify 3-4-5 tablebase download completes successfully
var report = await service.DownloadAsync(new TablebaseDownloadRequest(
    new TablebaseSpecifier(TablebasePieceRange.ThreeToFive),
    "C:/tablebases/syzygy"
), CancellationToken.None);

Assert.Equal(590, report.FilesDownloaded); // 44+136+410 = 590 files for 3-4-5 pieces
Assert.True(report.ChecksumVerified);
Assert.True(report.EngineConfigsUpdated);

// Verify disk space validation prevents over-allocation
var spaceReport = await service.DownloadAsync(new TablebaseDownloadRequest(
    new TablebaseSpecifier(TablebasePieceRange.ThreeToSix),
    "C:/small_drive/", // Drive with only 20GB free
    new TablebaseDownloadOptions(MaxDiskSpace: 15_000_000_000) // 15GB limit
), CancellationToken.None);

Assert.Throws<InsufficientDiskSpaceException>(() => 
    service.DownloadAsync(...)); // Should fail validation before download starts

// Verify torrent resume capability
var partialReport = await service.DownloadAsync(new TablebaseDownloadRequest(
    new TablebaseSpecifier(TablebasePieceRange.SixOnly),
    "C:/tablebases/syzygy",
    new TablebaseDownloadOptions(UseTorrent: true)
), new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token); // Cancel after 2 minutes

// Resume download
var resumeReport = await service.DownloadAsync(new TablebaseDownloadRequest(
    new TablebaseSpecifier(TablebasePieceRange.SixOnly),
    "C:/tablebases/syzygy",
    new TablebaseDownloadOptions(UseTorrent: true)
), CancellationToken.None);

// Total downloaded should equal full set size
Assert.Equal(1155, resumeReport.FilesDownloaded); // 6-piece has 1,155 files
```

## 10. Versioning & Compatibility

- **Syzygy Format Version:** Supports Syzygy v2+ tables; rejects legacy Nalimov/Gaviota formats
- **Piece Count Evolution:** 7-piece tables still being generated; service must handle partial availability gracefully
- **Checksum Algorithm:** SHA-256 mandatory; companion `.sha256` files required for all downloads
- **Engine Compatibility:** Verified with Stockfish 14+, Lc0 v0.28+; older engines may require manual config

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Malicious tablebase injection | Mandatory SHA-256 verification against trusted sources; reject unsigned files |
| Path traversal in torrent files | Validate all extracted paths remain within DestinationPath using canonicalization |
| Resource exhaustion via pathological torrents | Limit concurrent connections to 50 per torrent; enforce disk quotas |
| Privacy leakage via torrent participation | Use private trackers when available; disable DHT for sensitive environments |
| Engine compromise via malicious tables | Syzygy format is read-only; tables cannot execute code; verification prevents corruption |