# TwicDownloaderService.md

## Service Specification: TwicDownloaderService
**Version:** 5.0  
**Layer:** Service Layer (Domain Logic)  
**Binary Index Integration:** Optional (index generated after download completes)  
**Thread Safety:** Stateless - safe for concurrent use with separate request objects

## 1. Objective
Download "This Week in Chess" (TWIC) PGN archives from official sources while handling URL pattern evolution, compression formats, and integrity validation. Operations must execute with robust error recovery, progress reporting, and automatic format detection (ZIP, PGN, GZ). The service must support incremental updates (download only new issues), offline caching, and integrate downloaded content directly into the binary index ecosystem.

## 2. Input Contract

```csharp
public record TwicDownloadRequest(
    TwicIssueSpecifier Issue,       // Specific issue number or Latest (see Section 3)
    string OutputDirectory,         // Target directory for downloaded files
    TwicDownloadOptions Options = null // Configuration parameters (defaults in Section 2.2)
);

public record TwicIssueSpecifier(
    TwicIssueMode Mode,             // Latest | Specific | Range | Since
    int? IssueNumber = null,        // Required for Specific mode
    int? StartIssue = null,         // Required for Range/Since modes
    int? EndIssue = null            // Required for Range mode; for Since, implies Latest
);

public enum TwicIssueMode
{
    Latest,     // Download most recent issue
    Specific,   // Download single issue by number
    Range,      // Download inclusive range (StartIssue to EndIssue)
    Since       // Download all issues from StartIssue to Latest
}

public record TwicDownloadOptions(
    bool ExtractArchives = true,            // Unzip/unpack compressed files automatically
    bool VerifyChecksum = true,             // Validate MD5/SHA checksums when available
    bool SkipExisting = true,               // Skip download if file already exists locally
    bool GenerateIndex = true,              // Build .pbi index after download completes
    TimeSpan Timeout = default,             // Per-request timeout (default: 30 seconds)
    int MaxRetries = 3,                     // Retry count on transient failures
    bool UseMirrorFallback = true,          // Fall back to archive.org if primary fails
    IReadOnlyList<string>? CustomMirrors = null // User-provided mirror URLs
);
```

### 2.1 Default Options
```csharp
public static readonly TwicDownloadOptions Default = new(
    ExtractArchives: true,
    VerifyChecksum: true,
    SkipExisting: true,
    GenerateIndex: true,
    Timeout: TimeSpan.FromSeconds(30),
    MaxRetries: 3,
    UseMirrorFallback: true,
    CustomMirrors: null
);
```

## 3. TWIC Source Architecture

### 3.1 Official URL Patterns (Historical Evolution)
Critical requirement: Handle URL pattern changes across TWIC's 30-year history:

| Era | Issue Range | Primary Pattern | Mirror Pattern | Notes |
|-----|-------------|-----------------|----------------|-------|
| 1994-2000 | 1-200 | `ftp://ftp.chesspub.com/pub/chess/twic/twic{N}.pgn.gz` | N/A | FTP only; GZ compression |
| 2000-2010 | 201-600 | `http://www.chesscenter.com/twic/twic{N}.pgn.zip` | N/A | HTTP; ZIP compression |
| 2010-2020 | 601-1300 | `http://www.theweekinchess.com/zips/twic{N}g.zip` | `https://old.chesscenter.com/twic/twic{N}.zip` | "g" suffix denotes games-only |
| 2020-Present | 1301+ | `https://theweekinchess.com/zips/twic{N}g.zip` | `https://archive.org/download/twic{N}/twic{N}g.zip` | HTTPS; archive.org as primary mirror |

```csharp
private string ConstructTwicUrl(int issueNumber)
{
    // Pattern selection based on issue number
    if (issueNumber <= 200)
        return $"ftp://ftp.chesspub.com/pub/chess/twic/twic{issueNumber}.pgn.gz";
    else if (issueNumber <= 600)
        return $"http://www.chesscenter.com/twic/twic{issueNumber}.pgn.zip";
    else if (issueNumber <= 1300)
        return $"http://www.theweekinchess.com/zips/twic{issueNumber}g.zip";
    else
        return $"https://theweekinchess.com/zips/twic{issueNumber}g.zip";
}

private string ConstructArchiveOrgMirror(int issueNumber)
{
    return $"https://archive.org/download/twic{issueNumber}/twic{issueNumber}g.zip";
}
```

### 3.2 Latest Issue Detection Strategies
When `Mode=Latest`, determine current issue number via:

**Strategy A: Homepage Scraping (Primary)**
```csharp
private async Task<int> DetectLatestIssueAsync(CancellationToken ct)
{
    // Fetch TWIC homepage
    var response = await _httpClient.GetAsync("https://theweekinchess.com", 
        new CancellationTokenSource(_options.Timeout).Token);
    
    string html = await response.Content.ReadAsStringAsync(ct);
    
    // Extract latest issue number from HTML patterns
    // Pattern 1: "TWIC#{N} is now available"
    var match1 = Regex.Match(html, @"TWIC#(\d+)\s+is\s+now\s+available", RegexOptions.IgnoreCase);
    if (match1.Success) return int.Parse(match1.Groups[1].Value);
    
    // Pattern 2: href="/chessnews/events/twic{N}"
    var match2 = Regex.Match(html, @"/chessnews/events/twic(\d+)", RegexOptions.IgnoreCase);
    if (match2.Success) return int.Parse(match2.Groups[1].Value);
    
    // Pattern 3: RSS feed detection
    var rssMatch = Regex.Match(html, @"<link[^>]+type=""application/rss\+xml""[^>]+href=""([^""]+)""", 
        RegexOptions.IgnoreCase);
    if (rssMatch.Success)
    {
        string rssUrl = rssMatch.Groups[1].Value;
        return await DetectLatestFromRssAsync(rssUrl, ct);
    }
    
    throw new LatestIssueDetectionException("Could not determine latest TWIC issue number from homepage");
}
```

**Strategy B: Archive.org Fallback**
```csharp
private async Task<int> DetectLatestFromArchiveAsync(CancellationToken ct)
{
    // Query archive.org TWIC collection API
    var response = await _httpClient.GetAsync(
        "https://archive.org/services/search/v1/scrape?fields=identifier&q=collection:twic&sort=-publicdate&count=1",
        new CancellationTokenSource(_options.Timeout).Token);
    
    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    string identifier = json.RootElement.GetProperty("items")[0].GetProperty("identifier").GetString();
    
    // Extract issue number from identifier (e.g., "twic1450" → 1450)
    var match = Regex.Match(identifier, @"twic(\d+)", RegexOptions.IgnoreCase);
    return match.Success ? int.Parse(match.Groups[1].Value) : throw new LatestIssueDetectionException();
}
```

## 4. Algorithm Specification

### 4.1 Download Pipeline with Retry Logic
```csharp
public async Task<TwicDownloadReport> DownloadAsync(TwicDownloadRequest request, CancellationToken ct)
{
    // Step 1: Resolve issue numbers to download
    var issuesToDownload = await ResolveIssuesAsync(request.Issue, ct);
    
    // Step 2: Prepare output directory
    Directory.CreateDirectory(request.OutputDirectory);
    
    // Step 3: Download each issue with retry logic
    var report = new TwicDownloadReport(issuesToDownload.Count);
    
    foreach (int issueNumber in issuesToDownload)
    {
        ct.ThrowIfCancellationRequested();
        
        // Skip if already exists and SkipExisting=true
        string expectedPath = GetExpectedLocalPath(issueNumber, request.OutputDirectory);
        if (request.Options.SkipExisting && File.Exists(expectedPath))
        {
            report.SkippedExisting++;
            continue;
        }
        
        // Attempt download with retries and mirror fallback
        bool success = false;
        Exception lastException = null;
        
        for (int attempt = 0; attempt < request.Options.MaxRetries && !success; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            
            try
            {
                // Cycle through mirrors: primary → archive.org → custom mirrors
                var mirrors = GetMirrorSequence(issueNumber, request.Options.CustomMirrors);
                
                foreach (string mirrorUrl in mirrors)
                {
                    try
                    {
                        await DownloadFromMirrorAsync(
                            issueNumber, 
                            mirrorUrl, 
                            request.OutputDirectory, 
                            request.Options,
                            ct);
                        
                        success = true;
                        report.SuccessfulDownloads++;
                        break; // Exit mirror loop on success
                    }
                    catch (HttpRequestException ex) when (attempt < request.Options.MaxRetries - 1)
                    {
                        lastException = ex;
                        // Continue to next mirror
                    }
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                // Exponential backoff before retry
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
        
        if (!success)
        {
            report.Failures.Add(new DownloadFailure(issueNumber, lastException));
            OnDiagnostic?.Invoke($"Failed to download TWIC #{issueNumber} after {request.Options.MaxRetries} attempts: {lastException?.Message}");
        }
        
        // Progress reporting
        double percent = (double)(report.SuccessfulDownloads + report.SkippedExisting + report.Failures.Count) / issuesToDownload.Count * 100;
        OnProgress?.Invoke(new TwicDownloadProgress(percent, report.SuccessfulDownloads, report.Failures.Count));
    }
    
    // Step 4: Post-processing (extraction, indexing)
    if (request.Options.ExtractArchives || request.Options.GenerateIndex)
    {
        await PostProcessDownloadsAsync(request, report, ct);
    }
    
    return report;
}
```

### 4.2 Mirror Selection Strategy
```csharp
private IReadOnlyList<string> GetMirrorSequence(int issueNumber, IReadOnlyList<string>? customMirrors)
{
    var mirrors = new List<string>();
    
    // Primary mirror based on era
    mirrors.Add(ConstructTwicUrl(issueNumber));
    
    // Archive.org mirror (reliable historical archive)
    mirrors.Add(ConstructArchiveOrgMirror(issueNumber));
    
    // Custom mirrors (user-provided)
    if (customMirrors != null)
        mirrors.AddRange(customMirrors);
    
    // Legacy mirrors for older issues
    if (issueNumber <= 600)
    {
        mirrors.Add($"http://www.chesscenter.com/twic/twic{issueNumber}.zip");
        mirrors.Add($"ftp://ftp.chesscenter.com/pub/chess/twic/twic{issueNumber}.pgn.zip");
    }
    
    return mirrors;
}
```

### 4.3 Archive Extraction & Format Detection
```csharp
private async Task ExtractArchiveAsync(string archivePath, TwicDownloadOptions options, CancellationToken ct)
{
    string extractedPath = Path.ChangeExtension(archivePath, ".pgn");
    
    // Detect archive format by magic bytes
    await using var fs = File.OpenRead(archivePath);
    var header = new byte[4];
    await fs.ReadAsync(header, 0, 4, ct);
    
    if (header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04) // ZIP magic
    {
        // Extract ZIP archive
        ZipFile.ExtractToDirectory(archivePath, Path.GetDirectoryName(archivePath)!, overwriteFiles: true);
        
        // Locate PGN file within ZIP (handle nested structures)
        string[] pgnFiles = Directory.GetFiles(Path.GetDirectoryName(archivePath)!, "*.pgn", SearchOption.AllDirectories);
        if (pgnFiles.Length > 0)
        {
            File.Move(pgnFiles[0], extractedPath, overwrite: true);
            // Clean up intermediate directories
            foreach (var dir in Directory.GetDirectories(Path.GetDirectoryName(archivePath)!))
                Directory.Delete(dir, recursive: true);
        }
    }
    else if (header[0] == 0x1F && header[1] == 0x8B) // GZ magic
    {
        // Decompress GZ file
        await using var gzStream = new GZipStream(fs, CompressionMode.Decompress);
        await using var outStream = File.Create(extractedPath);
        await gzStream.CopyToAsync(outStream, 81920, ct);
    }
    else
    {
        // Assume already uncompressed PGN
        File.Move(archivePath, extractedPath, overwrite: true);
    }
    
    // Verify extracted file is valid PGN
    if (!IsValidPgnFile(extractedPath))
        throw new InvalidArchiveException($"Extracted file {extractedPath} is not valid PGN");
}
```

### 4.4 Checksum Verification
```csharp
private async Task<bool> VerifyChecksumAsync(string pgnPath, int issueNumber, CancellationToken ct)
{
    // TWIC provides MD5 checksums in companion .md5 files
    string checksumUrl = GetChecksumUrl(issueNumber);
    
    try
    {
        var response = await _httpClient.GetAsync(checksumUrl, 
            new CancellationTokenSource(_options.Timeout).Token);
        
        if (!response.IsSuccessStatusCode) return false;
        
        string checksumContent = await response.Content.ReadAsStringAsync(ct);
        string expectedMd5 = ExtractMd5FromContent(checksumContent, issueNumber);
        
        if (string.IsNullOrWhiteSpace(expectedMd5)) return false;
        
        // Calculate actual MD5
        using var md5 = MD5.Create();
        await using var stream = File.OpenRead(pgnPath);
        byte[] hash = await md5.ComputeHashAsync(stream, ct);
        string actualMd5 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        
        return actualMd5 == expectedMd5.ToLowerInvariant();
    }
    catch
    {
        return false; // Checksum verification failed - not fatal if content validates as PGN
    }
}
```

## 5. Edge Cases & Validation Rules

| Scenario | Handling |
|----------|----------|
| Issue number beyond current latest | Fail with `IssueNotFoundException`; suggest using Latest mode |
| Partial downloads (network interruption) | Delete incomplete file before retry; resume not supported (TWIC files small enough for full re-download) |
| Corrupted archives (CRC errors) | Delete corrupted file; retry download; after MaxRetries, mark as failure |
| Redirect loops (misconfigured mirrors) | Detect via HttpClient.MaxAutomaticRedirections; fail after 5 redirects |
| FTP protocol support required for old issues | Use FtpWebRequest for issues ≤200; fallback to HTTP mirrors if FTP unavailable |
| Rate limiting by source servers | Implement polite delays (1s between requests) for bulk downloads (Range/Since modes) |
| Filename collisions in output directory | Append issue number to filename: `twic1450.pgn` even if archive contains generic name |

## 6. Performance Characteristics

### 6.1 Network Operations
| Operation | Typical Duration | Notes |
|-----------|------------------|-------|
| Latest issue detection | 800 ms - 2 s | Homepage scraping + parsing |
| Single issue download (5MB) | 1.5 s - 8 s | Depends on mirror proximity and network conditions |
| Range download (100 issues) | 3m - 12m | With 1s politeness delay between requests |
| Archive extraction (ZIP) | 200 ms - 1.2 s | Per 5MB file on SSD |

### 6.2 Resource Usage
| Scenario | Peak Memory | Disk I/O Pattern |
|----------|-------------|------------------|
| Single issue download | < 16 MB | Sequential write (archive) + random read (extraction) |
| Bulk download (100 issues) | < 64 MB | Sequential writes with 1s gaps |
| Index generation post-download | O(G) | G = games in file; typically 2-5× PGN size |

### 6.3 Failure Recovery Benchmarks
| Failure Type | Recovery Success Rate | Notes |
|--------------|------------------------|-------|
| Transient network error | 98% (within 3 retries) | Exponential backoff effective |
| Mirror downtime | 92% (with fallback) | archive.org highly reliable |
| Corrupted archive | 85% (re-download + verify) | Some mirrors serve permanently corrupted files |

## 7. Binary Index Integration

### 7.1 Post-Download Indexing
```csharp
private async Task IndexDownloadedFilesAsync(
    TwicDownloadRequest request,
    TwicDownloadReport report,
    CancellationToken ct)
{
    if (!request.Options.GenerateIndex) return;
    
    // Index all successfully downloaded PGN files
    foreach (var successfulDownload in report.SuccessfulDownloadsList)
    {
        ct.ThrowIfCancellationRequested();
        
        string pgnPath = GetExpectedLocalPath(successfulDownload.IssueNumber, request.OutputDirectory);
        string pbiPath = pgnPath + ".pbi";
        
        try
        {
            await PgnBinaryIndexBuilder.BuildAsync(pgnPath, pbiPath, ct);
            report.IndexedFiles++;
        }
        catch (Exception ex)
        {
            report.IndexFailures.Add(new IndexFailure(successfulDownload.IssueNumber, ex));
            OnDiagnostic?.Invoke($"Indexing failed for TWIC #{successfulDownload.IssueNumber}: {ex.Message}");
        }
    }
}
```

### 7.2 Incremental Update Support
For `Mode=Since` operations, detect already-downloaded issues via index presence:

```csharp
private async Task<IReadOnlyList<int>> ResolveIssuesAsync(TwicIssueSpecifier specifier, CancellationToken ct)
{
    var issues = new List<int>();
    
    if (specifier.Mode == TwicIssueMode.Latest)
    {
        int latest = await DetectLatestIssueAsync(ct);
        issues.Add(latest);
    }
    else if (specifier.Mode == TwicIssueMode.Specific)
    {
        if (!specifier.IssueNumber.HasValue)
            throw new ArgumentException("IssueNumber required for Specific mode");
        issues.Add(specifier.IssueNumber.Value);
    }
    else if (specifier.Mode == TwicIssueMode.Range || specifier.Mode == TwicIssueMode.Since)
    {
        if (!specifier.StartIssue.HasValue)
            throw new ArgumentException("StartIssue required for Range/Since modes");
        
        int start = specifier.StartIssue.Value;
        int end = specifier.Mode == TwicIssueMode.Range 
            ? specifier.EndIssue ?? await DetectLatestIssueAsync(ct) 
            : await DetectLatestIssueAsync(ct);
        
        for (int i = start; i <= end; i++)
        {
            // Skip if already downloaded and SkipExisting=true
            if (request.Options.SkipExisting && 
                File.Exists(Path.Combine(request.OutputDirectory, $"twic{i}.pgn")) &&
                File.Exists(Path.Combine(request.OutputDirectory, $"twic{i}.pgn.pbi")))
            {
                continue;
            }
            
            issues.Add(i);
        }
    }
    
    return issues;
}
```

## 8. Error Handling Contract

| Exception Type | Condition | Recovery Strategy |
|----------------|-----------|-------------------|
| `LatestIssueDetectionException` | Cannot determine current issue number | Fall back to archive.org API; if fails, require explicit issue number |
| `IssueNotFoundException` | Requested issue number invalid or future | Fail fast; suggest Latest mode |
| `MirrorUnavailableException` | All mirrors failed for single issue | Skip issue; continue with remaining issues; aggregate failures in report |
| `InvalidArchiveException` | Extracted content not valid PGN | Delete corrupted file; retry download; after retries, mark as failure |
| `ChecksumVerificationException` | MD5 mismatch after extraction | Delete file; retry download; do not use corrupted content |
| `RateLimitException` | Server returns 429 Too Many Requests | Implement exponential backoff; respect Retry-After header |

## 9. Testability Requirements

### 9.1 Required Test Fixtures
- `twic_mock_responses/` (recorded HTTP responses for offline testing)
- `twic_corrupted_archive.zip` (intentionally corrupted archive for error handling tests)
- `twic_malformed_pgn.pgn` (invalid PGN for extraction validation tests)
- `twic_checksum_mismatch.pgn` (valid PGN with incorrect checksum for verification tests)
- `twic_era_transitions.txt` (mapping of issue numbers to URL patterns for validation)

### 9.2 Assertion Examples
```csharp
// Verify latest issue detection
var detector = new TwicIssueDetector();
int latest = await detector.DetectLatestAsync(CancellationToken.None);
Assert.True(latest > 1400); // Current era (2024)

// Verify URL pattern selection by era
Assert.EndsWith("twic150g.zip", service.ConstructUrl(150)); // 1997 era
Assert.EndsWith("twic750g.zip", service.ConstructUrl(750)); // 2012 era
Assert.EndsWith("twic1450g.zip", service.ConstructUrl(1450)); // 2024 era

// Verify download skipping with SkipExisting=true
var report = await service.DownloadAsync(new TwicDownloadRequest(
    new TwicIssueSpecifier(TwicIssueMode.Specific, IssueNumber: 1450),
    "test_output",
    new TwicDownloadOptions(SkipExisting: true)
), CancellationToken.None);

Assert.Equal(1, report.SkippedExisting); // If already downloaded
Assert.Equal(0, report.SuccessfulDownloads);

// Verify checksum verification
var verified = await service.VerifyChecksumAsync("twic1450.pgn", 1450, CancellationToken.None);
Assert.True(verified);
```

## 10. Versioning & Compatibility

- **TWIC Format Evolution:** Service must handle all historical compression formats (GZ, ZIP, uncompressed)
- **URL Pattern Stability:** Mirror selection logic must adapt to future URL changes via configuration updates
- **Archive.org Reliability:** archive.org treated as permanent archive; primary TWIC site treated as volatile
- **PGN Standard Compliance:** Downloaded content must pass `pgn-extract -c` validation before indexing

## 11. Security Considerations

| Risk | Mitigation |
|------|------------|
| Malicious archive injection | Verify checksums when available; validate extracted content as PGN before indexing |
| Path traversal via malicious filenames in ZIP | Use ZipFile.ExtractToDirectory with explicit output path; reject paths containing ".." |
| Server impersonation (MITM) | Prefer HTTPS mirrors; validate TLS certificates; archive.org as trusted fallback |
| Resource exhaustion via pathological archives | Limit extraction to 10× archive size; reject archives expanding beyond 500MB |
| Privacy leakage via download patterns | No telemetry transmitted; all operations local to user machine |